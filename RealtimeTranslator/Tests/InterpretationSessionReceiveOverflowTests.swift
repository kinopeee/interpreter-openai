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
            dualClient: dual,
            activeTickerIntervalNanoseconds: 50_000_000
        )
        session.delegate = delegate

        await session.start()
        await waitForCondition { session.state == .listening }
        dual.emit(
            target: .english,
            event: .inputTranscriptDelta(delta: "こんにちは", eventID: nil, elapsedMs: 10)
        )
        dual.emit(
            target: .english,
            event: .outputTranscriptDelta(delta: "Hello", eventID: nil, elapsedMs: 20)
        )
        await waitForCondition {
            delegate.latestSnapshot?.current.sourceText == "こんにちは"
                && delegate.latestSnapshot?.current.translatedText == "Hello"
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
        XCTAssertFalse(delegate.finalizedSnapshots.contains { $0.sourceText == "こんにちは" })
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
        dual.emit(
            target: .english,
            event: .error(message: "socket closed", code: "transport", errorType: nil)
        )
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

    // Given: Listening 中に termination だけ先に記録された session
    // When: 完了待ちが起きたあと、error イベントなしで overflow を記録する
    // Then: park 中でも欠落を見て再接続する
    func testLossAfterTerminationWakeStillReconnects() async {
        let dual = FakeDualRealtimeTranslationClient()
        let session = InterpretationSession(
            apiKeyStore: InMemoryAPIKeyStore(initialKey: "sk-test"),
            audioCapture: FakeRealtimeAudioCaptureService(),
            dualClient: dual,
            activeTickerIntervalNanoseconds: 50_000_000
        )

        await session.start()
        await waitForCondition { session.state == .listening }
        let feed = await dual.feed
        feed.deliveryState.tryRecordTermination(.transportFailure)
        try? await Task.sleep(nanoseconds: 50_000_000)
        feed.deliveryState.recordLoss(stage: .merge, capacity: 512)
        await waitForCondition {
            session.state == .listening && dual.startCallCount >= 2
        }
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
            dualClient: dual,
            activeTickerIntervalNanoseconds: 50_000_000
        )
        session.delegate = delegate
        await session.start()
        await waitForCondition { session.state == .listening }
        let epoch = await dual.connectionEpoch
        dual.emit(
            target: .english,
            event: .inputTranscriptDelta(delta: "こんにちは", eventID: nil, elapsedMs: 10)
        )
        dual.emit(
            target: .english,
            event: .outputTranscriptDelta(delta: "Hello", eventID: nil, elapsedMs: 20)
        )
        await waitForCondition {
            delegate.latestSnapshot?.current.sourceText == "こんにちは"
                && delegate.latestSnapshot?.current.translatedText == "Hello"
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
        XCTAssertFalse(delegate.finalizedSnapshots.contains { $0.sourceText == "こんにちは" })
        XCTAssertFalse(delegate.finalizedSnapshots.contains { $0.translatedText == "late" })
    }

    // Given: Listening 中に未確定の字幕ペアを受信した session
    // When: unknown code の transcription failed を受信する
    // Then: 未確定字幕だけを無効化し、接続を維持する
    func testUnknownTranscriptionFailureInvalidatesPendingPair() async {
        let dual = FakeDualRealtimeTranslationClient()
        let delegate = InterpretationSessionDelegateSpy()
        let session = InterpretationSession(
            apiKeyStore: InMemoryAPIKeyStore(initialKey: "sk-test"),
            audioCapture: FakeRealtimeAudioCaptureService(),
            dualClient: dual,
            activeTickerIntervalNanoseconds: 50_000_000
        )
        session.delegate = delegate

        await session.start()
        await waitForCondition { session.state == .listening }
        dual.emit(target: .english, event: .inputTranscriptDelta(delta: "こんにちは", eventID: nil, elapsedMs: 10))
        dual.emit(target: .english, event: .outputTranscriptDelta(delta: "Hello", eventID: nil, elapsedMs: 20))
        await waitForCondition { delegate.latestSnapshot?.current.sourceText == "こんにちは" }
        dual.publishSourceFailure(itemID: "item-1", eventID: nil, code: "unknown", errorType: nil)
        await waitForCondition { delegate.snapshots.last?.isInvalidation == true }

        XCTAssertEqual(session.state, .listening)
        XCTAssertEqual(dual.startCallCount, 1)
        XCTAssertFalse(delegate.finalizedSnapshots.contains { $0.sourceText == "こんにちは" })
        await session.stop()
    }

    // Given: 同じ item の transcription failed を二度受信した session
    // When: 両方の通知を処理する
    // Then: 無効化通知は一度だけ発生する
    func testDuplicateTranscriptionFailureInvalidatesOnce() async {
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
        dual.emit(target: .english, event: .inputTranscriptDelta(delta: "こんにちは", eventID: nil, elapsedMs: 10))
        dual.emit(target: .english, event: .outputTranscriptDelta(delta: "Hello", eventID: nil, elapsedMs: 20))
        await waitForCondition { delegate.latestSnapshot?.current.sourceText == "こんにちは" }
        dual.publishSourceFailure(itemID: "item-1", eventID: nil, code: "unknown", errorType: nil)
        await waitForCondition { delegate.snapshots.contains(where: \.isInvalidation) }
        dual.publishSourceFailure(itemID: "item-1", eventID: "evt-2", code: "unknown", errorType: nil)
        try? await Task.sleep(nanoseconds: 100_000_000)

        XCTAssertEqual(delegate.snapshots.filter(\.isInvalidation).count, 1)
        await session.stop()
    }

    // Given: failed を受信した時点では字幕内容がない session
    // When: 同じ item の failed 後に字幕ペアを受信し、もう一度 failed を受信する
    // Then: 後続の failed で未確定字幕を無効化する
    func testTranscriptionFailureBeforeContentCanInvalidateLaterContent() async {
        let dual = FakeDualRealtimeTranslationClient()
        let delegate = InterpretationSessionDelegateSpy()
        let session = InterpretationSession(
            apiKeyStore: InMemoryAPIKeyStore(initialKey: "sk-test"),
            audioCapture: FakeRealtimeAudioCaptureService(),
            dualClient: dual,
            activeTickerIntervalNanoseconds: 50_000_000
        )
        session.delegate = delegate

        await session.start()
        await waitForCondition { session.state == .listening }
        let invalidationsBeforeEmptyFailure = delegate.snapshots.filter(\.isInvalidation).count

        // When: 内容がない状態で failed を受信する
        dual.publishSourceFailure(itemID: "late-item", eventID: nil, code: "unknown", errorType: nil)
        try? await Task.sleep(nanoseconds: 100_000_000)
        XCTAssertEqual(
            delegate.snapshots.filter(\.isInvalidation).count,
            invalidationsBeforeEmptyFailure
        )

        dual.emit(target: .english, event: .inputTranscriptDelta(delta: "後続字幕", eventID: nil, elapsedMs: 10))
        dual.emit(target: .english, event: .outputTranscriptDelta(delta: "Later subtitle", eventID: nil, elapsedMs: 20))
        await waitForCondition {
            delegate.latestSnapshot?.current.sourceText == "後続字幕"
                && delegate.latestSnapshot?.current.translatedText == "Later subtitle"
        }

        // Then: 同じ item の failed を再受信すると無効化する
        dual.publishSourceFailure(itemID: "late-item", eventID: "second-event", code: "unknown", errorType: nil)
        await waitForCondition { delegate.snapshots.last?.isInvalidation == true }

        XCTAssertEqual(delegate.snapshots.filter(\.isInvalidation).count, 1)
        await session.stop()
    }

    // Given: 再接続後に新しい epoch で未確定字幕を表示している session
    // When: 古い epoch の transcription failed を受信する
    // Then: 無効化せず Listening のまま現在の字幕を保持する
    func testStaleEpochTranscriptionFailureDoesNotInvalidateCurrentSubtitle() async {
        let dual = FakeDualRealtimeTranslationClient()
        let delegate = InterpretationSessionDelegateSpy()
        let session = InterpretationSession(
            apiKeyStore: InMemoryAPIKeyStore(initialKey: "sk-test"),
            audioCapture: FakeRealtimeAudioCaptureService(),
            dualClient: dual,
            activeTickerIntervalNanoseconds: 50_000_000
        )
        session.delegate = delegate

        await session.start()
        await waitForCondition { session.state == .listening }
        let oldEpoch = await dual.connectionEpoch
        dual.emit(
            target: .english,
            event: .error(message: "socket closed", code: "transport", errorType: nil)
        )
        await waitForCondition {
            session.state == .listening && dual.startCallCount >= 2
        }
        dual.emit(
            target: .english,
            event: .inputTranscriptDelta(delta: "現在の字幕", eventID: "source-current", elapsedMs: 10)
        )
        dual.emit(
            target: .english,
            event: .outputTranscriptDelta(delta: "Current subtitle", eventID: "target-current", elapsedMs: 20)
        )
        await waitForCondition {
            delegate.latestSnapshot?.current.sourceText == "現在の字幕"
                && delegate.latestSnapshot?.current.translatedText == "Current subtitle"
        }
        let invalidationsBefore = delegate.snapshots.filter(\.isInvalidation).count

        dual.publishSourceFailure(
            itemID: "stale-item",
            eventID: "stale-event",
            code: "audio_unintelligible",
            errorType: nil,
            epoch: oldEpoch
        )
        try? await Task.sleep(nanoseconds: 100_000_000)

        XCTAssertEqual(session.state, .listening)
        XCTAssertEqual(dual.startCallCount, 2)
        XCTAssertEqual(delegate.snapshots.filter(\.isInvalidation).count, invalidationsBefore)
        XCTAssertEqual(delegate.latestSnapshot?.current.sourceText, "現在の字幕")
        XCTAssertEqual(delegate.latestSnapshot?.current.translatedText, "Current subtitle")
        await session.stop()
    }

    // Given: Listening 中に未確定の字幕ペアを表示している session
    // When: recover 分類の transcription failed を受信する
    // Then: 無効化して再接続し、未確定ペアを確定しない
    func testRecoverTranscriptionFailureReconnectsWithoutFinalizingPendingPair() async {
        let dual = FakeDualRealtimeTranslationClient()
        let delegate = InterpretationSessionDelegateSpy()
        let session = InterpretationSession(
            apiKeyStore: InMemoryAPIKeyStore(initialKey: "sk-test"),
            audioCapture: FakeRealtimeAudioCaptureService(),
            dualClient: dual,
            activeTickerIntervalNanoseconds: 50_000_000
        )
        session.delegate = delegate

        await session.start()
        await waitForCondition { session.state == .listening }
        dual.emit(
            target: .english,
            event: .inputTranscriptDelta(delta: "失敗する字幕", eventID: "source-recover", elapsedMs: 10)
        )
        dual.emit(
            target: .english,
            event: .outputTranscriptDelta(delta: "Recover subtitle", eventID: "target-recover", elapsedMs: 20)
        )
        await waitForCondition {
            delegate.latestSnapshot?.current.sourceText == "失敗する字幕"
                && delegate.latestSnapshot?.current.translatedText == "Recover subtitle"
        }

        dual.publishSourceFailure(
            itemID: "recover-item",
            eventID: nil,
            code: nil,
            errorType: "server_error"
        )
        await waitForCondition {
            session.state == .listening
                && dual.startCallCount >= 2
                && delegate.snapshots.contains(where: \.isInvalidation)
        }

        XCTAssertFalse(delegate.finalizedSnapshots.contains {
            $0.sourceText == "失敗する字幕" || $0.translatedText == "Recover subtitle"
        })
        await session.stop()
    }

    // Given: 未確定の字幕ペアを表示中で failed の termination が先に完了する session
    // When: recover 分類の failed イベントを後から受信する
    // Then: 未確定ペアを確定せず無効化して再接続する
    func testRecoverTranscriptionFailureCompletionBeforeEventDoesNotFinalizePendingPair() async {
        let dual = FakeDualRealtimeTranslationClient()
        let delegate = InterpretationSessionDelegateSpy()
        let session = InterpretationSession(
            apiKeyStore: InMemoryAPIKeyStore(initialKey: "sk-test"),
            audioCapture: FakeRealtimeAudioCaptureService(),
            dualClient: dual,
            activeTickerIntervalNanoseconds: 50_000_000
        )
        session.delegate = delegate

        await session.start()
        await waitForCondition { session.state == .listening }
        dual.emit(
            target: .english,
            event: .inputTranscriptDelta(delta: "順序競合字幕", eventID: nil, elapsedMs: 10)
        )
        dual.emit(
            target: .english,
            event: .outputTranscriptDelta(delta: "Ordering race", eventID: nil, elapsedMs: 20)
        )
        await waitForCondition {
            delegate.latestSnapshot?.current.sourceText == "順序競合字幕"
                && delegate.latestSnapshot?.current.translatedText == "Ordering race"
        }

        // When: termination が完了してから failed イベントを配送する
        let fallbackGate = CheckedContinuationBox()
        session.afterFailedSourceTerminationForTests = {
            fallbackGate.resume()
        }
        let failureTask = Task {
            await dual.publishSourceFailureAfterTermination(
                itemID: "ordering-item",
                eventID: nil,
                code: nil,
                errorType: "server_error",
                waitUntilFallback: fallbackGate
            )
        }
        await failureTask.value
        await waitForCondition {
            session.state == .listening
                && dual.startCallCount >= 2
                && delegate.snapshots.contains(where: \.isInvalidation)
        }

        // Then: 失敗したペアは確定されない
        XCTAssertFalse(delegate.finalizedSnapshots.contains {
            $0.sourceText == "順序競合字幕" || $0.translatedText == "Ordering race"
        })
        await session.stop()
    }

    // Given: Listening 中に未確定の字幕ペアを表示している session
    // When: halt 分類の transcription failed を受信する
    // Then: 無効化して Error になり、flush で未確定ペアを確定しない
    func testHaltTranscriptionFailureDoesNotFinalizePendingPair() async {
        let dual = FakeDualRealtimeTranslationClient()
        let delegate = InterpretationSessionDelegateSpy()
        let session = InterpretationSession(
            apiKeyStore: InMemoryAPIKeyStore(initialKey: "sk-test"),
            audioCapture: FakeRealtimeAudioCaptureService(),
            dualClient: dual,
            activeTickerIntervalNanoseconds: 50_000_000
        )
        session.delegate = delegate

        await session.start()
        await waitForCondition { session.state == .listening }
        dual.emit(
            target: .english,
            event: .inputTranscriptDelta(delta: "失敗する字幕", eventID: "source-halt", elapsedMs: 10)
        )
        dual.emit(
            target: .english,
            event: .outputTranscriptDelta(delta: "Halt subtitle", eventID: "target-halt", elapsedMs: 20)
        )
        await waitForCondition {
            delegate.latestSnapshot?.current.sourceText == "失敗する字幕"
                && delegate.latestSnapshot?.current.translatedText == "Halt subtitle"
        }

        dual.publishSourceFailure(
            itemID: "halt-item",
            eventID: nil,
            code: "insufficient_quota",
            errorType: "server_error"
        )
        await waitForCondition { session.state == .error }

        XCTAssertEqual(dual.startCallCount, 1)
        XCTAssertTrue(delegate.snapshots.contains(where: \.isInvalidation))
        XCTAssertFalse(delegate.finalizedSnapshots.contains {
            $0.sourceText == "失敗する字幕" || $0.translatedText == "Halt subtitle"
        })
        await session.stop()
    }

    // Given: reconnect 前の flush で確定済み字幕ペアを保持している session
    // When: transcription failed を受信する
    // Then: 確定済み字幕と delegate 通知数を保持する
    func testTranscriptionFailurePreservesFinalizedSubtitle() async throws {
        let dual = FakeDualRealtimeTranslationClient()
        let delegate = InterpretationSessionDelegateSpy()
        let session = InterpretationSession(
            apiKeyStore: InMemoryAPIKeyStore(initialKey: "sk-test"),
            audioCapture: FakeRealtimeAudioCaptureService(),
            dualClient: dual,
            activeTickerIntervalNanoseconds: 8_100_000_000
        )
        session.delegate = delegate

        await session.start()
        await waitForCondition { session.state == .listening }
        dual.emit(
            target: .english,
            event: .inputTranscriptDelta(delta: "確定済み字幕", eventID: "source-final", elapsedMs: 10)
        )
        await waitForCondition { dual.spokenLanguages.contains(.japanese) }
        dual.emit(
            target: .english,
            event: .outputTranscriptDelta(delta: "Finalized subtitle", eventID: "target-final", elapsedMs: 20)
        )
        await waitForCondition {
            delegate.latestSnapshot?.current.sourceText == "確定済み字幕"
                && delegate.latestSnapshot?.current.translatedText == "Finalized subtitle"
        }
        await waitForCondition(timeout: 10) {
            delegate.finalizedSnapshots.contains {
                $0.sourceText == "確定済み字幕" && $0.translatedText == "Finalized subtitle"
            }
        }
        // 同一 tick の aggregator.tick 再通知が終わるまで待ってから件数を取る。
        try? await Task.sleep(nanoseconds: 50_000_000)
        let finalizedCount = delegate.snapshots.filter {
            $0.current.state == .finalized && !$0.isInvalidation
        }.count

        dual.publishSourceFailure(
            itemID: "finalized-item",
            eventID: nil,
            code: "audio_unintelligible",
            errorType: nil
        )
        try? await Task.sleep(nanoseconds: 100_000_000)

        XCTAssertEqual(
            delegate.snapshots.filter {
                $0.current.state == .finalized && !$0.isInvalidation
            }.count,
            finalizedCount
        )
        XCTAssertTrue(delegate.finalizedSnapshots.contains {
            $0.sourceText == "確定済み字幕" && $0.translatedText == "Finalized subtitle"
        })
        await session.stop()
    }

    // Given: 停止前に未確定の字幕ペアを表示している session
    // When: stop drain 中に transcription failed を受信する
    // Then: 停止は完了し、未確定ペアを確定しない
    func testStopDrainTranscriptionFailureDoesNotFinalizePendingPair() async {
        let dual = FakeDualRealtimeTranslationClient()
        let delegate = InterpretationSessionDelegateSpy()
        let session = InterpretationSession(
            apiKeyStore: InMemoryAPIKeyStore(initialKey: "sk-test"),
            audioCapture: FakeRealtimeAudioCaptureService(),
            dualClient: dual,
            activeTickerIntervalNanoseconds: 50_000_000
        )
        session.delegate = delegate

        await session.start()
        await waitForCondition { session.state == .listening }
        let epoch = await dual.connectionEpoch
        dual.emit(
            target: .english,
            event: .inputTranscriptDelta(delta: "停止中の字幕", eventID: "source-stop", elapsedMs: 10)
        )
        dual.emit(
            target: .english,
            event: .outputTranscriptDelta(delta: "Stopping subtitle", eventID: "target-stop", elapsedMs: 20)
        )
        await waitForCondition {
            delegate.latestSnapshot?.current.sourceText == "停止中の字幕"
                && delegate.latestSnapshot?.current.translatedText == "Stopping subtitle"
        }
        dual.closeGracefullyEvents = [
            RealtimeTranslationStreamEvent(
                lane: .source,
                event: .inputTranscriptFailed(
                    itemID: "stop-item",
                    eventID: "stop-event",
                    code: "audio_unintelligible",
                    errorType: nil
                ),
                epoch: epoch
            )
        ]

        await session.stop()

        XCTAssertEqual(session.state, .idle)
        XCTAssertTrue(delegate.snapshots.contains(where: \.isInvalidation))
        XCTAssertFalse(delegate.finalizedSnapshots.contains {
            $0.sourceText == "停止中の字幕" || $0.translatedText == "Stopping subtitle"
        })
    }

    // Given: 停止前に未確定の字幕ペアを表示し、keepAlive failed が配送前にキューされた session
    // When: merge 停止で failed イベントが drain へ入る前に失われる
    // Then: 停止は完了し、未確定ペアを無効化して確定しない
    func testStopDrainDroppedKeepAliveFailureInvalidatesPendingPair() async {
        let dual = FakeDualRealtimeTranslationClient()
        let delegate = InterpretationSessionDelegateSpy()
        let session = InterpretationSession(
            apiKeyStore: InMemoryAPIKeyStore(initialKey: "sk-test"),
            audioCapture: FakeRealtimeAudioCaptureService(),
            dualClient: dual,
            activeTickerIntervalNanoseconds: 50_000_000
        )
        session.delegate = delegate

        await session.start()
        await waitForCondition { session.state == .listening }
        dual.emit(
            target: .english,
            event: .inputTranscriptDelta(delta: "停止drop字幕", eventID: nil, elapsedMs: 10)
        )
        dual.emit(
            target: .english,
            event: .outputTranscriptDelta(delta: "Dropped failure", eventID: nil, elapsedMs: 20)
        )
        await waitForCondition {
            delegate.latestSnapshot?.current.sourceText == "停止drop字幕"
                && delegate.latestSnapshot?.current.translatedText == "Dropped failure"
        }

        dual.queueSourceFailureWithoutDrain()
        await session.stop()

        XCTAssertEqual(session.state, .idle)
        XCTAssertTrue(delegate.snapshots.contains(where: \.isInvalidation))
        XCTAssertFalse(delegate.finalizedSnapshots.contains {
            $0.sourceText == "停止drop字幕" || $0.translatedText == "Dropped failure"
        })
    }

    // Given: keepAlive failed を正常に消費して pending counter が空になった session
    // When: その後に新しい字幕ペアを表示して停止する
    // Then: 新しい字幕ペアは停止時に確定される
    func testConsumedKeepAliveFailureDoesNotDiscardLaterPair() async {
        let dual = FakeDualRealtimeTranslationClient()
        let delegate = InterpretationSessionDelegateSpy()
        let session = InterpretationSession(
            apiKeyStore: InMemoryAPIKeyStore(initialKey: "sk-test"),
            audioCapture: FakeRealtimeAudioCaptureService(),
            dualClient: dual,
            activeTickerIntervalNanoseconds: 50_000_000
        )
        session.delegate = delegate

        await session.start()
        await waitForCondition { session.state == .listening }
        dual.queueAndPublishSourceFailure(
            itemID: "consumed-item",
            eventID: "consumed-event",
            code: "audio_unintelligible",
            errorType: nil
        )
        await waitForCondition { dual.pendingSourceFailureCount == 0 }

        dual.emit(
            target: .english,
            event: .inputTranscriptDelta(delta: "有効な後続字幕", eventID: nil, elapsedMs: 10)
        )
        dual.emit(
            target: .english,
            event: .outputTranscriptDelta(delta: "Valid follow-up", eventID: nil, elapsedMs: 20)
        )
        await waitForCondition {
            delegate.latestSnapshot?.current.sourceText == "有効な後続字幕"
                && delegate.latestSnapshot?.current.translatedText == "Valid follow-up"
        }

        await session.stop()

        XCTAssertTrue(delegate.finalizedSnapshots.contains {
            $0.sourceText == "有効な後続字幕" && $0.translatedText == "Valid follow-up"
        })
    }

    // Given: 停止前に未確定の字幕ペアを表示している session
    // When: stop drain 中に古い epoch の transcription failed を受信する
    // Then: 現在の未確定ペアを無効化せず、停止時に確定できる
    func testStopDrainStaleEpochTranscriptionFailureDoesNotInvalidateCurrentPair() async {
        let dual = FakeDualRealtimeTranslationClient()
        let delegate = InterpretationSessionDelegateSpy()
        let session = InterpretationSession(
            apiKeyStore: InMemoryAPIKeyStore(initialKey: "sk-test"),
            audioCapture: FakeRealtimeAudioCaptureService(),
            dualClient: dual,
            activeTickerIntervalNanoseconds: 50_000_000
        )
        session.delegate = delegate

        await session.start()
        await waitForCondition { session.state == .listening }
        let epoch = await dual.connectionEpoch
        dual.emit(
            target: .english,
            event: .inputTranscriptDelta(delta: "停止中の字幕", eventID: "source-stale-stop", elapsedMs: 10)
        )
        dual.emit(
            target: .english,
            event: .outputTranscriptDelta(delta: "Stopping subtitle", eventID: "target-stale-stop", elapsedMs: 20)
        )
        await waitForCondition {
            delegate.latestSnapshot?.current.sourceText == "停止中の字幕"
                && delegate.latestSnapshot?.current.translatedText == "Stopping subtitle"
        }
        dual.closeGracefullyEvents = [
            RealtimeTranslationStreamEvent(
                lane: .source,
                event: .inputTranscriptFailed(
                    itemID: "stale-stop-item",
                    eventID: "stale-stop-event",
                    code: "audio_unintelligible",
                    errorType: nil
                ),
                epoch: epoch - 1
            )
        ]

        await session.stop()

        XCTAssertEqual(session.state, .idle)
        XCTAssertFalse(delegate.snapshots.contains(where: \.isInvalidation))
        XCTAssertTrue(delegate.finalizedSnapshots.contains {
            $0.sourceText == "停止中の字幕" && $0.translatedText == "Stopping subtitle"
        })
    }

    // Given: session が keepAlive の failed を受信する
    // When: 秘密情報を含む未確定字幕を無効化する
    // Then: message delegate と字幕 snapshot に秘密情報を出さない
    func testTranscriptionFailureSessionDoesNotExposeMessage() async {
        let dual = FakeDualRealtimeTranslationClient()
        let delegate = InterpretationSessionDelegateSpy()
        let session = InterpretationSession(
            apiKeyStore: InMemoryAPIKeyStore(initialKey: "sk-test"),
            audioCapture: FakeRealtimeAudioCaptureService(),
            dualClient: dual,
            activeTickerIntervalNanoseconds: 50_000_000
        )
        session.delegate = delegate

        await session.start()
        await waitForCondition { session.state == .listening }
        dual.emit(
            target: .english,
            event: .inputTranscriptDelta(delta: "こんにちは sk-leak-1234", eventID: nil, elapsedMs: 10)
        )
        dual.emit(
            target: .english,
            event: .outputTranscriptDelta(delta: "Secret subtitle", eventID: nil, elapsedMs: 20)
        )
        await waitForCondition {
            delegate.latestSnapshot?.current.sourceText.contains("sk-leak-1234") == true
        }
        dual.publishSourceFailure(
            itemID: "privacy-item",
            eventID: "privacy-event",
            code: "audio_unintelligible",
            errorType: nil
        )
        await waitForCondition { delegate.snapshots.contains(where: \.isInvalidation) }

        XCTAssertTrue(delegate.messages.isEmpty)
        let invalidationIndex = delegate.snapshots.firstIndex(where: \.isInvalidation) ?? delegate.snapshots.endIndex
        XCTAssertFalse(delegate.snapshots[invalidationIndex...].contains { snapshot in
            snapshot.current.sourceText.contains("sk-")
                || snapshot.current.translatedText.contains("sk-")
        })
        await session.stop()
    }

    private func waitForCondition(
        timeout: TimeInterval = 5,
        file: StaticString = #filePath,
        line: UInt = #line,
        _ condition: @escaping () -> Bool
    ) async {
        let deadline = Date().addingTimeInterval(timeout)
        while Date() < deadline {
            if condition() { return }
            try? await Task.sleep(nanoseconds: 10_000_000)
        }
        XCTFail("Condition not met", file: file, line: line)
    }
}
