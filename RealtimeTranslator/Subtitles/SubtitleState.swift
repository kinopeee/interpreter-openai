import Foundation

enum SubtitleBlockState: String, Sendable, Equatable {
    case live
    case finalized
}

struct LiveSubtitle: Equatable, Sendable {
    var sourceText: String
    var translatedText: String
    var lastUpdatedAt: Date
    var state: SubtitleBlockState
    var isTranslationCurrent = false
    var canFinalize = false

    static let empty = LiveSubtitle(
        sourceText: "",
        translatedText: "",
        lastUpdatedAt: .distantPast,
        state: .live,
        isTranslationCurrent: false,
        canFinalize: false
    )

    var isEmpty: Bool {
        sourceText.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
            && translatedText.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
    }
}

struct SubtitleSnapshot: Equatable, Sendable {
    var current: LiveSubtitle
    var statusBanner: String?
    var isInvalidation = false

    static let empty = SubtitleSnapshot(
        current: .empty,
        statusBanner: nil,
        isInvalidation: false
    )

    var presentation: SubtitlePresentation {
        SubtitlePresentation(
            current: SubtitlePresentation.Block(current),
            statusBanner: statusBanner
        )
    }
}

struct SubtitlePresentation: Equatable, Sendable {
    struct Block: Equatable, Sendable {
        let sourceText: String
        let translatedText: String
        /// 未確定マーカーの有無が変わるため、確定状態も再描画の判定に含める。
        let isFinalized: Bool

        init(_ subtitle: LiveSubtitle) {
            sourceText = subtitle.sourceText
            translatedText = subtitle.translatedText
            isFinalized = subtitle.state == .finalized
        }
    }

    let current: Block
    let statusBanner: String?
}
