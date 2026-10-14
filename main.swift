import Foundation
import Cocoa

func option(_ name: String) -> String? {
    let args = CommandLine.arguments
    guard let index = args.firstIndex(of: name), index + 1 < args.count else { return nil }
    return args[index + 1]
}
if CommandLine.arguments.contains("--self-test") {
    let body = Data([1] + (0..<511).map { UInt8($0 % 256) })
    var input = Data([22, 3, 1, 2, 0]); input.append(body)
    let trailer = Data([20, 3, 3, 0, 1, 1]); input.append(trailer)
    for size in [1, 16, 48, 256, 1024] {
        let parts = splitClientHello(input, chunkSize: size)!
        var rebuilt = Data()
        for part in parts.dropLast() {
            precondition(part[0] == 22 && part[1] == 3 && part[2] == 1)
            precondition(Int(part[3]) * 256 + Int(part[4]) == part.count - 5)
            rebuilt.append(part.dropFirst(5))
        }
        precondition(rebuilt == body && parts.last == trailer)
    }
    precondition(splitClientHello(input.prefix(4)) == nil)
    precondition(splitClientHello(input.prefix(100)) == nil)
    let nonHello = Data([23, 3, 3, 0, 1, 42])
    precondition(splitClientHello(nonHello) == [nonHello])
    precondition(validDomain("www.bing.com") && !validDomain("evil; command") && !validDomain("https://example.com"))
    print("PASS: TLS record reassembly, partial reads, trailing records, domain validation")
    exit(0)
}
if CommandLine.arguments.contains("--worker") {
    guard let dataPath = option("--data") else { fputs("Missing --data\n", stderr); exit(2) }
    let directory = URL(fileURLWithPath: dataPath)
    let settings = readJSON(ConnectionSettings.self, from: directory.appendingPathComponent("settings.json")) ?? ConnectionSettings()
    let helper = option("--helper") ?? Bundle.main.bundleURL.appendingPathComponent("Contents/Helpers/usque").path
    let parent = option("--parent").flatMap(Int32.init)
    let worker = ConnectionWorker(directory: directory, helper: helper, settings: settings,
                                  testOnly: CommandLine.arguments.contains("--test-only"), parent: parent)
    exit(worker.run())
}
let application = NSApplication.shared
application.setActivationPolicy(.regular)
let delegate = AppDelegate()
application.delegate = delegate
application.run()
