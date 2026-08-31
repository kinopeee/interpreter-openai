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
///
/// `withThrowingTaskGroup` は throw 後も残りの子タスク完了を待つ。
/// `URLSessionWebSocketTask.send` のように Swift キャンセルを無視する I/O では、
/// timeout 側で socket を abort しないと待ちが解けない。
enum AsyncTimeout {
    static func run<T: Sendable>(
        nanoseconds: UInt64,
        onTimeout: (@Sendable () -> Void)? = nil,
        operation: @Sendable @escaping () async throws -> T
    ) async throws -> T {
        try await withThrowingTaskGroup(of: T.self) { group in
            group.addTask {
                try await operation()
            }
            group.addTask {
                try await Task.sleep(nanoseconds: nanoseconds)
                onTimeout?()
                throw RealtimeTranslationError.recoverableTransportFailure("send timeout")
            }
            do {
                let result = try await group.next()!
                group.cancelAll()
                return result
            } catch {
                group.cancelAll()
                onTimeout?()
                throw error
            }
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
        nonisolated(unsafe) let socket = task
        let timeoutNanoseconds = sendTimeoutNanoseconds
        try await withThrowingTaskGroup(of: Void.self) { group in
            group.addTask {
                try await socket.send(.string(text))
            }
            group.addTask {
                try await Task.sleep(nanoseconds: timeoutNanoseconds)
                throw RealtimeTranslationError.recoverableTransportFailure("send timeout")
            }
            do {
                try await group.next()!
                group.cancelAll()
            } catch {
                group.cancelAll()
                // TaskGroup は残りの send 完了を待つ。Swift キャンセルでは解けないので
                // socket を abort してから throw する。
                await close()
                throw error
            }
        }
    }

    func receive() async throws -> Data {
        guard let task else {
            throw URLSessionWebSocketTransportError.notConnected
        }
        nonisolated(unsafe) let socket = task
        let message = try await withTaskCancellationHandler {
            try await socket.receive()
        } onCancel: {
            socket.cancel(with: .goingAway, reason: nil)
        }
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
