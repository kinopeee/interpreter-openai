import Foundation

enum RealtimeTranslationOutputLanguage: String, Sendable, Equatable, Hashable {
    case english = "en"
    case japanese = "ja"
    case spanish = "es"
}

enum RealtimeTranslationLane: Sendable, Equatable {
    case source
    case translation(RealtimeTranslationOutputLanguage)

    var isSource: Bool {
        if case .source = self { return true }
        return false
    }

    var target: RealtimeTranslationOutputLanguage? {
        if case .translation(let target) = self { return target }
        return nil
    }
}

enum RealtimeTranslationNoiseReduction: String, Sendable, Equatable, CaseIterable {
    case nearField = "near_field"
    case farField = "far_field"

    var displayName: String {
        switch self {
        case .nearField:
            return UiCopy.text("settings.noiseReduction.nearField")
        case .farField:
            return UiCopy.text("settings.noiseReduction.farField")
        }
    }
}

/// gpt-live-transcribe の遅延/精度トレードオフ。
enum RealtimeTranscriptionDelay: String, Sendable, Equatable, CaseIterable {
    case minimal
    case low
    case medium
    case high
    case xhigh

    var displayName: String {
        switch self {
        case .minimal:
            return UiCopy.text("settings.transcriptionDelay.minimal")
        case .low:
            return UiCopy.text("settings.transcriptionDelay.low")
        case .medium:
            return UiCopy.text("settings.transcriptionDelay.medium")
        case .high:
            return UiCopy.text("settings.transcriptionDelay.high")
        case .xhigh:
            return UiCopy.text("settings.transcriptionDelay.xhigh")
        }
    }
}

struct RealtimeTranslationSessionConfig: Sendable, Equatable {
    var outputLanguage: RealtimeTranslationOutputLanguage
    var inputTranscriptionModel: String?
    var noiseReduction: RealtimeTranslationNoiseReduction?

    static func englishTargetWithSourceTranscription(
        noiseReduction: RealtimeTranslationNoiseReduction = .farField
    ) -> RealtimeTranslationSessionConfig {
        withSourceTranscription(target: .english, noiseReduction: noiseReduction)
    }

    static func withSourceTranscription(
        target: RealtimeTranslationOutputLanguage,
        noiseReduction: RealtimeTranslationNoiseReduction = .farField
    ) -> RealtimeTranslationSessionConfig {
        RealtimeTranslationSessionConfig(
            outputLanguage: target,
            inputTranscriptionModel: "gpt-realtime-whisper",
            noiseReduction: noiseReduction
        )
    }

    static func withoutSourceTranscription(
        target: RealtimeTranslationOutputLanguage,
        noiseReduction: RealtimeTranslationNoiseReduction = .farField
    ) -> RealtimeTranslationSessionConfig {
        RealtimeTranslationSessionConfig(
            outputLanguage: target,
            inputTranscriptionModel: nil,
            noiseReduction: noiseReduction
        )
    }

    static func englishTargetWithoutSourceTranscription(
        noiseReduction: RealtimeTranslationNoiseReduction = .farField
    ) -> RealtimeTranslationSessionConfig {
        withoutSourceTranscription(target: .english, noiseReduction: noiseReduction)
    }

    static func japaneseTargetWithoutSourceTranscription(
        noiseReduction: RealtimeTranslationNoiseReduction = .farField
    ) -> RealtimeTranslationSessionConfig {
        withoutSourceTranscription(target: .japanese, noiseReduction: noiseReduction)
    }
}

enum RealtimeTranslationClientEvent: Sendable, Equatable {
    case sessionUpdate(RealtimeTranslationSessionConfig)
    case inputAudioBufferAppend(base64Audio: String)
    case sessionClose
}

enum RealtimeTranslationServerEvent: Sendable, Equatable {
    case sessionCreated
    case sessionUpdated
    case inputTranscriptDelta(delta: String, eventID: String?, elapsedMs: Int?)
    case outputTranscriptDelta(delta: String, eventID: String?, elapsedMs: Int?)
    case outputAudioDelta
    case sessionClosed
    case error(message: String, code: String?)
    case unknown(type: String)
}

enum RealtimeTranslationError: Error, LocalizedError, Equatable, Sendable, CustomStringConvertible, CustomDebugStringConvertible {
    /// 正規化済みのサーバー文言。生の資格情報は保持できない。
    struct SanitizedMessage: Sendable, Equatable, Hashable {
        let value: String

        init(_ raw: String) {
            value = RealtimeTranslationError.sanitizedServerMessage(raw)
        }
    }

    case missingAPIKey
    case notConnected
    case invalidMessage
    case authenticationFailed
    case fatalServerError(SanitizedMessage)
    case receiveOverflow
    case recoverableTransportFailure(String)
    case sessionUpdateTimeout
    case closeTimeout
    case cancelled

    /// 生のサーバー文言を渡しても保持前に正規化する。
    static func fatalServerError(_ raw: String) -> RealtimeTranslationError {
        .fatalServerError(SanitizedMessage(raw))
    }

    static var genericServerMessage: String { UiCopy.text("error.genericServer") }

    /// 接続直前のキー正規化。空は欠落、形式不正は認証失敗として送らない。
    static func requireNormalizedAPIKey(_ raw: String) throws -> String {
        switch APIKeyNormalization.normalize(raw) {
        case .empty:
            throw missingAPIKey
        case .malformed:
            throw authenticationFailed
        case .valid(let key):
            return key
        }
    }

    var errorDescription: String? {
        description(using: UserCopyStore.current)
    }

    /// `String(describing:)` / debug dump が associated value の生文言を出さないようにする。
    var description: String { errorDescription ?? "" }

    var debugDescription: String { description }

    /// 表示文言の `UserCopy` を明示する。未指定時は Current。
    func description(using copy: UserCopy) -> String {
        switch self {
        case .missingAPIKey:
            return copy.text("error.missingApiKey")
        case .notConnected:
            return copy.text("error.notConnected")
        case .invalidMessage:
            return copy.text("error.invalidMessage")
        case .authenticationFailed:
            return copy.text("error.authenticationFailed")
        case .fatalServerError(let message):
            return message.value
        case .receiveOverflow:
            return copy.text("error.receiveOverflow")
        case .recoverableTransportFailure:
            return copy.text("error.transportDisconnected")
        case .sessionUpdateTimeout:
            return copy.text("error.sessionUpdateTimeout")
        case .closeTimeout:
            return copy.text("error.closeTimeout")
        case .cancelled:
            return copy.text("error.cancelled")
        }
    }

    var isRecoverable: Bool {
        switch self {
        case .recoverableTransportFailure, .receiveOverflow, .sessionUpdateTimeout:
            return true
        case .missingAPIKey, .notConnected, .invalidMessage, .authenticationFailed,
            .fatalServerError, .closeTimeout, .cancelled:
            return false
        }
    }

    /// ランタイム / handshake の認証失敗判定。
    /// bare `auth` / `401` / `403` 部分一致は `authority` や `4010` に誤爆するため使わない。
    /// `authorization` は単語として一致し、`authority` には一致しない。
    static func isAuthenticationFailure(code: String?, message: String) -> Bool {
        let codeLowered = SecretText.normalizeForMatch(code ?? "").replacingOccurrences(of: " ", with: "")
        let messageLowered = SecretText.normalizeForMatch(message)

        if knownAuthenticationFailureCodes.contains(codeLowered) {
            return true
        }
        if codeLowered.contains("invalid_api_key")
            || codeLowered.contains("authentication")
            || codeLowered.contains("unauthorized")
            || codeLowered.contains("authorization")
        {
            return true
        }

        let authPhrases = [
            "unauthorized",
            "unauthenticated",
            "authorization",
            "invalid_api_key",
            "incorrect api key",
            "invalid api key",
            "authentication",
            "authentication failed",
            "authentication error",
            "not authenticated",
            "api key is invalid",
        ]
        if authPhrases.contains(where: { messageLowered.contains($0) }) {
            return true
        }

        // HTTP 401/403 をトークン単位で検出（4010 等の部分一致を避ける）
        return containsHTTPAuthStatus(messageLowered)
    }

    /// アラート・バナー・ログへ出してよいサーバー文言へ正規化する。
    static func sanitizedServerMessage(_ message: String) -> String {
        let lowered = SecretText.normalizeForMatch(message)
        let compact = lowered.replacingOccurrences(of: " ", with: "")
        if compact.contains("sk-")
            || lowered.contains("api key")
            || lowered.contains("authorization")
            || lowered.contains("bearer ")
        {
            return genericServerMessage
        }
        return message.isEmpty ? genericServerMessage : message
    }

    private static let knownAuthenticationFailureCodes: Set<String> = [
        "invalid_api_key",
        "invalid_auth",
        "authentication_error",
        "unauthorized",
        "unauthenticated",
        "401",
        "403",
    ]

    private static func containsHTTPAuthStatus(_ text: String) -> Bool {
        text.range(of: #"(?<![0-9])(401|403)(?![0-9])"#, options: .regularExpression) != nil
    }
}
