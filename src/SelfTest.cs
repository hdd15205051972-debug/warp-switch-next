using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace WarpSwitchNext
{
    /// <summary>Checks run by the build on a real Windows machine.</summary>
    static class SelfTest
    {
        sealed class Report
        {
            readonly string file;
            public int Failures;
            public Report(string file) { this.file = file; if (file != null) File.WriteAllText(file, ""); }
            public void Line(string text)
            {
                Console.WriteLine(text);
                if (file != null) File.AppendAllText(file, text + Environment.NewLine);
            }
            public void Check(bool ok, string what)
            {
                Line((ok ? "ok    " : "FAIL  ") + what);
                if (!ok) Failures++;
            }
            public int Done(string summary)
            {
                Line(Failures == 0 ? "PASS: " + summary : "FAILED: " + Failures + " check(s)");
                return Failures == 0 ? 0 : 1;
            }
        }

        // ---------- Unit checks: no network, no system changes ----------

        public static int Units(string outFile)
        {
            var r = new Report(outFile);
            try
            {
                var body = new byte[512];
                body[0] = 1;
                for (int i = 1; i < body.Length; i++) body[i] = (byte)((i - 1) % 256);
                var input = new List<byte> { 22, 3, 1, 2, 0 };
                input.AddRange(body);
                var trailer = new byte[] { 20, 3, 3, 0, 1, 1 };
                input.AddRange(trailer);
                var bytes = input.ToArray();
                foreach (var size in new[] { 1, 16, 48, 256, 1024 })
                {
                    var parts = ClientHello.Split(bytes, bytes.Length, size);
                    var rebuilt = new List<byte>();
                    bool headers = true;
                    foreach (var part in parts.Take(parts.Count - 1))
                    {
                        headers &= part[0] == 22 && part[1] == 3 && part[2] == 1 && part[3] * 256 + part[4] == part.Length - 5;
                        rebuilt.AddRange(part.Skip(5));
                    }
                    r.Check(headers && rebuilt.SequenceEqual(body) && parts.Last().SequenceEqual(trailer), "ClientHello split into " + size + "-byte records and reassembled");
                }
                r.Check(ClientHello.Split(bytes, 4) == null, "incomplete header waits for more data");
                r.Check(ClientHello.Split(bytes, 100) == null, "incomplete record waits for more data");
                var other = new byte[] { 23, 3, 3, 0, 1, 42 };
                var passthrough = ClientHello.Split(other, other.Length);
                r.Check(passthrough.Count == 1 && passthrough[0].SequenceEqual(other), "non-handshake data passes through unchanged");

                r.Check(Util.ValidDomain("www.bing.com") && Util.ValidDomain("dock.example.edu"), "valid domains accepted");
                r.Check(!Util.ValidDomain("evil; command") && !Util.ValidDomain("https://example.com") && !Util.ValidDomain("bing") && !Util.ValidDomain(""), "invalid domains rejected");

                var merged = SystemProxy.MergeBypass("*.corp.example;LOCALHOST");
                r.Check(merged.StartsWith("*.corp.example;LOCALHOST") && merged.Contains("192.168.*") && merged.Contains("172.31.*") && merged.Contains("<local>")
                        && merged.Split(';').Count(x => x.Equals("localhost", StringComparison.OrdinalIgnoreCase)) == 1, "proxy bypass keeps existing rules and adds local networks");
                r.Check(SystemProxy.IsOurs(" 127.0.0.1:7891 ") && !SystemProxy.IsOurs("127.0.0.1:8080") && !SystemProxy.IsOurs(null), "own proxy address recognised");

                var temp = Path.Combine(Path.GetTempPath(), "warp-switch-test-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(temp);
                try
                {
                    var settingsPath = Path.Combine(temp, "settings.json");
                    Json.Write(settingsPath, new Settings { Sni = "dock.example.edu", Mode = "compatible", AutoConnect = true });
                    var back = Json.Read<Settings>(settingsPath);
                    r.Check(back != null && back.Sni == "dock.example.edu" && back.Mode == "compatible" && back.AutoConnect, "settings saved and read back");
                    File.WriteAllText(settingsPath, "{\"Sni\":\"www.bing.com\"}");
                    back = Json.Read<Settings>(settingsPath);
                    r.Check(back != null && back.Mode == "auto" && !back.AutoConnect, "older settings files still load with defaults");
                    var accountPath = Path.Combine(temp, "account.json");
                    File.WriteAllText(accountPath, "{\"private_key\":\"k\",\"id\":\"i\",\"access_token\":\"t\",\"endpoint_pub_key\":\"p\",\"ipv4\":\"172.16.0.2\",\"ipv6\":\"fd01::2\",\"license\":\"x\",\"n\":3}");
                    r.Check(Account.Valid(accountPath) && Account.Load(accountPath) != null, "complete account accepted");
                    File.WriteAllText(accountPath, "{\"private_key\":\"k\"}");
                    r.Check(!Account.Valid(accountPath), "incomplete account rejected");
                }
                finally { try { Directory.Delete(temp, true); } catch { } }

                r.Line("info  usque for this PC: " + Path.GetFileName(Paths.Usque) + ", 64-bit process: " + Environment.Is64BitProcess);
            }
            catch (Exception e) { r.Check(false, "unexpected error: " + e); }
            return r.Done("relay record splitting, domain checks, proxy rules, settings and account files");
        }

        // ---------- System proxy: changes and restores the Windows proxy (build machine only) ----------

        static string RegistryProxy()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings"))
                return "ProxyEnable=" + key?.GetValue("ProxyEnable") + " ProxyServer=" + key?.GetValue("ProxyServer");
        }

        static string Describe(ProxyConfig c) => "flags=" + c.Flags + " server='" + c.Server + "' bypass='" + c.Bypass + "' pac='" + c.AutoConfigUrl + "'";

        public static int Proxy(string outFile)
        {
            var r = new Report(outFile);
            var dir = Path.Combine(Path.GetTempPath(), "warp-switch-proxy-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var original = WinInet.Query();
            r.Line("info  machine proxy before test: " + Describe(original) + " | " + RegistryProxy());
            const int mask = WinInet.Direct | WinInet.Proxy | WinInet.AutoProxyUrl | WinInet.AutoDetect;
            try
            {
                var proxy = new SystemProxy(dir);
                // A typical laptop: automatic detection on, plus an old manual proxy that is switched off.
                var start = new ProxyConfig { Flags = WinInet.Direct | WinInet.AutoDetect, Server = "10.0.0.1:8080", Bypass = "*.corp.example", AutoConfigUrl = "http://wpad.example/proxy.pac" };
                WinInet.Apply(start);
                var seen = WinInet.Query();
                r.Check((seen.Flags & mask) == start.Flags && seen.Server == start.Server, "test settings applied: " + Describe(seen));

                proxy.Enable();
                var on = WinInet.Query();
                r.Check((on.Flags & mask) == (WinInet.Direct | WinInet.Proxy), "connected: manual proxy only (auto-detect and script paused): flags=" + on.Flags);
                r.Check(on.Server == Ports.ProxyAddress, "connected: proxy is " + Ports.ProxyAddress);
                r.Check(on.Bypass.StartsWith("*.corp.example") && on.Bypass.Contains("192.168.*"), "connected: bypass keeps old rules and adds local networks");
                var reg = RegistryProxy();
                r.Check(reg.Contains("ProxyEnable=1") && reg.Contains("ProxyServer=" + Ports.ProxyAddress), "connected: registry shows the proxy for other apps (" + reg + ")");
                r.Check(WebRequest.GetSystemWebProxy().GetProxy(new Uri("https://example.com/")).Authority == Ports.ProxyAddress, "connected: .NET system proxy resolves to " + Ports.ProxyAddress);
                r.Check(File.Exists(Path.Combine(dir, "proxy-snapshot.json")), "connected: previous settings saved to disk");

                proxy.Restore();
                var off = WinInet.Query();
                r.Check((off.Flags & mask) == start.Flags && off.Server == start.Server && off.Bypass == start.Bypass && off.AutoConfigUrl == start.AutoConfigUrl,
                        "disconnected: every previous setting restored: " + Describe(off));
                r.Check(!File.Exists(Path.Combine(dir, "proxy-snapshot.json")), "disconnected: saved copy removed");

                // Another proxy program is active: refuse, change nothing.
                var foreign = new ProxyConfig { Flags = WinInet.Direct | WinInet.Proxy, Server = "10.9.9.9:3128", Bypass = "<local>" };
                WinInet.Apply(foreign);
                bool refused = false;
                try { proxy.Enable(); } catch (UserError e) { refused = true; r.Line("info  refusal message: " + e.Message); }
                var still = WinInet.Query();
                r.Check(refused && still.Server == foreign.Server && !File.Exists(Path.Combine(dir, "proxy-snapshot.json")), "another active proxy is left alone");

                // The user changed the proxy while connected: restoring must not overwrite their choice.
                WinInet.Apply(start);
                proxy.Enable();
                var chosen = new ProxyConfig { Flags = WinInet.Direct | WinInet.Proxy, Server = "10.8.8.8:8888", Bypass = "" };
                WinInet.Apply(chosen);
                proxy.Restore();
                var kept = WinInet.Query();
                r.Check(kept.Server == chosen.Server && (kept.Flags & WinInet.Proxy) != 0, "a proxy the user picked while connected is kept");

                // App crashed while connected: the next start puts things back.
                WinInet.Apply(start);
                proxy.Enable();
                new SystemProxy(dir).Restore();
                var recovered = WinInet.Query();
                r.Check((recovered.Flags & mask) == start.Flags && recovered.Server == start.Server, "after a crash, the next start restores the proxy");
            }
            catch (Exception e) { r.Check(false, "unexpected error: " + e); }
            finally
            {
                try { WinInet.Apply(original); } catch { }
                try { Directory.Delete(dir, true); } catch { }
                r.Line("info  machine proxy after test: " + Describe(WinInet.Query()));
            }
            return r.Done("system proxy set, verified and restored");
        }

        // ---------- Real connection: registers a WARP account and connects (build machine only) ----------

        public static int Connect(string dataDir, string outFile)
        {
            var r = new Report(outFile);
            dataDir = dataDir ?? Path.Combine(Path.GetTempPath(), "warp-switch-connect-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dataDir);
            var usque = Paths.Usque;
            r.Line("info  data: " + dataDir);
            r.Line("info  usque: " + usque);
            try
            {
                var account = Path.Combine(dataDir, "account.json");
                if (!Account.Valid(account))
                {
                    var pending = Path.Combine(dataDir, "account.pending.json");
                    var p = Process.Start(new ProcessStartInfo(usque, "-c " + Util.Quote(pending) + " register -a")
                    {
                        UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = dataDir,
                    });
                    var output = p.StandardOutput.ReadToEndAsync();
                    var errors = p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(60000)) { p.Kill(); }
                    r.Line("info  register output: " + (output.Result + errors.Result).Replace("\n", " | ").Trim());
                    bool ok = p.ExitCode == 0 && Account.Valid(pending);
                    r.Check(ok, "registered a new WARP account with usque.exe");
                    if (!ok) return r.Done("");
                    File.Move(pending, account);
                }

                foreach (var mode in new[] { "fragment", "compatible" })
                {
                    var w = new ConnectionWorker(dataDir, usque, "www.bing.com", mode, fresh: false, testOnly: true);
                    StateInfo reached = null;
                    w.StateChanged += s => { r.Line("      [" + s.Phase + "] " + s.Message); if (s.Phase == "on") reached = s; };
                    int code = w.Run();
                    // A test run always ends with 已关闭; what matters is that it reached 已连接 on the way.
                    r.Check(code == 0 && reached != null && reached.Fragmented == (mode == "fragment") && reached.Ip != "—",
                            (mode == "fragment" ? "握手分片" : "兼容模式") + " reached WARP: entry " + reached?.Endpoint + ", exit IP " + reached?.Ip + ", node " + reached?.Colo);
                }

                // Full run, as the app does it: system proxy on, real traffic, then off again.
                var before = WinInet.Query();
                var worker = new ConnectionWorker(dataDir, usque, "www.bing.com", "auto", fresh: false, testOnly: false);
                worker.StateChanged += s => r.Line("      [" + s.Phase + "] " + s.Message);
                int exit = -1;
                var thread = new Thread(() => exit = worker.Run()) { IsBackground = true };
                thread.Start();
                var deadline = DateTime.UtcNow.AddMinutes(4);
                while (DateTime.UtcNow < deadline && thread.IsAlive && worker.Snapshot().Phase != "on") Thread.Sleep(500);
                bool connected = worker.Snapshot().Phase == "on";
                r.Check(connected, "app connection reached 已连接");
                if (connected)
                {
                    var reg = RegistryProxy();
                    r.Check(reg.Contains("ProxyServer=" + Ports.ProxyAddress), "system proxy set while connected (" + reg + ")");
                    var trace = ConnectionWorker.Trace(WebRequest.GetSystemWebProxy());
                    r.Check(trace.TryGetValue("warp", out var warp) && (warp == "on" || warp == "plus"),
                            "a normal HTTPS request through the system proxy arrives via WARP (warp=" + (warp ?? "?") + ", ip=" + (trace.TryGetValue("ip", out var ip) ? ip : "?") + ")");
                }
                worker.Cancel();
                r.Check(thread.Join(30000), "closing finished within 30 seconds");
                var after = WinInet.Query();
                const int mask = WinInet.Direct | WinInet.Proxy | WinInet.AutoProxyUrl | WinInet.AutoDetect;
                r.Check((after.Flags & mask) == (before.Flags & mask) && after.Server == before.Server && !SystemProxy.IsOurs(after.Server),
                        "system proxy restored after closing: " + Describe(after));
            }
            catch (Exception e) { r.Check(false, "unexpected error: " + e); }
            return r.Done("registered, connected in both modes, used the system proxy and restored it");
        }

        // ---------- Screenshots of the window in each state ----------

        public static int Screenshots(string folder, string outFile)
        {
            var r = new Report(outFile);
            folder = folder ?? "screenshots";
            Directory.CreateDirectory(folder);
            var data = Path.Combine(Path.GetTempPath(), "warp-switch-ui-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(data);
            try
            {
                var controller = new AppController(data, Paths.Usque);
                using (var form = new MainForm(controller))
                {
                    form.StartPosition = FormStartPosition.Manual;
                    form.Location = new Point(40, 40);
                    form.ShowInTaskbar = false;
                    form.Show();
                    Application.DoEvents();
                    var scenes = new (string name, StateInfo state, bool setup, bool working, bool auto)[]
                    {
                        ("1-first-run", new StateInfo(), true, false, false),
                        ("2-registering", new StateInfo { Phase = "registering", Message = "正在创建你的独立免费 WARP 账号…" }, true, true, false),
                        ("3-connecting", new StateInfo { Phase = "connecting", Message = "正在尝试入口 2/8 · 握手分片", Endpoint = "162.159.198.4", Sni = "www.bing.com" }, false, true, true),
                        ("4-connected", new StateInfo { Phase = "on", Message = "已连接 · 浏览器及使用系统代理的程序可用", Endpoint = "162.159.198.4", Sni = "www.bing.com", Ip = "104.28.203.89", Colo = "IAD", Fragmented = true }, false, true, true),
                        ("5-error", new StateInfo { Phase = "error", Message = "这些入口暂时无法通过验证。可换一个伪装域名或点“换入口重试”。" }, false, false, true),
                        ("6-off", new StateInfo { Message = "已关闭，系统代理已恢复" }, false, false, false),
                    };
                    foreach (var scene in scenes)
                    {
                        controller.Preview(scene.state, scene.setup, scene.working, scene.auto);
                        form.Refresh();
                        Application.DoEvents();
                        using (var bmp = new Bitmap(form.Width, form.Height))
                        {
                            form.DrawToBitmap(bmp, new Rectangle(Point.Empty, form.Size));
                            var file = Path.Combine(folder, scene.name + ".png");
                            bmp.Save(file, ImageFormat.Png);
                            r.Check(File.Exists(file), "saved " + file + " (" + form.Width + "x" + form.Height + ")");
                        }
                    }
                    using (var tray = new Bitmap(4 * 40, 40))
                    using (var g = Graphics.FromImage(tray))
                    {
                        g.Clear(Color.FromArgb(32, 32, 32));
                        int x = 4;
                        foreach (var phase in new[] { "off", "connecting", "on", "error" })
                        {
                            g.DrawIcon(Ui.TrayIcon(phase), new Rectangle(x, 4, 32, 32));
                            x += 40;
                        }
                        tray.Save(Path.Combine(folder, "7-tray-icons.png"), ImageFormat.Png);
                    }
                    form.Hide();
                }
            }
            catch (Exception e) { r.Check(false, "unexpected error: " + e); }
            finally { try { Directory.Delete(data, true); } catch { } }
            return r.Done("window rendered in every state");
        }
    }
}
