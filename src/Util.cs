using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace WarpSwitchNext
{
    /// <summary>An error whose message is meant for the user, shown as-is in the window.</summary>
    sealed class UserError : Exception
    {
        public UserError(string message) : base(message) { }
    }

    static class Ports
    {
        public const int Proxy = 7891;
        public const int Relay = 18443;
        public const string ProxyAddress = "127.0.0.1:7891";
    }

    static class Paths
    {
        public static string Data
        {
            get
            {
                var overridden = Environment.GetEnvironmentVariable("WARPSWITCH_DATA");
                if (!string.IsNullOrEmpty(overridden)) return overridden;
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WarpSwitchNext");
            }
        }

        public static string AppDirectory => AppDomain.CurrentDomain.BaseDirectory;

        /// <summary>The usque build that matches this PC: ARM64, x64, or 32-bit Windows.</summary>
        public static string Usque
        {
            get
            {
                var core = Path.Combine(AppDirectory, "core");
                string arch = !Environment.Is64BitOperatingSystem ? "386" : NativeArm64() ? "arm64" : "amd64";
                var best = Path.Combine(core, "usque-" + arch + ".exe");
                if (File.Exists(best)) return best;
                // x64 builds also run on ARM64 Windows through emulation.
                return Path.Combine(core, "usque-amd64.exe");
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool IsWow64Process2(IntPtr process, out ushort processMachine, out ushort nativeMachine);

        static bool NativeArm64()
        {
            try
            {
                return IsWow64Process2(Process.GetCurrentProcess().Handle, out _, out ushort native) && native == 0xAA64;
            }
            catch (EntryPointNotFoundException) { return false; } // Windows 10 before 1709
        }
    }

    static class Util
    {
        static readonly Regex Domain = new Regex(
            @"^(?=.{1,253}$)[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?(?:\.[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?)+$",
            RegexOptions.CultureInvariant);

        public static bool ValidDomain(string s) => s != null && s.Length <= 253 && Domain.IsMatch(s);

        public static string Quote(string arg) => "\"" + arg.Replace("\"", "\\\"") + "\"";

        public static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        public static void OpenFolder(string path)
        {
            try { Directory.CreateDirectory(path); Process.Start("explorer.exe", Quote(path)); } catch { }
        }

        public static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        }

        /// <summary>True when some real network adapter is up and has a gateway (Wi-Fi joined, cable plugged in).</summary>
        public static bool Online()
        {
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback || nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                    var gateways = nic.GetIPProperties().GatewayAddresses;
                    if (gateways.Any(g => g.Address != null && !g.Address.Equals(IPAddress.Any) && !g.Address.Equals(IPAddress.IPv6Any)))
                        return true;
                }
                return false;
            }
            catch { return NetworkInterface.GetIsNetworkAvailable(); }
        }

        /// <summary>True when something on this PC is already listening on the TCP port.</summary>
        public static bool PortBusy(int port)
        {
            try
            {
                return IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(e => e.Port == port);
            }
            catch { return false; }
        }

        [DllImport("kernel32.dll")]
        static extern bool QueryUnbiasedInterruptTime(out ulong time);

        /// <summary>Seconds the PC has been awake since boot; does not advance while asleep.</summary>
        public static double AwakeSeconds()
        {
            try { if (QueryUnbiasedInterruptTime(out ulong t)) return t / 10_000_000.0; } catch { }
            return Environment.TickCount / 1000.0;
        }
    }

    static class Json
    {
        static JavaScriptSerializer Serializer => new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        public static T Read<T>(string path) where T : class
        {
            try { return File.Exists(path) ? Serializer.Deserialize<T>(File.ReadAllText(path, Encoding.UTF8)) : null; }
            catch { return null; }
        }

        public static Dictionary<string, object> ReadObject(string path)
        {
            try { return File.Exists(path) ? Serializer.DeserializeObject(File.ReadAllText(path, Encoding.UTF8)) as Dictionary<string, object> : null; }
            catch { return null; }
        }

        public static void Write(string path, object value)
        {
            var temp = path + ".tmp";
            File.WriteAllText(temp, Serializer.Serialize(value), new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
        }
    }

    sealed class Settings
    {
        public string Sni { get; set; } = "www.bing.com";
        public string Mode { get; set; } = "auto";
        public bool AutoConnect { get; set; }
        public bool TrayTipShown { get; set; }
    }

    sealed class CachedRoute
    {
        public string Endpoint { get; set; }
        public string Sni { get; set; }
        public bool Fragmented { get; set; }
    }

    static class Account
    {
        static readonly string[] Required = { "private_key", "id", "access_token", "endpoint_pub_key", "ipv4", "ipv6" };

        public static bool Valid(string path)
        {
            var values = Json.ReadObject(path);
            return values != null && Required.All(k => values.TryGetValue(k, out var v) && v is string s && s.Length > 0);
        }

        public static Dictionary<string, object> Load(string path)
        {
            var values = Json.ReadObject(path);
            if (values == null) return null;
            if (!(values.TryGetValue("private_key", out var key) && key is string) ||
                !(values.TryGetValue("endpoint_pub_key", out var pub) && pub is string)) return null;
            return values;
        }
    }

    /// <summary>A small append-only log with a size cap, safe to call from any thread.</summary>
    sealed class LogFile
    {
        readonly string path;
        readonly object gate = new object();

        public LogFile(string path, bool truncate = false, long cap = 2_000_000)
        {
            this.path = path;
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                if (truncate || (File.Exists(path) && new FileInfo(path).Length > cap)) File.WriteAllText(path, "");
            }
            catch { }
        }

        public string FilePath => path;

        public void Line(string text)
        {
            if (text == null) return;
            lock (gate)
            {
                try { File.AppendAllText(path, "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + text + "\r\n", Encoding.UTF8); }
                catch { }
            }
        }
    }

    /// <summary>Child processes (usque) are put in a job that Windows kills when this app exits for any reason.</summary>
    static class ChildJob
    {
        [StructLayout(LayoutKind.Sequential)]
        struct BasicLimit
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct IoCounters { public ulong R, W, O, RB, WB, OB; }

        [StructLayout(LayoutKind.Sequential)]
        struct ExtendedLimit
        {
            public BasicLimit Basic;
            public IoCounters Io;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll")]
        static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);
        [DllImport("kernel32.dll")]
        static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        static IntPtr job = IntPtr.Zero;
        static readonly object gate = new object();

        public static void Add(Process process)
        {
            try
            {
                lock (gate)
                {
                    if (job == IntPtr.Zero)
                    {
                        var handle = CreateJobObject(IntPtr.Zero, null);
                        var info = new ExtendedLimit();
                        info.Basic.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                        int size = Marshal.SizeOf(typeof(ExtendedLimit));
                        var ptr = Marshal.AllocHGlobal(size);
                        try
                        {
                            Marshal.StructureToPtr(info, ptr, false);
                            if (!SetInformationJobObject(handle, 9, ptr, (uint)size)) return; // JobObjectExtendedLimitInformation
                        }
                        finally { Marshal.FreeHGlobal(ptr); }
                        job = handle;
                    }
                    AssignProcessToJobObject(job, process.Handle);
                }
            }
            catch { }
        }
    }

    static class AutoStart
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ValueName = "WARP 新开关";

        static string Command => Util.Quote(System.Windows.Forms.Application.ExecutablePath) + " --autostart";

        public static void Set(bool on)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) key.SetValue(ValueName, Command);
                else key.DeleteValue(ValueName, false);
            }
        }

        /// <summary>Keep the entry pointing at this copy of the app (it may have been moved or updated).</summary>
        public static void Sync(bool on)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
                {
                    var current = key?.GetValue(ValueName) as string;
                    if (on && current != Command) Set(true);
                    if (!on && current != null) Set(false);
                }
            }
            catch { }
        }
    }
}
