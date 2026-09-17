import Foundation

/// 字幕表示用に末尾N文字だけを残す。SwiftUIの head truncation は
/// 1行目を先頭固定にしたまま2行目だけを省略するため、表示前に切り詰める。
enum SubtitleTailClipper {
    static let japaneseCharacterLimit = 60
    static let englishCharacterLimit = 120
    static let ellipsis = "…"

    static func clip(_ text: String) -> String {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return text }

        let limit =
            containsCJK(trimmed)
            ? japaneseCharacterLimit
            : englishCharacterLimit
        guard trimmed.count > limit else { return trimmed }

        var tail = String(trimmed.suffix(limit))
        if !containsCJK(trimmed) {
            tail = dropLeadingPartialWord(from: tail)
        }
        guard !tail.isEmpty else {
            return ellipsis + String(trimmed.suffix(limit))
        }
        return ellipsis + tail
    }

    static func containsCJK(_ text: String) -> Bool {
        text.unicodeScalars.contains { scalar in
            (0x3040...0x30FF).contains(scalar.value)
                || (0x4E00...0x9FFF).contains(scalar.value)
                || (0x3400...0x4DBF).contains(scalar.value)
        }
    }

    /// 英語の単語途中切れを避けるため、先頭から最初の空白までを捨てる。
    private static func dropLeadingPartialWord(from text: String) -> String {
        guard let firstSpace = text.firstIndex(where: { $0.isWhitespace }) else {
            return text
        }
        let afterSpace = text.index(after: firstSpace)
        guard afterSpace < text.endIndex else { return text }
        return String(text[afterSpace...])
    }
}
