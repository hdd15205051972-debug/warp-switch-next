import Foundation
import Darwin
import Network

let fm = FileManager.default
let proxyPort = 7891
let relayPort: UInt16 = 18443

func validAccount(at url: URL) -> Bool {
    guard let data = try? Data(contentsOf: url),
          let values = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return false }
    return ["private_key", "id", "access_token", "endpoint_pub_key", "ipv4", "ipv6"].allSatisfy {
        guard let value = values[$0] as? String else { return false }
        return !value.isEmpty
    }
}

struct CommandResult { let code: Int32; let text: String }
func command(_ executable: String, _ arguments: [String], timeout: Double = 8) -> CommandResult {
    let process = Process(), output = Pipe()
    process.executableURL = URL(fileURLWithPath: executable); process.arguments = arguments
    process.standardOutput = output; process.standardError = output; process.standardInput = FileHandle.nullDevice
    let done = DispatchSemaphore(value: 0)
    process.terminationHandler = { _ in done.signal() }
    do { try process.run() } catch { return CommandResult(code: -1, text: error.localizedDescription) }
    // Drain concurrently: a full pipe must never deadlock termination or cleanup.
    let reader = DispatchGroup(); reader.enter()
    var data = Data()
    DispatchQueue.global().async { data = output.fileHandleForReading.readDataToEndOfFile(); reader.leave() }
    if done.wait(timeout: .now() + timeout) == .timedOut {
        process.terminate()
        if done.wait(timeout: .now() + 1) == .timedOut { kill(process.processIdentifier, SIGKILL); done.wait() }
    }
    reader.wait()
    return CommandResult(code: process.terminationStatus, text: String(data: data, encoding: .utf8) ?? "")
}
func privateJSON<T: Encodable>(_ value: T, to url: URL) throws {
    let data = try JSONEncoder().encode(value)
    try data.write(to: url, options: .atomic)
    try fm.setAttributes([.posixPermissions: 0o600], ofItemAtPath: url.path)
}
func readJSON<T: Decodable>(_ type: T.Type, from url: URL) -> T? {
    guard let data = try? Data(contentsOf: url) else { return nil }
    return try? JSONDecoder().decode(type, from: data)
}
func validDomain(_ s: String) -> Bool {
    s.count <= 253 && s.range(of: #"^(?=.{1,253}$)[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?(?:\.[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?)+$"#, options: .regularExpression) != nil
}

struct ConnectionSettings: Codable {
    var sni = "www.bing.com"
    var mode = "auto"
    var fresh = false
    // Optional so settings.json written by older versions (without this key) still decodes.
    var autoConnect: Bool? = nil
}
struct ConnectionState: Codable {
    var phase = "off"
    var message = "点击开启以连接"
    var endpoint = "—"
    var sni = "—"
    var ip = "—"
    var colo = "—"
    var fragmented = false
    var pid: Int32 = 0
    var updated = Date().timeIntervalSince1970
}
struct CachedRoute: Codable { var endpoint: String; var sni: String; var fragmented: Bool }
struct SavedProxy: Codable {
    var service: String; var kind: String; var server: String; var port: Int; var enabled: Bool
    var bypass: [String]? = nil
    var appliedBypass: [String]? = nil
}
let proxyKinds = [("web", "HTTP"), ("secureweb", "HTTPS")]

final class ProxyManager {
    let path: URL
    init(directory: URL) { path = directory.appendingPathComponent("proxy-snapshot.json") }
    func getBypass(_ service: String) throws -> [String] {
        let result = command("/usr/sbin/networksetup", ["-getproxybypassdomains", service])
        guard result.code == 0 else { throw NSError(domain: "Proxy", code: 10, userInfo: [NSLocalizedDescriptionKey: "无法读取内网直连设置：\(service)"]) }
        if result.text.contains("There aren't any") { return [] }
        return result.text.split(separator: "\n").map(String.init)
    }
    func stopLegacy() throws {
        let domain = "gui/\(getuid())"
        let legacy = fm.homeDirectoryForCurrentUser.appendingPathComponent("Library/Application Support/WarpSwitch/bin").path
        let known = command("/bin/launchctl", ["print", "\(domain)/com.warp.singbox"])
        guard known.code == 0, known.text.contains(legacy + "/sing-box") else { return }
        _ = command("/bin/launchctl", ["bootout", "\(domain)/com.warp.singbox"])
        let usque = command("/bin/launchctl", ["print", "\(domain)/com.warp.usque"])
        if usque.code == 0, usque.text.contains(legacy + "/usque") {
            _ = command("/bin/launchctl", ["bootout", "\(domain)/com.warp.usque"])
        }
        let services = command("/usr/sbin/networksetup", ["-listallnetworkservices"]).text.split(separator: "\n").dropFirst().map(String.init)
        for service in services where !service.hasPrefix("*") {
            for kind in ["web", "secureweb", "socksfirewall"] {
                if let current = try? get(kind, service), current.0 == "127.0.0.1", current.1 == 7890 {
                    _ = command("/usr/sbin/networksetup", ["-set\(kind)proxystate", service, "off"])
                }
            }
        }
    }
    func get(_ kind: String, _ service: String) throws -> (String, Int, Bool, Bool) {
        let result = command("/usr/sbin/networksetup", ["-get\(kind)proxy", service])
        guard result.code == 0, result.text.contains("Enabled:") else {
            throw NSError(domain: "Proxy", code: 1, userInfo: [NSLocalizedDescriptionKey: "无法读取系统代理：\(service)"])
        }
        var values: [String: String] = [:]
        for line in result.text.split(separator: "\n") {
            let kv = line.split(separator: ":", maxSplits: 1)
            if kv.count == 2 { values[String(kv[0])] = kv[1].trimmingCharacters(in: .whitespaces) }
        }
        return (values["Server"] ?? "", Int(values["Port"] ?? "0") ?? 0,
                values["Enabled"] == "Yes", values["Authenticated Proxy Enabled"] == "1")
    }
    func enable() throws {
        if fm.fileExists(atPath: path.path) { try restore() }
        let result = command("/usr/sbin/networksetup", ["-listallnetworkservices"])
        guard result.code == 0 else {
            throw NSError(domain: "Proxy", code: 2, userInfo: [NSLocalizedDescriptionKey: "系统未允许设置网络代理；请在系统授权提示中允许。"])
        }
        let services = result.text.split(separator: "\n").dropFirst().map(String.init).filter { !$0.hasPrefix("*") && !$0.isEmpty }
        guard !services.isEmpty else { throw NSError(domain: "Proxy", code: 3, userInfo: [NSLocalizedDescriptionKey: "没有可用网络服务"] ) }
        var saved: [SavedProxy] = []
        for service in services {
            let bypass = try getBypass(service)
            let localDomains = ["localhost", "127.0.0.1", "::1", "*.local", "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "169.254.0.0/16"]
            var applied = bypass
            for domain in localDomains where !applied.contains(domain) { applied.append(domain) }
            for (kind, _) in proxyKinds {
                let (server, port, enabled, authenticated) = try get(kind, service)
                if enabled && authenticated {
                    throw NSError(domain: "Proxy", code: 4, userInfo: [NSLocalizedDescriptionKey: "\(service) 正在使用需要密码的代理，请先关闭它再连接。"])
                }
                if enabled && !(server == "127.0.0.1" && port == proxyPort) {
                    throw NSError(domain: "Proxy", code: 5, userInfo: [NSLocalizedDescriptionKey: "\(service) 正在使用另一个代理，请先关闭旧 WARP 或其他代理软件。"])
                }
                saved.append(SavedProxy(service: service, kind: kind, server: server, port: port, enabled: enabled, bypass: bypass, appliedBypass: applied))
            }
        }
        try privateJSON(saved, to: path)
        do {
            for item in saved where item.kind == "web" {
                guard command("/usr/sbin/networksetup", ["-setproxybypassdomains", item.service] + (item.appliedBypass ?? [])).code == 0 else {
                    throw NSError(domain: "Proxy", code: 11, userInfo: [NSLocalizedDescriptionKey: "无法设置内网直连：\(item.service)"])
                }
            }
            for item in saved {
                let result = command("/usr/sbin/networksetup", ["-set\(item.kind)proxy", item.service, "127.0.0.1", String(proxyPort)])
                guard result.code == 0 else { throw NSError(domain: "Proxy", code: 6, userInfo: [NSLocalizedDescriptionKey: "无法设置系统代理：\(item.service)"]) }
                let current = try get(item.kind, item.service)
                guard current.0 == "127.0.0.1", current.1 == proxyPort, current.2 else {
                    throw NSError(domain: "Proxy", code: 7, userInfo: [NSLocalizedDescriptionKey: "系统代理没有生效，请允许系统网络设置授权。"])
                }
            }
        } catch { try? restore(); throw error }
    }
    func restore() throws {
        guard fm.fileExists(atPath: path.path) else { return }
        guard let saved = readJSON([SavedProxy].self, from: path) else {
            throw NSError(domain: "Proxy", code: 8, userInfo: [NSLocalizedDescriptionKey: "代理恢复记录无法读取，请在系统网络设置里关闭 127.0.0.1:7891 代理。"])
        }
        var failures = false
        for item in saved where item.kind == "web" {
            if let prior = item.bypass, let applied = item.appliedBypass {
                do {
                    let current = try getBypass(item.service)
                    if current == applied {
                        if command("/usr/sbin/networksetup", ["-setproxybypassdomains", item.service] + (prior.isEmpty ? ["Empty"] : prior)).code != 0 { failures = true }
                    }
                } catch { failures = true }
            }
        }
        for item in saved {
            guard let current = try? get(item.kind, item.service) else { failures = true; continue }
            // Never overwrite a proxy the user changed after this app connected.
            guard current.0 == "127.0.0.1", current.1 == proxyPort else { continue }
            if !item.server.isEmpty && item.port > 0 {
                if command("/usr/sbin/networksetup", ["-set\(item.kind)proxy", item.service, item.server, String(item.port)]).code != 0 { failures = true }
            }
            if command("/usr/sbin/networksetup", ["-set\(item.kind)proxystate", item.service, item.enabled ? "on" : "off"]).code != 0 { failures = true }
        }
        if failures { throw NSError(domain: "Proxy", code: 9, userInfo: [NSLocalizedDescriptionKey: "部分系统代理未能恢复，请检查网络设置。"] ) }
        try fm.removeItem(at: path)
    }
}

final class ConnectionWorker {
    let directory: URL
    let helper: String
    let settings: ConnectionSettings
    let testOnly: Bool
    let parent: Int32?
    let proxy: ProxyManager
    var state = ConnectionState()
    var child: Process?
    var childLog: FileHandle?
    var relay: HelloRelay?
    private let lock = NSLock()
    private var cancelled = false
    var sources: [DispatchSourceSignal] = []
    let signalQueue = DispatchQueue(label: "warp.signals")
    var stopped: Bool {
        lock.lock(); defer { lock.unlock() }
        return cancelled || (parent != nil && getppid() != parent!)
    }
    func cancel() { lock.lock(); cancelled = true; lock.unlock() }
    init(directory: URL, helper: String, settings: ConnectionSettings, testOnly: Bool, parent: Int32?) {
        self.directory = directory; self.helper = helper; self.settings = settings
        self.testOnly = testOnly; self.parent = parent; self.proxy = ProxyManager(directory: directory)
    }
    func publish(_ phase: String, _ message: String) {
        state.phase = phase; state.message = message; state.pid = getpid()
        state.updated = Date().timeIntervalSince1970
        try? privateJSON(state, to: directory.appendingPathComponent("state.json"))
        print("[\(phase)] \(message)"); fflush(stdout)
    }
    func pause(_ seconds: Double) {
        let deadline = Date().addingTimeInterval(seconds)
        while !stopped && Date() < deadline { Thread.sleep(forTimeInterval: 0.1) }
    }
    func stopChild() {
        if let child, child.isRunning {
            child.terminate()
            let deadline = Date().addingTimeInterval(2)
            while child.isRunning && Date() < deadline { Thread.sleep(forTimeInterval: 0.05) }
            if child.isRunning { kill(child.processIdentifier, SIGKILL) }
            child.waitUntilExit()
        }
        child = nil
        try? childLog?.close(); childLog = nil
        relay?.stop(); relay = nil
    }
    func trace() -> [String: String] {
        let result = command("/usr/bin/curl", ["-fsS", "--max-time", "4", "--connect-timeout", "3", "-x", "http://127.0.0.1:\(proxyPort)", "https://www.cloudflare.com/cdn-cgi/trace"], timeout: 5)
        guard result.code == 0 else { return [:] }
        var values: [String: String] = [:]
        for line in result.text.split(separator: "\n") {
            let kv = line.split(separator: "=", maxSplits: 1)
            if kv.count == 2 { values[String(kv[0])] = String(kv[1]) }
        }
        return values
    }
    func attempt(endpoint: String, fragment: Bool, account: [String: Any]) throws -> Bool {
        stopChild()
        relay = HelloRelay(endpoint: endpoint, fragment: fragment, port: relayPort)
        try relay!.start()
        var runtime = account; runtime["endpoint_h2_v4"] = "127.0.0.1"
        let cfg = directory.appendingPathComponent("runtime.json")
        let contents = try JSONSerialization.data(withJSONObject: runtime, options: [.prettyPrinted, .sortedKeys])
        try contents.write(to: cfg, options: .atomic)
        try fm.setAttributes([.posixPermissions: 0o600], ofItemAtPath: cfg.path)
        let log = directory.appendingPathComponent("connection.log")
        if !fm.fileExists(atPath: log.path) { fm.createFile(atPath: log.path, contents: nil, attributes: [.posixPermissions: 0o600]) }
        childLog = try FileHandle(forWritingTo: log); try childLog!.seekToEnd()
        let p = Process(); p.executableURL = URL(fileURLWithPath: helper)
        p.arguments = ["-c", cfg.path, "http-proxy", "-b", "127.0.0.1", "-p", String(proxyPort), "--http2", "-P", String(relayPort), "-s", settings.sni, "-d", "1.1.1.1", "-d", "1.0.0.1", "--always-reconnect"]
        p.standardInput = FileHandle.nullDevice; p.standardOutput = childLog; p.standardError = childLog
        try p.run(); child = p
        let deadline = Date().addingTimeInterval(17)
        while !stopped && p.isRunning && Date() < deadline {
            pause(0.8)
            let t = trace()
            if t["warp"] == "on" || t["warp"] == "plus" {
                state.endpoint = endpoint; state.sni = settings.sni; state.fragmented = fragment
                state.ip = t["ip"] ?? "—"; state.colo = t["colo"] ?? "—"
                return true
            }
        }
        return false
    }
    /// Rebuild the tunnel while the system proxy stays on: the last working entry first,
    /// then up to two others, keeping the handshake mode that worked before.
    func rebuild(endpoints: [String], account: [String: Any]) throws -> Bool {
        let current = state.endpoint, fragmented = state.fragmented
        var order = endpoints.filter { $0 != current }
        if endpoints.contains(current) { order.insert(current, at: 0) }
        let tries = Array(order.prefix(3))
        for (index, endpoint) in tries.enumerated() {
            if stopped { return false }
            publish("reconnecting", "正在重新建立连接（\(index + 1)/\(tries.count)）")
            if try attempt(endpoint: endpoint, fragment: fragmented, account: account) { return true }
        }
        return false
    }
    func run() -> Int32 {
        for sig in [SIGTERM, SIGINT, SIGHUP] {
            signal(sig, SIG_IGN)
            let source = DispatchSource.makeSignalSource(signal: sig, queue: signalQueue)
            source.setEventHandler { [weak self] in self?.cancel() }
            source.resume(); sources.append(source)
        }
        var exitCode: Int32 = 0
        defer {
            do { if !testOnly { try proxy.restore() } }
            catch { publish("error", error.localizedDescription); exitCode = 1 }
            stopChild()
            try? fm.removeItem(at: directory.appendingPathComponent("runtime.json"))
            if state.phase != "error" { publish("off", "已关闭，系统代理已恢复") }
        }
        do {
            guard validDomain(settings.sni) else { throw NSError(domain: "Settings", code: 1, userInfo: [NSLocalizedDescriptionKey: "请输入有效的伪装域名"] ) }
            // Watches whether the Mac has any usable network (Wi-Fi joins a few seconds after login or wake).
            let network = NWPathMonitor()
            network.start(queue: DispatchQueue(label: "warp.network"))
            defer { network.cancel() }
            var online: Bool { network.currentPath.status == .satisfied }
            if !testOnly {
                try proxy.restore()
                publish("connecting", "正在切换到新开关…")
                try proxy.stopLegacy()
            }
            // Right after login the network is usually not up yet; trying entries now would only fail.
            pause(0.5)
            if !online {
                publish("connecting", "等待网络连接…")
                let deadline = Date().addingTimeInterval(90)
                while !stopped && !online && Date() < deadline { pause(1) }
                if stopped { return 0 }
            }
            let accountPath = directory.appendingPathComponent("account.json")
            guard let data = try? Data(contentsOf: accountPath),
                  let account = try JSONSerialization.jsonObject(with: data) as? [String: Any],
                  account["private_key"] is String, account["endpoint_pub_key"] is String else {
                throw NSError(domain: "Account", code: 1, userInfo: [NSLocalizedDescriptionKey: "未找到有效的 WARP 账号；请重新打开应用并完成首次注册。"])
            }
            let cached = readJSON(CachedRoute.self, from: directory.appendingPathComponent("last-route.json"))
            var endpoints = ["162.159.198.3", "162.159.198.4", "162.159.198.5", "162.159.198.6", "162.159.198.7", "162.159.198.8", "162.159.199.3", "162.159.199.4"]
            // Respect the request for a different entry: never retry the old app's entry.
            if let old = account["endpoint_h2_v4"] as? String { endpoints.removeAll { $0 == old } }
            if settings.fresh {
                endpoints.shuffle()
                if let cached { endpoints.removeAll { $0 == cached.endpoint } }
            }
            if !settings.fresh, let cached, endpoints.contains(cached.endpoint) {
                endpoints.removeAll { $0 == cached.endpoint }; endpoints.insert(cached.endpoint, at: 0)
            }
            let fragments: [Bool] = settings.mode == "fragment" ? [true] : settings.mode == "compatible" ? [false] : [true, false]
            var failureCycles = 0
            var connected = false
            outer: for fragmented in fragments {
                for (index, endpoint) in endpoints.enumerated() {
                    if stopped { break outer }
                    state.endpoint = endpoint; state.sni = settings.sni; state.fragmented = fragmented
                    publish("connecting", "正在尝试入口 \(index + 1)/\(endpoints.count) · \(fragmented ? "握手分片" : "兼容模式")")
                    if try attempt(endpoint: endpoint, fragment: fragmented, account: account) {
                        connected = true; break outer
                    }
                }
            }
            if stopped { return 0 }
            guard connected else {
                publish("error", "这些入口暂时无法通过验证。可换一个伪装域名或点“换入口重试”。")
                return 1
            }
            try privateJSON(CachedRoute(endpoint: state.endpoint, sni: settings.sni, fragmented: state.fragmented), to: directory.appendingPathComponent("last-route.json"))
            if testOnly { publish("on", "测试通过：WARP 已验证，未修改系统代理"); return 0 }
            try proxy.enable()
            publish("on", "已连接 · 浏览器及支持系统代理的 App 可使用")
            var lastWall = Date().timeIntervalSince1970
            var lastAwake = ProcessInfo.processInfo.systemUptime
            var rebuildWhenOnline = false
            while !stopped {
                pause(5)
                if stopped { break }
                // systemUptime stops while the Mac sleeps but the wall clock keeps going,
                // so a gap between the two means the lid was closed and we just woke up.
                let wall = Date().timeIntervalSince1970, awake = ProcessInfo.processInfo.systemUptime
                if (wall - lastWall) - (awake - lastAwake) > 10 { rebuildWhenOnline = true; failureCycles = 0 }
                lastWall = wall; lastAwake = awake
                let t = trace()
                if t["warp"] == "on" || t["warp"] == "plus" {
                    failureCycles = 0; rebuildWhenOnline = false
                    state.ip = t["ip"] ?? "—"; state.colo = t["colo"] ?? "—"
                    publish("on", "已连接 · 浏览器及支持系统代理的 App 可使用")
                    continue
                }
                // No network at all (e.g. Wi-Fi still rejoining after wake): wait, don't count it as a failure.
                guard online else {
                    publish("reconnecting", "等待网络恢复，恢复后会自动重新连接…")
                    continue
                }
                failureCycles += 1
                // A short drop while awake: give the tunnel's own reconnect a chance first.
                if !rebuildWhenOnline && failureCycles < 3 && child?.isRunning == true {
                    publish("reconnecting", "连接中断，正在等待隧道恢复（\(failureCycles)/3）")
                    continue
                }
                // After sleep the old tunnel is dead, and a long drop won't heal on its own: rebuild it.
                if try rebuild(endpoints: endpoints, account: account) {
                    failureCycles = 0; rebuildWhenOnline = false
                    try? privateJSON(CachedRoute(endpoint: state.endpoint, sni: settings.sni, fragmented: state.fragmented), to: directory.appendingPathComponent("last-route.json"))
                    publish("on", "已重新连接 · 浏览器及支持系统代理的 App 可使用")
                    continue
                }
                if stopped { break }
                if !online {
                    rebuildWhenOnline = true
                    publish("reconnecting", "等待网络恢复，恢复后会自动重新连接…")
                    continue
                }
                try proxy.restore()
                publish("error", "无法重新连接，系统代理已恢复。请点“换入口重试”。")
                return 1
            }
        } catch { publish("error", error.localizedDescription); exitCode = 1 }
        return exitCode
    }
}
