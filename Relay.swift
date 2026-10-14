import Foundation
import Network

// The TLS handshake bytes are unchanged. Only the record boundaries change.
// Certificate/public-key verification remains in usque, beyond this byte relay.
func splitClientHello(_ bytes: Data, chunkSize: Int = 48) -> [Data]? {
    guard bytes.count >= 5 else { return nil }
    let b = [UInt8](bytes)
    let length = Int(b[3]) * 256 + Int(b[4])
    guard b[0] == 22, length > 0, length <= 16384 else { return [bytes] }
    guard bytes.count >= length + 5 else { return nil }
    guard b[5] == 1 else { return [bytes] }
    var records: [Data] = []
    var offset = 5
    while offset < length + 5 {
        let count = min(chunkSize, length + 5 - offset)
        var part = Data([22, b[1], b[2], UInt8(count >> 8), UInt8(count & 255)])
        part.append(contentsOf: b[offset..<(offset + count)])
        records.append(part)
        offset += count
    }
    if b.count > length + 5 { records.append(Data(b[(length + 5)...])) }
    return records
}

final class HelloRelay {
    let queue = DispatchQueue(label: "warp.hello-relay")
    private var listener: NWListener?
    private var sessions: [UUID: RelaySession] = [:]
    let endpoint: String
    let fragment: Bool
    let port: UInt16

    init(endpoint: String, fragment: Bool, port: UInt16 = 18443) {
        self.endpoint = endpoint; self.fragment = fragment; self.port = port
    }
    func start() throws {
        let tcp = NWProtocolTCP.Options(); tcp.noDelay = true
        let params = NWParameters(tls: nil, tcp: tcp)
        params.requiredLocalEndpoint = .hostPort(host: "127.0.0.1", port: NWEndpoint.Port(rawValue: port)!)
        let server = try NWListener(using: params)
        listener = server
        server.newConnectionHandler = { [weak self] client in
            guard let self else { client.cancel(); return }
            let id = UUID()
            let session = RelaySession(client: client, endpoint: self.endpoint, fragment: self.fragment,
                                       queue: self.queue) { [weak self] in self?.sessions.removeValue(forKey: id) }
            self.sessions[id] = session
            session.start()
        }
        let ready = DispatchSemaphore(value: 0)
        var failure: Error?
        server.stateUpdateHandler = { state in
            if case .ready = state { ready.signal() }
            if case .failed(let error) = state { failure = error; ready.signal() }
        }
        server.start(queue: queue)
        if ready.wait(timeout: .now() + 4) == .timedOut {
            server.cancel(); throw NSError(domain: "Relay", code: 1, userInfo: [NSLocalizedDescriptionKey: "本机转发端口启动超时"])
        }
        if let failure { throw failure }
    }
    func stop() {
        queue.sync {
            listener?.cancel(); listener = nil
            let active = Array(sessions.values)
            active.forEach { $0.close() }
            sessions.removeAll()
        }
    }
}

private final class RelaySession {
    let client: NWConnection
    let upstream: NWConnection
    let fragment: Bool
    let queue: DispatchQueue
    let onClose: () -> Void
    var closed = false
    var first = true
    var buffered = Data()
    var generation = 0

    init(client: NWConnection, endpoint: String, fragment: Bool, queue: DispatchQueue, onClose: @escaping () -> Void) {
        self.client = client; self.fragment = fragment; self.queue = queue; self.onClose = onClose
        let tcp = NWProtocolTCP.Options(); tcp.noDelay = true
        self.upstream = NWConnection(host: NWEndpoint.Host(endpoint), port: 443,
                                     using: NWParameters(tls: nil, tcp: tcp))
    }
    func start() {
        client.stateUpdateHandler = { [weak self] state in
            if case .failed = state { self?.close() }
        }
        upstream.stateUpdateHandler = { [weak self] state in
            guard let self else { return }
            switch state {
            case .ready: self.receiveClient(); self.receiveServer()
            case .failed: self.close()
            default: break
            }
        }
        client.start(queue: queue); upstream.start(queue: queue)
        queue.asyncAfter(deadline: .now() + 12) { [weak self] in
            if let self, self.first { self.close() }
        }
    }
    func close() {
        guard !closed else { return }
        closed = true
        client.stateUpdateHandler = nil; upstream.stateUpdateHandler = nil
        client.cancel(); upstream.cancel(); onClose()
    }
    func receiveClient() {
        guard !closed else { return }
        client.receive(minimumIncompleteLength: 1, maximumLength: 65536) { [weak self] data, _, complete, error in
            guard let self, !self.closed else { return }
            if error != nil { self.close(); return }
            guard let data, !data.isEmpty else {
                if complete { self.close() } else { self.receiveClient() }; return
            }
            var pieces = [data]
            if self.first && self.fragment {
                self.buffered.append(data)
                guard let split = splitClientHello(self.buffered) else {
                    if complete { self.close() } else { self.receiveClient() }; return
                }
                pieces = split; self.buffered.removeAll()
            }
            self.first = false
            self.send(pieces, index: 0, delay: self.fragment && pieces.count > 1, complete: complete)
        }
    }
    func send(_ pieces: [Data], index: Int, delay: Bool, complete: Bool) {
        guard !closed else { return }
        guard index < pieces.count else {
            if complete {
                upstream.send(content: nil, contentContext: .finalMessage, isComplete: true, completion: .contentProcessed { _ in })
            } else { receiveClient() }
            return
        }
        upstream.send(content: pieces[index], completion: .contentProcessed { [weak self] error in
            guard let self else { return }
            if error != nil { self.close(); return }
            if delay {
                self.queue.asyncAfter(deadline: .now() + .milliseconds(Int.random(in: 2...6))) {
                    self.send(pieces, index: index + 1, delay: delay, complete: complete)
                }
            } else { self.send(pieces, index: index + 1, delay: delay, complete: complete) }
        })
    }
    func receiveServer() {
        guard !closed else { return }
        upstream.receive(minimumIncompleteLength: 1, maximumLength: 65536) { [weak self] data, _, complete, error in
            guard let self, !self.closed else { return }
            if error != nil { self.close(); return }
            self.client.send(content: data, contentContext: complete ? .finalMessage : .defaultMessage,
                             isComplete: complete, completion: .contentProcessed { [weak self] error in
                guard let self else { return }
                if error != nil || complete { self.close() } else { self.receiveServer() }
            })
        }
    }
}
