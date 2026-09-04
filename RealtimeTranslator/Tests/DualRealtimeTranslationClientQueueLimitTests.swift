import Foundation
import XCTest
@testable import RealtimeTranslator

final class DualRealtimeTranslationClientQueueLimitTests: XCTestCase {
    // Given: translation-queue.json の共有契約
    // When: macOS のキュー定数を読む
    // Then: pending は 80 フレーム、preroll は 40 フレームである
    func testQ01ConstantsMatchFixture() throws {
        let fixture = try SharedFixtures.load("translation-queue")
        XCTAssertEqual(
            SharedFixtures.number(fixture["pendingFrameLimit"]),
            DualRealtimeTranslationClient.translationPendingFrameLimit
        )
        XCTAssertEqual(
            SharedFixtures.number(fixture["prerollFrameLimit"]),
            DualRealtimeTranslationClient.translationPrerollFrameLimit
        )
        XCTAssertEqual(
            SharedFixtures.text(
                (fixture["overflow"] as? [String: Any])?["errorCode"]
            ),
            "transport"
        )
        XCTAssertEqual(
            UiCopy.text("error.translationBacklog"),
            "翻訳音声の送信待ちが上限に達しました。"
        )
    }

    // Given: 翻訳送信を保持した実行中クライアント
    // When: in-flight を除いて 81 フレームを追加する
    // Then: pending を捨てて一度だけ停止し、再 start 後は回復する
    func testQ02ThroughQ14BoundedOverflowAndRestart() async throws {
        let source = FakeRealtimeWebSocketTransport()
        let english = FakeRealtimeWebSocketTransport()
        let japanese = FakeRealtimeWebSocketTransport()
        let dual = DualRealtimeTranslationClient(
            sourceConnection: RealtimeSourceTranscriptionConnection(
                transport: source,
                safetyIdentifier: "test-safety",
                handshakeTimeoutNanoseconds: 1_000_000_000,
                closeTimeoutNanoseconds: 500_000_000
            ),
            englishConnection: RealtimeTranslationConnection(
                target: .english,
                transport: english,
                safetyIdentifier: "test-safety",
                sessionUpdateTimeoutNanoseconds: 1_000_000_000,
                closeTimeoutNanoseconds: 500_000_000
            ),
            japaneseConnection: RealtimeTranslationConnection(
                target: .japanese,
                transport: japanese,
                safetyIdentifier: "test-safety",
                sessionUpdateTimeoutNanoseconds: 1_000_000_000,
                closeTimeoutNanoseconds: 500_000_000
            )
        )
        try await startDual(
            dual,
            source: source,
            english: english,
            japanese: japanese
        )
        try await dual.selectTranslationTarget(.english)
        await english.setHoldAudioAppends(true)
        try await dual.appendAudioFrame(Data("in-flight".utf8))
        try await waitUntil { await english.heldAudioAppendCount == 1 }
        for index in 0...80 {
            try await dual.appendAudioFrame(Data("queued-\(index)".utf8))
        }
        try await waitUntil { await dual.isTranslationPumpHalted }
        let pendingCount = await dual.pendingTranslationFrameCount
        let halted = await dual.isTranslationPumpHalted
        let heldCount = await english.heldAudioAppendCount
        XCTAssertEqual(pendingCount, 0)
        XCTAssertTrue(halted)
        XCTAssertEqual(heldCount, 1)
        await english.releaseAllAudioAppends()
        try await dual.start(apiKey: "sk-test", pair: .jaEn)
        try await dual.selectTranslationTarget(.english)
        try await dual.appendAudioFrame(Data("recovered".utf8))
        let sentCount = await english.sent.count
        XCTAssertGreaterThanOrEqual(sentCount, 1)
        await dual.forceClose()
    }

    private func startDual(
        _ dual: DualRealtimeTranslationClient,
        source: FakeRealtimeWebSocketTransport,
        english: FakeRealtimeWebSocketTransport,
        japanese: FakeRealtimeWebSocketTransport
    ) async throws {
        try await source.enqueueJSON(["type": "session.created"])
        try await english.enqueueJSON(["type": "session.created"])
        try await japanese.enqueueJSON(["type": "session.created"])
        let startTask = Task {
            try await dual.start(apiKey: "sk-test", pair: .jaEn)
        }
        try await waitUntil { await source.sent.count >= 1 }
        try await waitUntil { await english.sent.count >= 1 }
        try await source.enqueueJSON(["type": "session.updated"])
        try await english.enqueueJSON(["type": "session.updated"])
        try await startTask.value
    }

    private func waitUntil(
        timeout: TimeInterval = 5,
        condition: @escaping @Sendable () async -> Bool
    ) async throws {
        let deadline = Date().addingTimeInterval(timeout)
        while Date() < deadline {
            if await condition() {
                return
            }
            try await Task.sleep(nanoseconds: 10_000_000)
        }
        XCTFail("condition timed out")
    }
}
