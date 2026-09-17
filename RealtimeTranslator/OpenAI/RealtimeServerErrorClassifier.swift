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
        classifyCore(errorType: errorType, code: code, message: message, fallback: .halt)
    }

    static func classifyTranscriptionFailure(
        errorType: String?,
        code: String?
    ) -> RealtimeServerErrorClassification {
        classifyCore(errorType: errorType, code: code, message: "", fallback: .keepAlive)
    }

    private static func classifyCore(
        errorType: String?,
        code: String?,
        message: String,
        fallback: RealtimeServerErrorDisposition
    ) -> RealtimeServerErrorClassification {
        let normalizedCode = normalize(code)
        let normalizedType = normalize(errorType)

        // 認証失敗は code に関わらず最優先（transport 扱いで再接続に回さない）。
        // code と type は独立して認証判定に回す（type だけに根拠がある場合も拾う）。
        if RealtimeTranslationError.isAuthenticationFailure(code: code, message: message)
            || RealtimeTranslationError.isAuthenticationFailure(code: errorType, message: message)
        {
            return RealtimeServerErrorClassification(disposition: .halt, termination: .authenticationFailed)
        }

        if normalizedCode == transportCode {
            return RealtimeServerErrorClassification(disposition: .recover, termination: .transportFailure)
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

        return fallback == .keepAlive
            ? RealtimeServerErrorClassification(disposition: .keepAlive, termination: .none)
            : fatal(message)
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
