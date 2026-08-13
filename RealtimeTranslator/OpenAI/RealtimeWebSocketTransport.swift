import Foundation

protocol RealtimeWebSocketTransport: AnyObject, Sendable {
    func connect(
        url: URL,
        headers: [String: String]
    ) async throws
    func send(_ data: Data) async throws
    func receive() async throws -> Data
    func close() async
}

enum URLSessionWebSocketTransportError: Error, LocalizedError, Sendable {
    case notConnected
    case unsupportedMessage

    var errorDescription: String? {
        switch self {
        case .notConnected:
            return UiCopy.text("error.websocketNotConnected")
        case .unsupportedMessage:
            return UiCopy.text("error.websocketUnsupported")
        }
    }
}

/// WebSocket送信などの無期限待ちを防ぐためのtimeoutヘルパー。
enum AsyncTimeout {
    static func run<T: Sendable>(
        nanoseconds: UInt64,
        operation: @Sendable @escaping () async throws -> T
    ) async throws -> T {
        try await withThrowingTaskGroup(of: T.self) { group in
            group.addTask {
                try await operation()
            }
            group.addTask {
                try await Task.sleep(nanoseconds: nanoseconds)
                throw RealtimeTranslationError.recoverableTransportFailure("send timeout")
            }
            let result = try await group.next()!
            group.cancelAll()
            return result
        }
    }
}

actor URLSessionWebSocketTransport: RealtimeWebSocketTransport {
    private static let defaultSendTimeoutNanoseconds: UInt64 = 5_000_000_000

    private var session: URLSession?
    private var task: URLSessionWebSocketTask?
    private let sendTimeoutNanoseconds: UInt64

    init(sendTimeoutNanoseconds: UInt64 = defaultSendTimeoutNanoseconds) {
        self.sendTimeoutNanoseconds = sendTimeoutNanoseconds
    }

    func connect(
        url: URL,
        headers: [String: String]
    ) async throws {
        await close()

        let configuration = URLSessionConfiguration.ephemeral
        configuration.timeoutIntervalForRequest = 30
        configuration.httpAdditionalHeaders = headers
        let session = URLSession(configuration: configuration)
        let task = session.webSocketTask(with: url)
        self.session = session
        self.task = task
        task.resume()
    }

    func send(_ data: Data) async throws {
        guard let task else {
            throw URLSessionWebSocketTransportError.notConnected
        }
        guard let text = String(data: data, encoding: .utf8) else {
            throw RealtimeTranslationError.invalidMessage
        }
        try await AsyncTimeout.run(nanoseconds: sendTimeoutNanoseconds) {
            try await task.send(.string(text))
        }
    }

    func receive() async throws -> Data {
        guard let task else {
            throw URLSessionWebSocketTransportError.notConnected
        }
        let message = try await task.receive()
        switch message {
        case .string(let text):
            guard let data = text.data(using: .utf8) else {
                throw RealtimeTranslationError.invalidMessage
            }
            return data
        case .data(let data):
            return data
        @unknown default:
            throw URLSessionWebSocketTransportError.unsupportedMessage
        }
    }

    func close() async {
        task?.cancel(with: .goingAway, reason: nil)
        task = nil
        session?.invalidateAndCancel()
        session = nil
    }
}
