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
/// `onFailure` は `operation` が結果を返さずに終わったすべての場合に1回だけ呼ぶ。
/// 対象は timeout、親タスクのキャンセル、`operation` 自身のエラー。成功時は呼ばない。
/// `withThrowingTaskGroup` は throw 後も残りの子タスク完了を待つ。
/// `URLSessionWebSocketTask.send` のように Swift キャンセルを無視する I/O では、
/// `onFailure` で socket を abort しないと待ちが解けない。
enum AsyncTimeout {
    static func run<T: Sendable>(
        nanoseconds: UInt64,
        onFailure: (@Sendable () -> Void)? = nil,
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
            do {
                let result = try await group.next()!
                group.cancelAll()
                return result
            } catch {
                group.cancelAll()
                onFailure?()
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
        let session = URLSession(configuration: configuration)
        let task = session.webSocketTask(with: Self.makeHandshakeRequest(url: url, headers: headers))
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
        do {
            try await AsyncTimeout.run(
                nanoseconds: sendTimeoutNanoseconds,
                onFailure: { socket.cancel(with: .goingAway, reason: nil) }
            ) {
                do {
                    try await socket.send(.string(text))
                } catch {
                    throw Self.handshakeFailure(response: socket.response, underlying: error)
                }
            }
        } catch {
            await close()
            throw error
        }
    }

    func receive() async throws -> Data {
        guard let task else {
            throw URLSessionWebSocketTransportError.notConnected
        }
        nonisolated(unsafe) let socket = task
        let message = try await withTaskCancellationHandler {
            do {
                return try await socket.receive()
            } catch {
                throw Self.handshakeFailure(response: socket.response, underlying: error)
            }
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

    /// Authorization は URL Loading System が管理するヘッダーのため `httpAdditionalHeaders` では送られないことがある。
    /// 呼び出し元が渡したヘッダーはすべて handshake の request に載せる。
    static func makeHandshakeRequest(url: URL, headers: [String: String]) -> URLRequest {
        var request = URLRequest(url: url)
        for (name, value) in headers {
            request.setValue(value, forHTTPHeaderField: name)
        }
        return request
    }

    /// upgrade 前に失敗した（応答なし）または upgrade が成功した（101）場合はステータス不明として扱う。
    static func handshakeHTTPStatusCode(_ response: URLResponse?) -> Int? {
        guard let http = response as? HTTPURLResponse, http.statusCode != 101 else { return nil }
        return http.statusCode
    }

    /// upgrade が HTTP 応答で拒否されたときだけ、ステータスで分類した error に置き換える。
    /// ステータス不明は元の error（`URLError.badServerResponse` 等）をそのまま返す。
    /// 応答の本文・ヘッダーは参照せず、ログにはステータスコードと分類だけを出す。
    static func handshakeFailure(response: URLResponse?, underlying: Error) -> Error {
        handshakeFailure(statusCode: handshakeHTTPStatusCode(response), underlying: underlying)
    }

    static func handshakeFailure(statusCode: Int?, underlying: Error) -> Error {
        guard let statusCode else { return underlying }
        let classification = RealtimeServerErrorClassification.classifyHandshakeHTTPStatus(statusCode)
        AppLogger.realtime.notice(
            "WebSocket handshake rejected: status=\(statusCode, privacy: .public) termination=\(String(describing: classification.termination), privacy: .public)"
        )
        return classification.makeError()
    }

    func close() async {
        task?.cancel(with: .goingAway, reason: nil)
        task = nil
        session?.invalidateAndCancel()
        session = nil
    }
}
