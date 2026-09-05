import XCTest
@testable import RealtimeTranslator

@MainActor
final class InterpretationSessionReceiveOverflowTests: XCTestCase {
    // Given: Listening 中に未確定の字幕ペアを受信した session
    // When: 現在 epoch の merge delivery に loss を記録する
    // Then: 無効化を 1 回だけ通知し、再接続して Listening に戻る
    func testCurrentEpochOverflowInvalidatesAndReconnects() async {
        let dual = FakeDualRealtimeTranslationClient()
        let delegate = InterpretationSessionDelegateSpy()
        let session = InterpretationSession(
            apiKeyStore: InMemoryAPIKeyStore(initialKey: "sk-test"),
            audioCapture: FakeRealtimeAudioCaptureService(),
            dualClient: dual
        )
        session.delegate = delegate

        await session.start()
        await waitForCondition { session.state == .listening }
        dual.publishSourceDelta("source")
        dual.emit(
            target: .english,
            event: .outputTranscriptDelta(delta: "translation", eventID: nil, elapsedMs: nil)
        )
        await waitForCondition {
            delegate.latestSnapshot?.current.sourceText == "source"
                && delegate.latestSnapshot?.current.translatedText == "translation"
        }

        let invalidationsBefore = delegate.snapshots.filter(\.isInvalidation).count
        dual.recordLoss(stage: .merge, capacity: 512)
        await waitForCondition {
            session.state == .listening && dual.startCallCount >= 2
        }

        let invalidations = delegate.snapshots.filter(\.isInvalidation)
        XCTAssertEqual(invalidations.count - invalidationsBefore, 1)
        XCTAssertEqual(invalidations.last?.current.sourceText, "")
        XCTAssertEqual(invalidations.last?.current.translatedText, "")
        XCTAssertFalse(delegate.finalizedSnapshots.contains { $0.sourceText == "source" })
        await session.stop()
    }

    // Given: 再接続後に新しい epoch で Listening 中の session
    // When: 前の epoch の delivery state に loss を記録する
    // Then: 無効化も再接続も発生しない
    func testPreviousEpochLossDoesNotAffectCurrentSession() async {
        let dual = FakeDualRealtimeTranslationClient()
        let session = InterpretationSession(
            apiKeyStore: InMemoryAPIKeyStore(initialKey: "sk-test"),
            audioCapture: FakeRealtimeAudioCaptureService(),
            dualClient: dual
        )

        await session.start()
        await waitForCondition { session.state == .listening }
        let oldFeed = await dual.feed
        dual.recordTermination(.transportFailure)
        await waitForCondition {
            session.state == .listening && dual.startCallCount >= 2
        }
        let startsAfterReconnect = dual.startCallCount
        oldFeed.deliveryState.recordLoss(stage: .merge, capacity: 512)
        await Task.yield()

        XCTAssertEqual(session.state, .listening)
        XCTAssertEqual(dual.startCallCount, startsAfterReconnect)
        await session.stop()
    }

    // Given: Listening 中の session
    // When: authentication termination と overflow を同じ epoch に記録する
    // Then: 認証エラーで終了し再接続しない
    func testAuthenticationTerminationTakesPrecedenceOverOverflow() async {
        let dual = FakeDualRealtimeTranslationClient()
        let session = InterpretationSession(
            apiKeyStore: InMemoryAPIKeyStore(initialKey: "sk-test"),
            audioCapture: FakeRealtimeAudioCaptureService(),
            dualClient: dual
        )

        await session.start()
        await waitForCondition { session.state == .listening }
        dual.recordTermination(.authenticationFailed)
        dual.recordLoss(stage: .merge, capacity: 512)
        await waitForCondition { session.state == .error }

        XCTAssertEqual(dual.startCallCount, 1)
        await session.stop()
    }

    // Given: Listening 中の session
    // When: fatal server termination と overflow を同じ epoch に記録する
    // Then: sanitized fatal message で終了し再接続しない
    func testFatalTerminationTakesPrecedenceOverOverflow() async {
        let dual = FakeDualRealtimeTranslationClient()
        let delegate = InterpretationSessionDelegateSpy()
        let session = InterpretationSession(
            apiKeyStore: InMemoryAPIKeyStore(initialKey: "sk-test"),
            audioCapture: FakeRealtimeAudioCaptureService(),
            dualClient: dual
        )
        session.delegate = delegate

        await session.start()
        await waitForCondition { session.state == .listening }
        dual.recordTermination(.fatalServerError(UiCopy.text("error.genericServer")))
        dual.recordLoss(stage: .merge, capacity: 512)
        await waitForCondition { session.state == .error }

        XCTAssertEqual(dual.startCallCount, 1)
        XCTAssertEqual(delegate.messages.last, UiCopy.text("error.genericServer"))
        await session.stop()
    }

    // Given: Stop 中に close drain を開始する session
    // When: drain 中に loss を記録してイベントを追加する
    // Then: Idle になり loss 後のイベントを確定しない
    func testLossDuringStopDrainReturnsIdleWithoutFinalizing() async {
        let dual = FakeDualRealtimeTranslationClient()
        let delegate = InterpretationSessionDelegateSpy()
        let session = InterpretationSession(
            apiKeyStore: InMemoryAPIKeyStore(initialKey: "sk-test"),
            audioCapture: FakeRealtimeAudioCaptureService(),
            dualClient: dual
        )
        session.delegate = delegate
        await session.start()
        await waitForCondition { session.state == .listening }
        let epoch = await dual.connectionEpoch
        dual.publishSourceDelta("source")
        dual.emit(
            target: .english,
            event: .outputTranscriptDelta(delta: "translation", eventID: nil, elapsedMs: nil)
        )
        await waitForCondition {
            delegate.latestSnapshot?.current.sourceText == "source"
                && delegate.latestSnapshot?.current.translatedText == "translation"
        }

        dual.onCloseGracefully = {
            dual.recordLoss(stage: .stopDrain, capacity: 1024)
        }
        dual.closeGracefullyEvents = [
            RealtimeTranslationStreamEvent(
                target: .english,
                event: .outputTranscriptDelta(
                    delta: "late",
                    eventID: nil,
                    elapsedMs: nil
                ),
                epoch: epoch
            )
        ]

        await session.stop()

        XCTAssertEqual(session.state, .idle)
        XCTAssertFalse(delegate.finalizedSnapshots.contains { $0.sourceText == "source" })
        XCTAssertFalse(delegate.finalizedSnapshots.contains { $0.translatedText == "late" })
    }

    private func waitForCondition(
        _ condition: @escaping () -> Bool
    ) async {
        for _ in 0..<10_000 {
            if condition() { return }
            await Task.yield()
        }
        XCTFail("Condition not met")
    }
}
