import Foundation

/// サーバー error イベントの扱い。`shared/fixtures/v1/server-error.json` が正本。
enum RealtimeServerErrorDisposition: Sendable, Equatable {
    /// 接続を維持し、termination も下流イベントも出さない。
    case keepAlive
    /// 既存の再接続へ倒す。
    case recover
    /// 録音を止めて Error にする。
    case halt
}

/// `error.type` / `error.code` を別々に受け取り、許可リストだけで分類する。
/// 判定に使わなかった文字列や生のサーバー文言は保持しない。
struct RealtimeServerErrorClassification: Sendable, Equatable {
    static let transportCode = "transport"

    private static let keepAliveCodes: Set<String> = ["input_audio_buffer_commit_empty"]
    private static let haltCodes: Set<String> = ["insufficient_quota", "billing_hard_limit_reached"]
    private static let recoverCodes: Set<String> = ["server_error", "rate_limit_exceeded", "session_expired"]
    private static let recoverTypes: Set<String> = ["server_error", "rate_limit_error"]

    let disposition: RealtimeServerErrorDisposition
    let termination: EventDeliveryTermination

    static func classify(errorType: String?, code: String?, message: String) -> RealtimeServerErrorClassification {
        let normalizedCode = normalize(code)
        let normalizedType = normalize(errorType)

        if normalizedCode == transportCode {
            return RealtimeServerErrorClassification(disposition: .recover, termination: .transportFailure)
        }

        // code が無い error は type を認証判定へ回す（既存の fallback と同じ範囲を守る）。
        if RealtimeTranslationError.isAuthenticationFailure(code: code ?? errorType, message: message) {
            return RealtimeServerErrorClassification(disposition: .halt, termination: .authenticationFailed)
        }

        if matches(haltCodes, normalizedCode) || matches(haltCodes, normalizedType) {
            return fatal(message)
        }

        if matches(keepAliveCodes, normalizedCode) {
            return RealtimeServerErrorClassification(disposition: .keepAlive, termination: .none)
        }

        if matches(recoverCodes, normalizedCode) || matches(recoverTypes, normalizedType) {
            return RealtimeServerErrorClassification(disposition: .recover, termination: .recoverableServerError)
        }

        return fatal(message)
    }

    /// keepAlive は例外にならないため呼び出し側で先に除外する。
    func makeError() -> RealtimeTranslationError {
        switch termination {
        case .authenticationFailed:
            return .authenticationFailed
        case .fatalServerError(let message):
            return .fatalServerError(message)
        case .recoverableServerError:
            return .recoverableServerError
        case .transportFailure:
            return .recoverableTransportFailure("server error transport failure")
        case .receiveOverflow:
            return .receiveOverflow
        case .none:
            return .recoverableTransportFailure("keep-alive server error")
        }
    }

    private static func fatal(_ message: String) -> RealtimeServerErrorClassification {
        RealtimeServerErrorClassification(
            disposition: .halt,
            termination: .fatalServerError(RealtimeTranslationError.sanitizedServerMessage(message))
        )
    }

    private static func normalize(_ value: String?) -> String {
        SecretText.normalizeForMatch(value ?? "").replacingOccurrences(of: " ", with: "")
    }

    private static func matches(_ allowlist: Set<String>, _ normalized: String) -> Bool {
        !normalized.isEmpty && allowlist.contains(normalized)
    }
}
