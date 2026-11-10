import XCTest
@testable import RealtimeTranslator

final class SubtitleTranscriptRecordingPolicyTests: XCTestCase {
    private let states: [TranslationState] = [
        .idle,
        .connecting,
        .listening,
        .reconnecting,
        .closing,
        .error,
    ]

    // Given: 記録が有効で、空でない確定字幕
    // When: idle を含む各翻訳状態で記録可否を判定する
    // Then: idle 以外では記録し、idle では記録しない
    func testFinalizedSubtitleRecordsOnlyOutsideIdle() {
        let subtitle = Self.subtitle(sourceText: "こんにちは", translatedText: "Hello")
        let states: [(TranslationState, Bool)] = [
            (.connecting, true),
            (.listening, true),
            (.reconnecting, true),
            (.closing, true),
            (.error, true),
            (.idle, false),
        ]

        for (state, expected) in states {
            XCTAssertEqual(
                SubtitleTranscriptRecordingPolicy.shouldRecord(
                    subtitle: subtitle,
                    recordSubtitles: true,
                    state: state
                ),
                expected,
                "state: \(state)"
            )
        }
    }

    // Given: 記録が有効で、空でない未確定字幕
    // When: すべての翻訳状態で記録可否を判定する
    // Then: どの状態でも記録しない
    func testLiveSubtitleIsNeverRecorded() {
        let subtitle = Self.subtitle(sourceText: "こんにちは", translatedText: "Hello", state: .live)

        for state in states {
            XCTAssertFalse(
                SubtitleTranscriptRecordingPolicy.shouldRecord(
                    subtitle: subtitle,
                    recordSubtitles: true,
                    state: state
                ),
                "state: \(state)"
            )
        }
    }

    // Given: 記録が有効で、本文が空の確定字幕
    // When: すべての翻訳状態で記録可否を判定する
    // Then: どの状態でも記録しない
    func testEmptySubtitleIsNeverRecorded() {
        let subtitle = Self.subtitle(sourceText: " \n", translatedText: "\t")

        for state in states {
            XCTAssertFalse(
                SubtitleTranscriptRecordingPolicy.shouldRecord(
                    subtitle: subtitle,
                    recordSubtitles: true,
                    state: state
                ),
                "state: \(state)"
            )
        }
    }

    // Given: 記録が無効で、空でない確定字幕
    // When: すべての翻訳状態で記録可否を判定する
    // Then: どの状態でも記録しない
    func testRecordingOffNeverRecordsFinalizedSubtitle() {
        let subtitle = Self.subtitle(sourceText: "こんにちは", translatedText: "Hello")

        for state in states {
            XCTAssertFalse(
                SubtitleTranscriptRecordingPolicy.shouldRecord(
                    subtitle: subtitle,
                    recordSubtitles: false,
                    state: state
                ),
                "state: \(state)"
            )
        }
    }

    private static func subtitle(
        sourceText: String,
        translatedText: String,
        state: SubtitleBlockState = .finalized
    ) -> LiveSubtitle {
        LiveSubtitle(
            sourceText: sourceText,
            translatedText: translatedText,
            lastUpdatedAt: .distantPast,
            state: state,
            isTranslationCurrent: true,
            canFinalize: true
        )
    }
}
