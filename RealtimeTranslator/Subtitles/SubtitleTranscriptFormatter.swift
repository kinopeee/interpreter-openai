import Foundation

/// 字幕セッション記録の上限とユーザー向けバナー（本文を含めない）。
enum SubtitleTranscriptLimits {
    static let maxFileBytes = 10 * 1024 * 1024
    static var sizeLimitBanner: String { UiCopy.text("transcript.sizeLimitBanner") }
    static var writeFailureBanner: String { UiCopy.text("transcript.writeFailureBanner") }
}

/// 字幕セッション記録のプレーンテキスト整形。時刻文字列は呼び出し側が渡す。
enum SubtitleTranscriptFormatter {
    static func formatEntry(
        timestamp: String,
        sourceText: String,
        translatedText: String
    ) -> String {
        "--- \(timestamp)\n原文: \(sourceText)\n訳文: \(translatedText)\n\n"
    }

    static func formatSessionStart(timestamp: String) -> String {
        "=== 録音開始 \(timestamp)\n\n"
    }

    /// ローカルオフセット付き ISO8601（`yyyy-MM-dd'T'HH:mm:ssXXXXX`）。
    static func formatTimestamp(_ date: Date, timeZone: TimeZone = .current) -> String {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.timeZone = timeZone
        formatter.dateFormat = "yyyy-MM-dd'T'HH:mm:ssXXXXX"
        return formatter.string(from: date)
    }
}
