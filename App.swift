import Cocoa
import SwiftUI

final class SwitchModel: ObservableObject {
    @Published var state = ConnectionState()
    @Published var settings = ConnectionSettings()
    @Published var working = false
    @Published var stopping = false
    @Published var needsSetup = true
    @Published var registering = false
    private var registrationCancelled = false
    let directory: URL
    var worker: Process?
    var log: FileHandle?
    var timer: Timer?
    var onState: (() -> Void)?

    init() {
        directory = fm.homeDirectoryForCurrentUser.appendingPathComponent("Library/Application Support/WarpSwitchNext", isDirectory: true)
        do {
            try fm.createDirectory(at: directory, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
            try fm.setAttributes([.posixPermissions: 0o700], ofItemAtPath: directory.path)
            settings = readJSON(ConnectionSettings.self, from: directory.appendingPathComponent("settings.json")) ?? ConnectionSettings()
            needsSetup = !validAccount(at: directory.appendingPathComponent("account.json"))
            try ProxyManager(directory: directory).restore()
        } catch { state.phase = "error"; state.message = error.localizedDescription }
        timer = Timer.scheduledTimer(withTimeInterval: 1, repeats: true) { [weak self] _ in self?.refresh() }
    }
    func refresh() {
        if working && !registering, let latest = readJSON(ConnectionState.self, from: directory.appendingPathComponent("state.json")) {
            state = latest
        }
        onState?()
    }
    func registerAccount(fresh: Bool) {
        registrationCancelled = false; registering = true
        state = ConnectionState(); state.phase = "registering"; state.message = "正在创建你的独立免费 WARP 账号…"
        let pending = directory.appendingPathComponent("account.pending.json")
        do {
            try? fm.removeItem(at: pending)
            let logPath = directory.appendingPathComponent("registration.log")
            fm.createFile(atPath: logPath.path, contents: nil, attributes: [.posixPermissions: 0o600])
            log = try FileHandle(forWritingTo: logPath)
            let p = Process()
            p.executableURL = Bundle.main.bundleURL.appendingPathComponent("Contents/Helpers/usque")
            p.arguments = ["-c", pending.path, "register", "-a"]
            p.standardInput = FileHandle.nullDevice; p.standardOutput = log; p.standardError = log
            p.terminationHandler = { [weak self] process in
                DispatchQueue.main.async { self?.finishRegistration(process, fresh: fresh) }
            }
            try p.run(); worker = p; working = true
            DispatchQueue.global().asyncAfter(deadline: .now() + 40) { [weak p] in
                if let p, p.isRunning { p.terminate() }
            }
        } catch {
            registering = false; working = false; worker = nil
            try? log?.close(); log = nil
            state.phase = "error"; state.message = "无法启动注册，请重新安装应用。"
        }
    }
    func finishRegistration(_ process: Process, fresh: Bool) {
        guard worker === process else { return }
        worker = nil; working = false; stopping = false; registering = false
        try? log?.close(); log = nil
        let pending = directory.appendingPathComponent("account.pending.json")
        defer { try? fm.removeItem(at: pending) }
        if registrationCancelled { state = ConnectionState(); state.message = "注册已取消"; return }
        guard process.terminationStatus == 0, validAccount(at: pending) else {
            state.phase = "error"; state.message = "注册未完成。请检查网络后重试，也可打开日志查看原因。"; return
        }
        do {
            let destination = directory.appendingPathComponent("account.json")
            try fm.setAttributes([.posixPermissions: 0o600], ofItemAtPath: pending.path)
            if fm.fileExists(atPath: destination.path) {
                // Preserve any previous invalid account for local troubleshooting.
                let backup = directory.appendingPathComponent("account.invalid.\(UUID().uuidString).json")
                try fm.moveItem(at: destination, to: backup)
            }
            try fm.moveItem(at: pending, to: destination)
            needsSetup = false
            start(fresh: fresh)
        } catch { state.phase = "error"; state.message = "账号未能保存，请检查应用数据目录的访问权限。" }
    }
    func start(fresh: Bool = false) {
        guard !working, !stopping else { return }
        let sni = settings.sni.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        guard validDomain(sni) else { state.phase = "error"; state.message = "请输入有效的域名，例如 www.bing.com"; return }
        settings.sni = sni; settings.fresh = fresh
        if needsSetup { registerAccount(fresh: fresh); return }
        do {
            try privateJSON(settings, to: directory.appendingPathComponent("settings.json"))
            try? fm.removeItem(at: directory.appendingPathComponent("state.json"))
            let p = Process(); p.executableURL = Bundle.main.executableURL
            p.arguments = ["--worker", "--data", directory.path, "--parent", String(getpid())]
            p.standardInput = FileHandle.nullDevice
            let logPath = directory.appendingPathComponent("worker.log")
            if !fm.fileExists(atPath: logPath.path) { fm.createFile(atPath: logPath.path, contents: nil, attributes: [.posixPermissions: 0o600]) }
            if let size = try? fm.attributesOfItem(atPath: logPath.path)[.size] as? Int, size > 2_000_000 {
                try Data().write(to: logPath)
            }
            log = try FileHandle(forWritingTo: logPath); try log?.seekToEnd()
            p.standardOutput = log; p.standardError = log
            p.terminationHandler = { [weak self] process in
                DispatchQueue.main.async {
                    self?.completed(process)
                }
            }
            try p.run(); worker = p; working = true
            state = ConnectionState(); state.phase = "connecting"; state.message = "正在寻找不同的 WARP 入口…"
        } catch { state.phase = "error"; state.message = error.localizedDescription }
    }
    func completed(_ process: Process) {
        guard worker === process else { return }
        refresh()
        working = false; stopping = false; worker = nil
        try? log?.close(); log = nil
        if state.phase != "error" {
            state = ConnectionState()
            state.message = "已关闭，系统代理已恢复"
        }
        onState?()
    }
    func stop(after: (() -> Void)? = nil) {
        guard let p = worker, p.isRunning else { after?(); return }
        if registering { registrationCancelled = true }
        stopping = true; state.phase = "stopping"; state.message = "正在关闭并恢复系统代理…"
        p.terminate()
        DispatchQueue.global().async {
            p.waitUntilExit()
            DispatchQueue.main.async {
                if self.registering { self.finishRegistration(p, fresh: false) } else { self.completed(p) }
                after?()
            }
        }
    }
    func retry() {
        if working { stop { [weak self] in self?.start(fresh: true) } }
        else { start(fresh: true) }
    }
    func editDomain() {
        let alert = NSAlert(); alert.messageText = "修改伪装域名"
        alert.informativeText = "这是连接时展示的 TLS 域名。它不会把入口 IP 变成该网站的 IP，也不能保证网络设备识别不到 WARP。修改后请重新连接。"
        let input = NSTextField(frame: NSRect(x: 0, y: 0, width: 300, height: 26))
        input.stringValue = settings.sni; alert.accessoryView = input
        alert.addButton(withTitle: "保存"); alert.addButton(withTitle: "取消")
        alert.window.initialFirstResponder = input
        if alert.runModal() == .alertFirstButtonReturn {
            let value = input.stringValue.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
            if validDomain(value) { settings.sni = value; try? privateJSON(settings, to: directory.appendingPathComponent("settings.json")) }
            else { let error = NSAlert(); error.messageText = "域名格式不正确"; error.runModal() }
        }
    }
    var title: String {
        switch state.phase {
        case "registering": return "正在注册"
        case "on": return "已连接"
        case "connecting": return "正在连接"
        case "reconnecting": return "正在重连"
        case "stopping": return "正在关闭"
        case "error": return "暂未连接"
        default: return "已关闭"
        }
    }
}

struct SwitchView: View {
    @ObservedObject var model: SwitchModel
    var tint: Color { model.state.phase == "on" ? .green : model.state.phase == "error" ? .orange : .blue }
    func row(_ label: String, _ value: String) -> some View {
        HStack { Text(label).foregroundStyle(.secondary); Spacer(); Text(value).font(.system(size: 14, design: .monospaced)).textSelection(.enabled).lineLimit(1).minimumScaleFactor(0.6) }
            .padding(.vertical, 10)
    }
    var body: some View {
        VStack(spacing: 18) {
            HStack(spacing: 12) {
                Image(systemName: "shield.lefthalf.filled").font(.system(size: 31)).foregroundStyle(.white)
                    .frame(width: 58, height: 58).background(.blue.gradient, in: RoundedRectangle(cornerRadius: 15))
                VStack(alignment: .leading, spacing: 5) {
                    Text("WARP 新开关").font(.system(size: 23, weight: .bold))
                    Text("不同入口 · HTTPS 连接 · 握手分片").font(.system(size: 12)).foregroundStyle(.secondary)
                }
                Spacer()
            }
            Spacer(minLength: 2)
            ZStack {
                Circle().fill(tint.opacity(0.12)).frame(width: 136, height: 136)
                Circle().stroke(tint.opacity(0.5), lineWidth: 3).frame(width: 136, height: 136)
                if ["connecting", "reconnecting", "stopping", "registering"].contains(model.state.phase) {
                    ProgressView().controlSize(.large).tint(tint)
                } else {
                    Image(systemName: model.state.phase == "on" ? "checkmark.shield.fill" : "shield.lefthalf.filled")
                        .font(.system(size: 60)).foregroundStyle(tint)
                }
            }
            Text(model.title).font(.system(size: 25, weight: .semibold))
            Text(model.state.message).font(.system(size: 13)).foregroundStyle(.secondary)
                .multilineTextAlignment(.center).frame(height: 38)
            VStack(spacing: 0) {
                row("出口 IP", model.state.ip); Divider()
                row("WARP 入口", model.state.endpoint); Divider()
                HStack {
                    Text("伪装域名").foregroundStyle(.secondary); Spacer()
                    Text(model.settings.sni).font(.system(size: 14, design: .monospaced))
                    Button { model.editDomain() } label: { Image(systemName: "pencil") }.buttonStyle(.plain).disabled(model.working)
                }.padding(.vertical, 10)
                Divider(); row("节点", model.state.colo)
                Divider(); row("本机代理", "127.0.0.1:7891")
            }.padding(.horizontal, 16).background(Color.primary.opacity(0.035), in: RoundedRectangle(cornerRadius: 13))
                .overlay(RoundedRectangle(cornerRadius: 13).stroke(Color.primary.opacity(0.12)))
            HStack {
                Text("连接模式").foregroundStyle(.secondary); Spacer()
                Picker("连接模式", selection: $model.settings.mode) {
                    Text("自动尝试").tag("auto")
                    Text("握手分片").tag("fragment")
                    Text("兼容模式").tag("compatible")
                }.labelsHidden().frame(width: 160).disabled(model.working)
            }.font(.system(size: 13))
            Button {
                model.working ? model.stop() : model.start()
            } label: {
                Text(model.working ? (model.registering ? "取消注册" : "关闭") : (model.needsSetup ? "同意条款并注册" : "开启")).font(.system(size: 19, weight: .bold)).frame(maxWidth: .infinity).padding(.vertical, 13)
                    .foregroundStyle(.white).background(model.working ? Color.red : Color.blue, in: Capsule())
            }.buttonStyle(.plain).disabled(model.stopping)
            HStack {
                Button("换入口重试") { model.retry() }.disabled(model.stopping || model.needsSetup)
                Spacer()
                Button("打开日志") { NSWorkspace.shared.open(model.directory) }
                Button("退出") { NSApp.terminate(nil) }
            }.font(.system(size: 12))
            if model.needsSetup {
                VStack(spacing: 3) {
                    Text("首次使用将向 Cloudflare 注册独立账号。")
                    Link("Cloudflare 服务条款", destination: URL(string: "https://www.cloudflare.com/terms/")!)
                }.font(.system(size: 11)).foregroundStyle(.secondary)
            } else {
                Text("通过系统代理连接。服务器公钥校验保持开启。")
                    .font(.system(size: 11)).foregroundStyle(.tertiary)
            }
            Spacer(minLength: 0)
        }.padding(28).frame(width: 460, height: 780)
    }
}

final class AppDelegate: NSObject, NSApplicationDelegate, NSWindowDelegate {
    var model: SwitchModel!
    var window: NSWindow!
    var status: NSStatusItem!
    var ending = false
    func applicationDidFinishLaunching(_ notification: Notification) {
        let others = NSRunningApplication.runningApplications(withBundleIdentifier: "io.github.warp-switch-next")
            .filter { $0.processIdentifier != getpid() }
        if let other = others.first { other.activate(); NSApp.terminate(nil); return }
        model = SwitchModel()
        window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 460, height: 780), styleMask: [.titled, .closable, .miniaturizable], backing: .buffered, defer: false)
        window.title = "WARP 新开关"; window.isReleasedWhenClosed = false; window.delegate = self
        window.contentView = NSHostingView(rootView: SwitchView(model: model))
        window.center(); window.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
        status = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        status.button?.image = NSImage(systemSymbolName: "shield.lefthalf.filled", accessibilityDescription: "WARP 新开关")
        model.onState = { [weak self] in self?.updateMenu() }
        updateMenu()
        if CommandLine.arguments.contains("--connect") && !model.needsSetup { model.start() }
    }
    func updateMenu() {
        guard let model else { return }
        let menu = NSMenu()
        let label = NSMenuItem(title: "WARP 新开关 · \(model.title)", action: nil, keyEquivalent: "")
        menu.addItem(label); menu.addItem(.separator())
        func item(_ title: String, _ action: Selector) {
            let i = NSMenuItem(title: title, action: action, keyEquivalent: ""); i.target = self; menu.addItem(i)
        }
        item("显示窗口", #selector(showWindow)); item(model.working ? "关闭连接" : model.needsSetup ? "同意条款并注册" : "开启连接", #selector(toggle))
        item("换入口重试", #selector(retry)); menu.addItem(.separator()); item("退出", #selector(quit))
        status.menu = menu
        status.button?.toolTip = "WARP 新开关 · \(model.title)"
    }
    @objc func showWindow() { window.makeKeyAndOrderFront(nil); NSApp.activate(ignoringOtherApps: true) }
    @objc func toggle() { model.working ? model.stop() : model.start() }
    @objc func retry() { model.retry() }
    @objc func quit() { NSApp.terminate(nil) }
    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        if ending || model?.working != true { return .terminateNow }
        ending = true
        model.stop { NSApp.reply(toApplicationShouldTerminate: true) }
        return .terminateLater
    }
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { false }
}
