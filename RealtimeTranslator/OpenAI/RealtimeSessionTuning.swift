import Foundation

/// 録音開始時にRealtimeセッションへ渡す認識・ノイズ低減チューニング。
struct RealtimeSessionTuning: Sendable, Equatable {
    var noiseReduction: RealtimeTranslationNoiseReduction
    var transcriptionPrompt: String
    var transcriptionKeywords: [String]

    static let defaultPrompt =
        "Japanese and English conversation about software development, programming, and hackathons."

    static let defaultKeywords = [
        "ハッカソン",
        "hackathon",
        "エンジニア",
        "エンジニアリング",
        "クレジット",
        "モデル",
    ]

    static let `default` = RealtimeSessionTuning(
        noiseReduction: .farField,
        transcriptionPrompt: defaultPrompt,
        transcriptionKeywords: defaultKeywords
    )

    /// 1行1語のテキストをキーワード配列へ正規化する。
    static func parseKeywords(
        from text: String,
        limit: Int = 64
    ) -> [String] {
        var result: [String] = []
        result.reserveCapacity(min(limit, 16))
        for line in text.split(whereSeparator: \.isNewline) {
            let trimmed = line.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !trimmed.isEmpty else { continue }
            result.append(trimmed)
            if result.count >= limit {
                break
            }
        }
        return result
    }

    static func keywordsText(from keywords: [String]) -> String {
        keywords.joined(separator: "\n")
    }
}
