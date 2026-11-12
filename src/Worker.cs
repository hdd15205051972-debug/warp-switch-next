using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Cache;
using System.Threading;

namespace WarpSwitchNext
{
    sealed class StateInfo
    {
        public string Phase = "off";
        public string Message = "点击开启以连接";
        public string Endpoint = "—";
        public string Sni = "—";
        public string Ip = "—";
        public string Colo = "—";
        public bool Fragmented;
        public StateInfo Clone() => (StateInfo)MemberwiseClone();
    }

    /// <summary>
    /// Runs one connection on a background thread: finds a working entry, verifies real WARP
    /// traffic, points the system proxy at it, then keeps it alive until cancelled.
    /// </summary>
    sealed class ConnectionWorker
    {
        static readonly string[] Entries =
        {
            "162.159.198.3", "162.159.198.4", "162.159.198.5", "162.159.198.6",
            "162.159.198.7", "162.159.198.8", "162.159.199.3", "162.159.199.4",
        };

        readonly string dir, usque, sni, mode;
        readonly bool fresh, testOnly;
        readonly SystemProxy proxy;
        readonly ManualResetEvent cancelled = new ManualResetEvent(false);
        readonly object stateLock = new object();
        readonly StateInfo state = new StateInfo();
        readonly LogFile log, childLog;
        volatile bool resumed;
        Process child;
        HelloRelay relay;

        public event Action<StateInfo> StateChanged;

        public ConnectionWorker(string dataDirectory, string usquePath, string sni, string mode, bool fresh, bool testOnly)
        {
            dir = dataDirectory; usque = usquePath; this.sni = sni; this.mode = mode;
            this.fresh = fresh; this.testOnly = testOnly;
            proxy = new SystemProxy(dir);
            log = new LogFile(Path.Combine(dir, "worker.log"));
            childLog = new LogFile(Path.Combine(dir, "connection.log"), cap: 5_000_000);
        }

        string RuntimePath => Path.Combine(dir, "runtime.json");
        string RoutePath => Path.Combine(dir, "last-route.json");

        public bool Stopped => cancelled.WaitOne(0);
        public void Cancel() => cancelled.Set();
        /// <summary>Called when Windows wakes from sleep: the old tunnel is likely dead.</summary>
        public void NotifyResume() => resumed = true;

        public StateInfo Snapshot() { lock (stateLock) return state.Clone(); }

        void Publish(string phase, string message)
        {
            StateInfo copy;
            lock (stateLock) { state.Phase = phase; state.Message = message; copy = state.Clone(); }
            log.Line("[" + phase + "] " + message);
            StateChanged?.Invoke(copy);
        }

        void Pause(double seconds) => cancelled.WaitOne(TimeSpan.FromSeconds(seconds));

        /// <summary>Ends usque and the relay right away (used when Windows is shutting down).</summary>
        public void KillNow()
        {
            Cancel();
            try { if (child != null && !child.HasExited) child.Kill(); } catch { }
        }

        static bool IsWarp(Dictionary<string, string> t) =>
            t.TryGetValue("warp", out var w) && (w == "on" || w == "plus");

        static string Get(Dictionary<string, string> t, string key) => t.TryGetValue(key, out var v) ? v : "—";

        /// <summary>Asks Cloudflare, through the local proxy, whether the request arrived over WARP.</summary>
        public static Dictionary<string, string> Trace(IWebProxy via = null)
        {
            var values = new Dictionary<string, string>();
            try
            {
                var request = (HttpWebRequest)WebRequest.Create("https://www.cloudflare.com/cdn-cgi/trace");
                request.Proxy = via ?? new WebProxy("127.0.0.1", Ports.Proxy);
                request.Timeout = 5000;
                request.ReadWriteTimeout = 5000;
                request.KeepAlive = false;
                request.UserAgent = "WarpSwitchNext";
                request.CachePolicy = new RequestCachePolicy(RequestCacheLevel.NoCacheNoStore);
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream()))
                {
                    foreach (var line in reader.ReadToEnd().Split('\n'))
                    {
                        int eq = line.IndexOf('=');
                        if (eq > 0) values[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                    }
                }
            }
            catch { }
            return values;
        }

        void StopChild()
        {
            var p = child;
            child = null;
            if (p != null)
            {
                try { if (!p.HasExited) { p.Kill(); p.WaitForExit(3000); } } catch { }
                try { p.Dispose(); } catch { }
            }
            relay?.Stop();
            relay = null;
        }

        bool Attempt(string endpoint, bool fragment, Dictionary<string, object> account)
        {
            StopChild();
            relay = new HelloRelay(endpoint, fragment, Ports.Relay);
            relay.Start();
            // usque connects to our relay on 127.0.0.1; the relay connects to the real entry.
            var runtime = new Dictionary<string, object>(account) { ["endpoint_h2_v4"] = "127.0.0.1" };
            Json.Write(RuntimePath, runtime);
            var args = string.Join(" ", new[]
            {
                "-c", Util.Quote(RuntimePath), "http-proxy", "-b", "127.0.0.1", "-p", Ports.Proxy.ToString(),
                "--http2", "-P", Ports.Relay.ToString(), "-s", sni, "-d", "1.1.1.1", "-d", "1.0.0.1", "--always-reconnect",
            });
            var p = new Process
            {
                StartInfo = new ProcessStartInfo(usque, args)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    WorkingDirectory = dir,
                },
            };
            p.OutputDataReceived += (s, e) => childLog.Line(e.Data);
            p.ErrorDataReceived += (s, e) => childLog.Line(e.Data);
            try { p.Start(); }
            catch (Exception e) { throw new UserError("无法启动连接组件：" + e.Message + "。请确认 core 文件夹完整，且未被安全软件隔离。"); }
            ChildJob.Add(p);
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            child = p;

            var deadline = DateTime.UtcNow.AddSeconds(17);
            while (!Stopped && !p.HasExited && DateTime.UtcNow < deadline)
            {
                Pause(0.8);
                var t = Trace();
                if (IsWarp(t))
                {
                    lock (stateLock)
                    {
                        state.Endpoint = endpoint; state.Sni = sni; state.Fragmented = fragment;
                        state.Ip = Get(t, "ip"); state.Colo = Get(t, "colo");
                    }
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Rebuild the tunnel while the system proxy stays on: the last working entry first,
        /// then up to two others, keeping the handshake mode that worked before.
        /// </summary>
        bool Rebuild(List<string> endpoints, Dictionary<string, object> account)
        {
            var now = Snapshot();
            var order = endpoints.Where(e => e != now.Endpoint).ToList();
            if (endpoints.Contains(now.Endpoint)) order.Insert(0, now.Endpoint);
            var tries = order.Take(3).ToList();
            for (int i = 0; i < tries.Count; i++)
            {
                if (Stopped) return false;
                Publish("reconnecting", "正在重新建立连接（" + (i + 1) + "/" + tries.Count + "）");
                if (Attempt(tries[i], now.Fragmented, account)) return true;
            }
            return false;
        }

        void SaveRoute()
        {
            var s = Snapshot();
            try { Json.Write(RoutePath, new CachedRoute { Endpoint = s.Endpoint, Sni = sni, Fragmented = s.Fragmented }); } catch { }
        }

        public int Run()
        {
            int exitCode = 0;
            try
            {
                if (!Util.ValidDomain(sni)) throw new UserError("请输入有效的伪装域名");
                if (!File.Exists(usque)) throw new UserError("找不到连接组件（core 文件夹）。请重新解压完整的程序文件夹。");
                foreach (var port in new[] { Ports.Proxy, Ports.Relay })
                    if (Util.PortBusy(port)) throw new UserError("本机端口 " + port + " 已被其他程序占用（可能是另一个代理软件），请先关闭它。");
                if (!testOnly)
                {
                    proxy.Restore();
                    Publish("connecting", "正在准备连接…");
                }

                // Right after login or wake the network is usually not up yet; trying entries now would only fail.
                Pause(0.3);
                if (!Util.Online())
                {
                    Publish("connecting", "等待网络连接…");
                    var wait = DateTime.UtcNow.AddSeconds(90);
                    while (!Stopped && !Util.Online() && DateTime.UtcNow < wait) Pause(1);
                    if (Stopped) return 0;
                }

                var account = Account.Load(Path.Combine(dir, "account.json"));
                if (account == null) throw new UserError("未找到有效的 WARP 账号；请重新打开程序并完成首次注册。");

                var cached = Json.Read<CachedRoute>(RoutePath);
                var endpoints = Entries.ToList();
                if (account.TryGetValue("endpoint_h2_v4", out var own) && own is string ownEntry) endpoints.Remove(ownEntry);
                if (fresh)
                {
                    var random = new Random();
                    endpoints = endpoints.OrderBy(_ => random.Next()).ToList();
                    if (cached != null) endpoints.Remove(cached.Endpoint);
                }
                else if (cached != null && endpoints.Contains(cached.Endpoint))
                {
                    endpoints.Remove(cached.Endpoint);
                    endpoints.Insert(0, cached.Endpoint);
                }
                var fragments = mode == "fragment" ? new[] { true } : mode == "compatible" ? new[] { false } : new[] { true, false };

                bool connected = false;
                foreach (var fragmented in fragments)
                {
                    for (int i = 0; i < endpoints.Count && !connected; i++)
                    {
                        if (Stopped) return 0;
                        lock (stateLock) { state.Endpoint = endpoints[i]; state.Sni = sni; state.Fragmented = fragmented; }
                        Publish("connecting", "正在尝试入口 " + (i + 1) + "/" + endpoints.Count + " · " + (fragmented ? "握手分片" : "兼容模式"));
                        connected = Attempt(endpoints[i], fragmented, account);
                    }
                    if (connected) break;
                }
                if (Stopped) return 0;
                if (!connected)
                {
                    Publish("error", "这些入口暂时无法通过验证。可换一个伪装域名或点“换入口重试”。");
                    return 1;
                }
                SaveRoute();
                if (testOnly) { Publish("on", "测试通过：WARP 已验证，未修改系统代理"); return 0; }

                proxy.Enable();
                Publish("on", "已连接 · 浏览器及使用系统代理的程序可用");

                int failures = 0;
                bool rebuildWhenOnline = false;
                double lastAwake = Util.AwakeSeconds();
                var lastWallTime = DateTime.UtcNow;
                while (!Stopped)
                {
                    Pause(5);
                    if (Stopped) break;
                    // The awake clock stops while the PC sleeps but the wall clock keeps going,
                    // so a gap between the two means the lid was closed and we just woke up.
                    double awake = Util.AwakeSeconds();
                    double wallDelta = (DateTime.UtcNow - lastWallTime).TotalSeconds;
                    if (resumed || wallDelta - (awake - lastAwake) > 10) { rebuildWhenOnline = true; failures = 0; resumed = false; }
                    lastWallTime = DateTime.UtcNow; lastAwake = awake;

                    var t = Trace();
                    if (IsWarp(t))
                    {
                        failures = 0; rebuildWhenOnline = false;
                        lock (stateLock) { state.Ip = Get(t, "ip"); state.Colo = Get(t, "colo"); }
                        Publish("on", "已连接 · 浏览器及使用系统代理的程序可用");
                        continue;
                    }
                    // No network at all (e.g. Wi-Fi still rejoining after wake): wait, don't count it as a failure.
                    if (!Util.Online())
                    {
                        Publish("reconnecting", "等待网络恢复，恢复后会自动重新连接…");
                        continue;
                    }
                    failures++;
                    // A short drop while awake: give the tunnel's own reconnect a chance first.
                    bool childAlive = child != null && !child.HasExited;
                    if (!rebuildWhenOnline && failures < 3 && childAlive)
                    {
                        Publish("reconnecting", "连接中断，正在等待隧道恢复（" + failures + "/3）");
                        continue;
                    }
                    // After sleep the old tunnel is dead, and a long drop won't heal on its own: rebuild it.
                    if (Rebuild(endpoints, account))
                    {
                        failures = 0; rebuildWhenOnline = false;
                        SaveRoute();
                        Publish("on", "已重新连接 · 浏览器及使用系统代理的程序可用");
                        continue;
                    }
                    if (Stopped) break;
                    if (!Util.Online())
                    {
                        rebuildWhenOnline = true;
                        Publish("reconnecting", "等待网络恢复，恢复后会自动重新连接…");
                        continue;
                    }
                    proxy.Restore();
                    Publish("error", "无法重新连接，系统代理已恢复。请点“换入口重试”。");
                    return 1;
                }
            }
            catch (UserError e) { Publish("error", e.Message); exitCode = 1; }
            catch (Exception e) { log.Line(e.ToString()); Publish("error", e.Message); exitCode = 1; }
            finally
            {
                try { if (!testOnly) proxy.Restore(); }
                catch (Exception e) { Publish("error", e.Message); exitCode = 1; }
                StopChild();
                Util.TryDelete(RuntimePath);
                if (Snapshot().Phase != "error") Publish("off", "已关闭，系统代理已恢复");
            }
            return exitCode;
        }
    }
}
