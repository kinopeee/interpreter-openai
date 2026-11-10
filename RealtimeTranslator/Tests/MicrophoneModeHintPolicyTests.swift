import XCTest
@testable import RealtimeTranslator

final class MicrophoneModeHintPolicyTests: XCTestCase {
    // Given: 録音中で、表示中のバナーが録音中案内と一致する
    // When: Voice Isolation の状態を判定する
    // Then: Voice Isolation が優先中ならヒントを表示する
    func testShowsHintForVoiceIsolationDuringListeningBanner() {
        XCTAssertTrue(
            MicrophoneModeHintPolicy.shouldShowHint(
                state: .listening,
                statusBanner: "Recording… please speak",
                listeningBanner: "Recording… please speak",
                prefersVoiceIsolation: true
            )
        )
    }

    // Given: 録音中で案内バナーが一致している
    // When: 標準モードの状態を判定する
    // Then: Voice Isolation のヒントは表示しない
    func testDoesNotShowHintForStandardMode() {
        XCTAssertFalse(
            MicrophoneModeHintPolicy.shouldShowHint(
                state: .listening,
                statusBanner: "Recording… please speak",
                listeningBanner: "Recording… please speak",
                prefersVoiceIsolation: false
            )
        )
    }

    // Given: Voice Isolation が優先中で案内バナーが一致している
    // When: idle、connecting、reconnecting、closing、error の状態を判定する
    // Then: 録音中以外ではヒントを表示しない
    func testDoesNotShowHintOutsideListening() {
        let states: [TranslationState] = [.idle, .connecting, .reconnecting, .closing, .error]

        for state in states {
            XCTAssertFalse(
                MicrophoneModeHintPolicy.shouldShowHint(
                    state: state,
                    statusBanner: "Recording… please speak",
                    listeningBanner: "Recording… please speak",
                    prefersVoiceIsolation: true
                ),
                "state: \(state)"
            )
        }
    }

    // Given: Voice Isolation が優先中で録音中の状態
    // When: nil、再接続、字幕上限、書き込み失敗のバナーを判定する
    // Then: 録音中案内以外ではヒントを表示しない
    func testDoesNotShowHintForOtherBanners() {
        let statusBanners = [
            nil,
            "Reconnecting…",
            SubtitleTranscriptStore.sizeLimitBanner,
            SubtitleTranscriptStore.writeFailureBanner,
        ]

        for statusBanner in statusBanners {
            XCTAssertFalse(
                MicrophoneModeHintPolicy.shouldShowHint(
                    state: .listening,
                    statusBanner: statusBanner,
                    listeningBanner: "Recording… please speak",
                    prefersVoiceIsolation: true
                ),
                "statusBanner: \(statusBanner ?? "nil")"
            )
        }
    }
}
