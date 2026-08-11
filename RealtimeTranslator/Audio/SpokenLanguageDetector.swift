import Foundation

/// 文字種から推定した言語の証拠。
///
/// `ambiguousLatin` はラテン文字1語だけの場合。日本語話者のローマ字発話や
/// 固有名詞の可能性があり、英語と断定できないため `english` と区別する。
enum SpokenLanguageEvidence: Equatable, Sendable {
    case japanese
    case english
    case spanish
    case ambiguousLatin
    case none
}

/// テキストの文字種(ひらがな・カタカナ・漢字・ラテン文字)から話者言語を推定する。
enum SpokenLanguageDetector {
    /// 言語切替検出用の末尾 Unicode scalar 数（空白除く）。
    static let recentEvidenceWindow = 16
    static let enEsWindow = 8

    static let spanishExclusiveWords = [
        "el", "la", "los", "las", "es", "está", "que", "y",
        "de", "del", "con", "por", "para", "pero", "más", "sí",
    ]
    static let englishExclusiveWords = [
        "the", "and", "is", "are", "of", "to", "it", "that",
        "this", "with", "for", "you", "they",
    ]

    static func detect(_ text: String, pair: LanguagePair) -> SpokenLanguage {
        switch evidence(in: text, pair: pair) {
        case .japanese:
            return .japanese
        case .english:
            return .english
        case .spanish:
            return .spanish
        case .ambiguousLatin, .none:
            return .unknown
        }
    }

    /// 空白を除いた末尾N個の Unicode scalar（code point）分の範囲だけで証拠を評価する。
    /// 空白 scalar は語境界判定のため残す。日本語がウィンドウ外へ流れ出ると英語切替を検出できる。
    /// 単位は Swift `Character` / UTF-16 `char` ではなく Unicode scalar（shared/protocol/routing.md 正本）。
    /// 全文コピーはせず、`unicodeScalars` の index を末尾から戻して部分文字列だけ評価する。
    static func recentEvidence(
        in text: String,
        pair: LanguagePair,
        window: Int? = nil
    ) -> SpokenLanguageEvidence {
        let effectiveWindow = window ?? (pair == .enEs ? enEsWindow : recentEvidenceWindow)
        guard effectiveWindow > 0, !text.isEmpty else {
            return evidence(in: text, pair: pair)
        }

        if pair == .enEs {
            let spans = wordSpans(in: text)
            guard spans.count > effectiveWindow else {
                return evidence(in: text, pair: pair)
            }
            var start = spans[spans.count - effectiveWindow].start
            while start > text.unicodeScalars.startIndex {
                let previous = text.unicodeScalars.index(before: start)
                guard text.unicodeScalars[previous].value == 0x00BF
                    || text.unicodeScalars[previous].value == 0x00A1
                else { break }
                start = previous
            }
            let end = spans[spans.count - 1].end
            return evidence(
                in: String(text.unicodeScalars[start..<end]),
                pair: pair
            )
        }

        let scalars = text.unicodeScalars
        var nonWhitespaceCount = 0
        var start = scalars.endIndex
        while start > scalars.startIndex, nonWhitespaceCount < effectiveWindow {
            start = scalars.index(before: start)
            let scalar = scalars[start]
            if !CharacterSet.whitespacesAndNewlines.contains(scalar) {
                nonWhitespaceCount += 1
            }
        }

        guard nonWhitespaceCount > 0 else {
            return .none
        }
        return evidence(in: String(scalars[start...]), pair: pair)
    }

    static func evidence(in text: String, pair: LanguagePair) -> SpokenLanguageEvidence {
        if pair == .enEs {
            return evidenceEnEs(in: text)
        }

        var hasJapanese = false
        var latinWordCount = 0
        var isInsideLatinWord = false

        for scalar in text.unicodeScalars {
            switch scalar.value {
            case 0x3040...0x30FF, 0x3400...0x4DBF, 0x4E00...0x9FFF:
                hasJapanese = true
                isInsideLatinWord = false
            default:
                if isLatinWordScalar(scalar) {
                    if !isInsideLatinWord {
                        latinWordCount += 1
                        isInsideLatinWord = true
                    }
                } else {
                    isInsideLatinWord = false
                }
            }
        }

        if hasJapanese {
            return .japanese
        }
        switch latinWordCount {
        case 0:
            return .none
        case 1:
            return .ambiguousLatin
        default:
            return pair == .jaEs ? .spanish : .english
        }
    }

    private static func evidenceEnEs(in text: String) -> SpokenLanguageEvidence {
        let words = wordStrings(in: text)
        if text.unicodeScalars.contains(where: {
            $0.value == 0x00BF || $0.value == 0x00A1 || $0.value == 0x00F1 || $0.value == 0x00D1
        }) {
            return .spanish
        }

        var spanishScore = 0
        var englishScore = 0
        for word in words {
            let lower = word.lowercased()
            if spanishExclusiveWords.contains(lower) {
                spanishScore += 1
            }
            if englishExclusiveWords.contains(lower) {
                englishScore += 1
            }
            if word.unicodeScalars.contains(where: {
                [0x00E1, 0x00E9, 0x00ED, 0x00F3, 0x00FA, 0x00FC,
                 0x00C1, 0x00C9, 0x00CD, 0x00D3, 0x00DA, 0x00DC].contains($0.value)
            }) {
                spanishScore += 2
            }
        }

        guard abs(spanishScore - englishScore) >= 2 else {
            return .ambiguousLatin
        }
        return spanishScore > englishScore ? .spanish : .english
    }

    private struct WordSpan {
        let start: String.UnicodeScalarView.Index
        let end: String.UnicodeScalarView.Index
    }

    private static func wordSpans(in text: String) -> [WordSpan] {
        let scalars = text.unicodeScalars
        var spans: [WordSpan] = []
        var start: String.UnicodeScalarView.Index?
        var index = scalars.startIndex
        while index < scalars.endIndex {
            let scalar = scalars[index]
            if isLatinWordScalar(scalar) {
                start = start ?? index
            } else if let wordStart = start {
                spans.append(WordSpan(start: wordStart, end: index))
                start = nil
            }
            index = scalars.index(after: index)
        }
        if let wordStart = start {
            spans.append(WordSpan(start: wordStart, end: scalars.endIndex))
        }
        return spans
    }

    private static func wordStrings(in text: String) -> [String] {
        wordSpans(in: text).map { String(text.unicodeScalars[$0.start..<$0.end]) }
    }

    private static func isLatinWordScalar(_ scalar: Unicode.Scalar) -> Bool {
        switch scalar.value {
        case 0x0041...0x005A, 0x0061...0x007A,
             0x00C0...0x00D6, 0x00D8...0x00F6, 0x00F8...0x00FF:
            return true
        default:
            return false
        }
    }
}
