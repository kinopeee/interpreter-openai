import Foundation

/// 文字種から推定した言語の証拠。
///
/// `ambiguousLatin` はラテン文字1語だけの場合。日本語話者のローマ字発話や
/// 固有名詞の可能性があり、英語と断定できないため `english` と区別する。
enum SpokenLanguageEvidence: Equatable, Sendable {
    case japanese
    case english
    case ambiguousLatin
    case none
}

/// テキストの文字種(ひらがな・カタカナ・漢字・ラテン文字)から話者言語を推定する。
enum SpokenLanguageDetector {
    /// 言語切替検出用の末尾 Unicode scalar 数（空白除く）。
    static let recentEvidenceWindow = 16

    static func detect(_ text: String) -> SpokenLanguage {
        switch evidence(in: text) {
        case .japanese:
            return .japanese
        case .english:
            return .english
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
        window: Int = recentEvidenceWindow
    ) -> SpokenLanguageEvidence {
        guard window > 0, !text.isEmpty else {
            return evidence(in: text)
        }

        let scalars = text.unicodeScalars
        var nonWhitespaceCount = 0
        var start = scalars.endIndex
        while start > scalars.startIndex, nonWhitespaceCount < window {
            start = scalars.index(before: start)
            let scalar = scalars[start]
            if !CharacterSet.whitespacesAndNewlines.contains(scalar) {
                nonWhitespaceCount += 1
            }
        }

        guard nonWhitespaceCount > 0 else {
            return .none
        }
        return evidence(in: String(scalars[start...]))
    }

    static func evidence(in text: String) -> SpokenLanguageEvidence {
        var hasJapanese = false
        var latinWordCount = 0
        var isInsideLatinWord = false

        for scalar in text.unicodeScalars {
            switch scalar.value {
            case 0x3040...0x30FF, 0x3400...0x4DBF, 0x4E00...0x9FFF:
                hasJapanese = true
                isInsideLatinWord = false
            case 0x0041...0x005A, 0x0061...0x007A:
                if !isInsideLatinWord {
                    latinWordCount += 1
                    isInsideLatinWord = true
                }
            default:
                isInsideLatinWord = false
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
            return .english
        }
    }
}
