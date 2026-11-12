using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace WarpSwitchNext
{
    sealed class ProxyConfig
    {
        public int Flags { get; set; }
        public string Server { get; set; } = "";
        public string Bypass { get; set; } = "";
        public string AutoConfigUrl { get; set; } = "";
    }

    /// <summary>
    /// Reads and writes the Windows (WinINet) proxy for the current user: the same settings as
    /// Settings → Network &amp; Internet → Proxy, used by Edge, Chrome and most desktop apps.
    /// </summary>
    static class WinInet
    {
        public const int Direct = 1, Proxy = 2, AutoProxyUrl = 4, AutoDetect = 8;
        const int OptFlags = 1, OptServer = 2, OptBypass = 3, OptAutoConfig = 4, OptFlagsUi = 10;
        const int PerConnectionOption = 75, SettingsChanged = 39, Refresh = 37;

        [StructLayout(LayoutKind.Sequential)]
        struct OptionList
        {
            public int dwSize;
            public IntPtr pszConnection;
            public int dwOptionCount;
            public int dwOptionError;
            public IntPtr pOptions;
        }

        // union { DWORD dwValue; LPWSTR pszValue; FILETIME ftValue; } — 8 bytes on both 32- and 64-bit.
        [StructLayout(LayoutKind.Explicit)]
        struct OptionValue
        {
            [FieldOffset(0)] public int dwValue;
            [FieldOffset(0)] public IntPtr pszValue;
            [FieldOffset(4)] public int high;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct Option
        {
            public int dwOption;
            public OptionValue Value;
        }

        [DllImport("wininet.dll", EntryPoint = "InternetQueryOptionW", SetLastError = true)]
        static extern bool InternetQueryOption(IntPtr handle, int option, IntPtr buffer, ref int length);

        [DllImport("wininet.dll", EntryPoint = "InternetSetOptionW", SetLastError = true)]
        static extern bool InternetSetOption(IntPtr handle, int option, IntPtr buffer, int length);

        [DllImport("kernel32.dll")]
        static extern IntPtr GlobalFree(IntPtr handle);

        public static ProxyConfig Query()
        {
            // Windows asks callers to read the connection type with FLAGS_UI and fall back to FLAGS.
            try { return Query(OptFlagsUi); }
            catch (Win32Exception) { return Query(OptFlags); }
        }

        static ProxyConfig Query(int flagsOption)
        {
            int[] ids = { flagsOption, OptServer, OptBypass, OptAutoConfig };
            int optionSize = Marshal.SizeOf(typeof(Option));
            int listSize = Marshal.SizeOf(typeof(OptionList));
            IntPtr options = Marshal.AllocHGlobal(optionSize * ids.Length);
            IntPtr list = Marshal.AllocHGlobal(listSize);
            try
            {
                for (int i = 0; i < ids.Length; i++)
                    Marshal.StructureToPtr(new Option { dwOption = ids[i] }, options + i * optionSize, false);
                Marshal.StructureToPtr(new OptionList { dwSize = listSize, dwOptionCount = ids.Length, pOptions = options }, list, false);
                int size = listSize;
                if (!InternetQueryOption(IntPtr.Zero, PerConnectionOption, list, ref size))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取系统代理设置");
                var result = new ProxyConfig();
                for (int i = 0; i < ids.Length; i++)
                {
                    var o = (Option)Marshal.PtrToStructure(options + i * optionSize, typeof(Option));
                    if (i == 0) { result.Flags = o.Value.dwValue; continue; }
                    string text = "";
                    if (o.Value.pszValue != IntPtr.Zero)
                    {
                        text = Marshal.PtrToStringUni(o.Value.pszValue) ?? "";
                        GlobalFree(o.Value.pszValue);
                    }
                    if (ids[i] == OptServer) result.Server = text;
                    else if (ids[i] == OptBypass) result.Bypass = text;
                    else result.AutoConfigUrl = text;
                }
                return result;
            }
            finally
            {
                Marshal.FreeHGlobal(options);
                Marshal.FreeHGlobal(list);
            }
        }

        public static void Apply(ProxyConfig config)
        {
            var strings = new List<IntPtr>();
            IntPtr Str(string s)
            {
                if (string.IsNullOrEmpty(s)) return IntPtr.Zero;
                var p = Marshal.StringToHGlobalUni(s); strings.Add(p); return p;
            }
            var items = new[]
            {
                new Option { dwOption = OptFlags, Value = new OptionValue { dwValue = config.Flags } },
                new Option { dwOption = OptServer, Value = new OptionValue { pszValue = Str(config.Server) } },
                new Option { dwOption = OptBypass, Value = new OptionValue { pszValue = Str(config.Bypass) } },
                new Option { dwOption = OptAutoConfig, Value = new OptionValue { pszValue = Str(config.AutoConfigUrl) } },
            };
            int optionSize = Marshal.SizeOf(typeof(Option));
            int listSize = Marshal.SizeOf(typeof(OptionList));
            IntPtr options = Marshal.AllocHGlobal(optionSize * items.Length);
            IntPtr list = Marshal.AllocHGlobal(listSize);
            try
            {
                for (int i = 0; i < items.Length; i++)
                    Marshal.StructureToPtr(items[i], options + i * optionSize, false);
                Marshal.StructureToPtr(new OptionList { dwSize = listSize, dwOptionCount = items.Length, pOptions = options }, list, false);
                if (!InternetSetOption(IntPtr.Zero, PerConnectionOption, list, listSize))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "无法修改系统代理设置");
                InternetSetOption(IntPtr.Zero, SettingsChanged, IntPtr.Zero, 0);
                InternetSetOption(IntPtr.Zero, Refresh, IntPtr.Zero, 0);
            }
            finally
            {
                Marshal.FreeHGlobal(options);
                Marshal.FreeHGlobal(list);
                foreach (var p in strings) Marshal.FreeHGlobal(p);
            }
        }
    }

    sealed class ProxySnapshot
    {
        public int Flags { get; set; }
        public string Server { get; set; }
        public string Bypass { get; set; }
        public string AutoConfigUrl { get; set; }
        public string AppliedBypass { get; set; }
    }

    /// <summary>
    /// Points the system proxy at 127.0.0.1:7891 and puts back exactly what was there before.
    /// The previous settings are saved to disk first, so a crash or power loss can be undone on next start.
    /// </summary>
    sealed class SystemProxy
    {
        static readonly object Gate = new object();
        static readonly string[] LocalBypass =
            new[] { "localhost", "127.*", "10.*" }
            .Concat(Enumerable.Range(16, 16).Select(n => "172." + n + ".*"))
            .Concat(new[] { "192.168.*", "169.254.*", "<local>" })
            .ToArray();

        readonly string path;

        public SystemProxy(string dataDirectory)
        {
            path = Path.Combine(dataDirectory, "proxy-snapshot.json");
        }

        public static bool IsOurs(string server) =>
            string.Equals((server ?? "").Trim(), Ports.ProxyAddress, StringComparison.OrdinalIgnoreCase);

        static bool UsesProxy(ProxyConfig c) => (c.Flags & WinInet.Proxy) != 0 && !string.IsNullOrWhiteSpace(c.Server);

        public static string MergeBypass(string existing)
        {
            var items = (existing ?? "").Split(';').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            foreach (var local in LocalBypass)
                if (!items.Any(i => string.Equals(i, local, StringComparison.OrdinalIgnoreCase))) items.Add(local);
            return string.Join(";", items);
        }

        public void Enable()
        {
            lock (Gate)
            {
                if (File.Exists(path)) RestoreLocked();
                var current = WinInet.Query();
                if (UsesProxy(current) && !IsOurs(current.Server))
                    throw new UserError("系统正在使用另一个代理（" + current.Server + "），请先关闭其他代理软件再连接。");
                // Our own proxy left behind without a record: the original was a direct connection.
                var original = UsesProxy(current) ? new ProxyConfig { Flags = WinInet.Direct, Bypass = current.Bypass, AutoConfigUrl = current.AutoConfigUrl } : current;
                var bypass = MergeBypass(original.Bypass);
                Json.Write(path, new ProxySnapshot
                {
                    Flags = original.Flags, Server = original.Server, Bypass = original.Bypass,
                    AutoConfigUrl = original.AutoConfigUrl, AppliedBypass = bypass,
                });
                try
                {
                    // Automatic detection and setup scripts take priority over a manual proxy on Windows,
                    // so they are paused while connected and restored afterwards.
                    WinInet.Apply(new ProxyConfig
                    {
                        Flags = WinInet.Direct | WinInet.Proxy,
                        Server = Ports.ProxyAddress,
                        Bypass = bypass,
                        AutoConfigUrl = original.AutoConfigUrl,
                    });
                    var check = WinInet.Query();
                    if (!UsesProxy(check) || !IsOurs(check.Server))
                        throw new UserError("系统代理没有生效，可能被安全软件或单位策略拦截。");
                }
                catch
                {
                    try { RestoreLocked(); } catch { }
                    throw;
                }
            }
        }

        public void Restore()
        {
            lock (Gate) RestoreLocked();
        }

        void RestoreLocked()
        {
            if (!File.Exists(path)) return;
            var saved = Json.Read<ProxySnapshot>(path);
            if (saved == null)
                throw new UserError("代理恢复记录无法读取，请在「设置 → 网络和 Internet → 代理」中关闭 127.0.0.1:7891。");
            var current = WinInet.Query();
            // Never overwrite a proxy the user changed after this app connected.
            if (UsesProxy(current) && IsOurs(current.Server))
            {
                WinInet.Apply(new ProxyConfig
                {
                    Flags = saved.Flags == 0 ? WinInet.Direct : saved.Flags,
                    Server = saved.Server,
                    Bypass = saved.Bypass,
                    AutoConfigUrl = saved.AutoConfigUrl,
                });
                var after = WinInet.Query();
                if (UsesProxy(after) && IsOurs(after.Server))
                    throw new UserError("系统代理未能恢复，请在「设置 → 网络和 Internet → 代理」中手动关闭 127.0.0.1:7891。");
            }
            File.Delete(path);
        }
    }
}
