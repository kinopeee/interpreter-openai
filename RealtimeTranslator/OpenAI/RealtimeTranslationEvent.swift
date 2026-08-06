import Foundation

enum RealtimeTranslationOutputLanguage: String, Sendable, Equatable {
    case english = "en"
    case japanese = "ja"
}

enum RealtimeTranslationNoiseReduction: String, Sendable, Equatable, CaseIterable {
    case nearField = "near_field"
    case farField = "far_field"

    var displayName: String {
        switch self {
        case .nearField:
            return "近距離マイク"
        case .farField:
            return "会議・遠距離"
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
            return "最速（精度低め）"
        case .low:
            return "低遅延（既定）"
        case .medium:
            return "バランス"
        case .high:
            return "高精度"
        case .xhigh:
            return "最高精度"
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
        RealtimeTranslationSessionConfig(
            outputLanguage: .english,
            inputTranscriptionModel: "gpt-realtime-whisper",
            noiseReduction: noiseReduction
        )
    }

    static func englishTargetWithoutSourceTranscription(
        noiseReduction: RealtimeTranslationNoiseReduction = .farField
    ) -> RealtimeTranslationSessionConfig {
        RealtimeTranslationSessionConfig(
            outputLanguage: .english,
            inputTranscriptionModel: nil,
            noiseReduction: noiseReduction
        )
    }

    static func japaneseTargetWithoutSourceTranscription(
        noiseReduction: RealtimeTranslationNoiseReduction = .farField
    ) -> RealtimeTranslationSessionConfig {
        RealtimeTranslationSessionConfig(
            outputLanguage: .japanese,
            inputTranscriptionModel: nil,
            noiseReduction: noiseReduction
        )
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

enum RealtimeTranslationError: Error, LocalizedError, Equatable, Sendable {
    case missingAPIKey
    case notConnected
    case invalidMessage
    case authenticationFailed
    case fatalServerError(String)
    case recoverableTransportFailure(String)
    case sessionUpdateTimeout
    case closeTimeout
    case cancelled

    var errorDescription: String? {
        switch self {
        case .missingAPIKey:
            return "APIキーが設定されていません"
        case .notConnected:
            return "翻訳セッションに接続していません"
        case .invalidMessage:
            return "翻訳サーバーからの応答を解釈できません"
        case .authenticationFailed:
            return "OpenAI APIキーが無効です"
        case .fatalServerError(let message):
            return sanitizedServerMessage(message)
        case .recoverableTransportFailure:
            return "翻訳サーバーとの接続が切れました"
        case .sessionUpdateTimeout:
            return "翻訳セッションの準備がタイムアウトしました"
        case .closeTimeout:
            return "翻訳セッションの終了待ちがタイムアウトしました"
        case .cancelled:
            return "翻訳セッションがキャンセルされました"
        }
    }

    var isRecoverable: Bool {
        switch self {
        case .recoverableTransportFailure, .sessionUpdateTimeout:
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
        let codeLowered = (code ?? "")
            .trimmingCharacters(in: .whitespacesAndNewlines)
            .lowercased()
        let messageLowered = message.lowercased()

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
        let lowered = message.lowercased()
        if lowered.contains("sk-")
            || lowered.contains("api key")
            || lowered.contains("authorization")
            || lowered.contains("bearer ")
        {
            return "翻訳サーバーでエラーが発生しました"
        }
        return message.isEmpty ? "翻訳サーバーでエラーが発生しました" : message
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
