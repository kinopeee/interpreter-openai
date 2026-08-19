import Foundation
import OSLog

enum AppLogger {
    private static let subsystem = Bundle.main.bundleIdentifier ?? "com.realtimetranslator.app"
    static let redactedPlaceholder = "[redacted]"

    static let general = Logger(subsystem: subsystem, category: "general")
    static let audio = Logger(subsystem: subsystem, category: "audio")
    static let realtime = Logger(subsystem: subsystem, category: "realtime")
    static let subtitle = Logger(subsystem: subsystem, category: "subtitle")
    static let session = Logger(subsystem: subsystem, category: "session")

    /// 秘密になりうる断片を伏字化する。ログ出力前に必ず通す。
    ///
    /// Swift の `NSRegularExpression` にはタイムアウト API が無いため、
    /// バックトラック爆発しない線形パターンだけを使う（Windows 版の matchTimeout 相当）。
    static func redact(_ message: String) -> String {
        var redacted = SecretText.stripFormatAndControl(message)
        for pattern in secretPatterns {
            redacted = pattern.stringByReplacingMatches(
                in: redacted,
                options: [],
                range: NSRange(redacted.startIndex..., in: redacted),
                withTemplate: redactedPlaceholder
            )
        }
        return redacted
    }

    private static let secretPatterns: [NSRegularExpression] = [
        try! NSRegularExpression(pattern: #"(?i)(?<![A-Za-z0-9])s\s*k-[A-Za-z0-9_\-]{4,}"#),
        try! NSRegularExpression(pattern: #"(?i)bearer\s+\S+"#),
        try! NSRegularExpression(pattern: #"(?i)authorization\s*:\s*[^\r\n]*"#),
        try! NSRegularExpression(pattern: #"(?i)openai-safety-identifier:\s*\S+"#),
        try! NSRegularExpression(
            pattern: #"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}"#
        ),
    ]
}
