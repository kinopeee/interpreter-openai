import Foundation

enum RealtimeTranslationOutputLanguage: String, Sendable, Equatable {
    case english = "en"
    case japanese = "ja"
}

enum RealtimeTranslationNoiseReduction: String, Sendable, Equatable {
    case nearField = "near_field"
    case farField = "far_field"
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
            return message.isEmpty ? "翻訳サーバーでエラーが発生しました" : message
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
}
