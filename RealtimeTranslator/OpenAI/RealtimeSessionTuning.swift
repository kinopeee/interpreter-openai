import Foundation

/// 録音開始時にRealtimeセッションへ渡す認識・ノイズ低減チューニング。
struct RealtimeSessionTuning: Sendable, Equatable {
    /// OpenAIがkeywords内で拒否する文字。session.update全体が失敗するため除去する。
    static let forbiddenKeywordCharacters = CharacterSet(charactersIn: "<>")
    static let keywordLimit = 64
    /// prompt長さ上限の安全マージン。超過するとsession.updateが拒否される。
    static let promptCharacterLimit = 1_000

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

    /// 用途別の認識ヒントプリセット。
    struct Preset: Sendable, Equatable, Identifiable {
        let id: String
        let displayName: String
        let prompt: String
        let keywords: [String]

        static let softwareDevelopment = Preset(
            id: "software_development",
            displayName: "ソフトウェア開発",
            prompt: defaultPrompt,
            keywords: defaultKeywords
        )

        static let businessMeeting = Preset(
            id: "business_meeting",
            displayName: "ビジネス会議",
            prompt:
                "Japanese and English business meeting about agenda, decisions, schedule, and action items.",
            keywords: [
                "アジェンダ",
                "agenda",
                "議事録",
                "アクションアイテム",
                "action item",
                "スケジュール",
                "決定事項",
                "ステークホルダー",
                "stakeholder",
                "フォローアップ",
            ]
        )

        static let hackathon = Preset(
            id: "hackathon",
            displayName: "ハッカソン",
            prompt:
                "Japanese and English hackathon conversation about demos, judging, teams, and pitches.",
            keywords: [
                "ハッカソン",
                "hackathon",
                "デモ",
                "demo",
                "ピッチ",
                "pitch",
                "審査",
                "ジャッジ",
                "チーム",
                "プロトタイプ",
                "prototype",
            ]
        )

        static let all: [Preset] = [
            .softwareDevelopment,
            .businessMeeting,
            .hackathon,
        ]
    }

    /// 1行1語のテキストをキーワード配列へ正規化する。
    /// trim後に `<` `>` を除去し、空になった行は捨てる。
    static func parseKeywords(
        from text: String,
        limit: Int = keywordLimit
    ) -> [String] {
        var result: [String] = []
        result.reserveCapacity(min(limit, 16))
        for line in text.split(whereSeparator: \.isNewline) {
            let trimmed = line.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !trimmed.isEmpty else { continue }
            let sanitized = trimmed.unicodeScalars
                .filter { !forbiddenKeywordCharacters.contains($0) }
                .map(String.init)
                .joined()
                .trimmingCharacters(in: .whitespacesAndNewlines)
            guard !sanitized.isEmpty else { continue }
            result.append(sanitized)
            if result.count >= limit {
                break
            }
        }
        return result
    }

    /// promptを送信可能な形へ正規化する。改行→空白、trim、文字数上限。
    static func sanitizedPrompt(_ text: String) -> String {
        let collapsed = text
            .replacingOccurrences(of: "\r\n", with: " ")
            .replacingOccurrences(of: "\n", with: " ")
            .replacingOccurrences(of: "\r", with: " ")
            .trimmingCharacters(in: .whitespacesAndNewlines)
        guard collapsed.count > promptCharacterLimit else { return collapsed }
        let end = collapsed.index(collapsed.startIndex, offsetBy: promptCharacterLimit)
        return String(collapsed[..<end])
    }

    static func keywordsText(from keywords: [String]) -> String {
        keywords.joined(separator: "\n")
    }

    /// 生の設定値から送信用tuningを組み立てる。
    static func make(
        noiseReduction: RealtimeTranslationNoiseReduction,
        prompt: String,
        keywordsText: String
    ) -> RealtimeSessionTuning {
        RealtimeSessionTuning(
            noiseReduction: noiseReduction,
            transcriptionPrompt: sanitizedPrompt(prompt),
            transcriptionKeywords: parseKeywords(from: keywordsText)
        )
    }
}
