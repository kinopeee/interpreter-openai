import XCTest
@testable import RealtimeTranslator

@MainActor
final class InterpretationSessionAudioLossTests: XCTestCase {
    func testStalledTransportInvalidatesUnconfirmedSubtitleWithoutReconnect() async {
        // Given: 送信キューを満杯にし、最初の送信をゲートで停止したsession
        let queue = RealtimeAudioFrameQueue()
        let audio = FakeRealtimeAudioCaptureService(queue: queue)
        let dual = FakeDualRealtimeTranslationClient()
        let gate = CheckedContinuationBox()
        dual.appendAudioFrameGate = gate
        let delegate = InterpretationSessionDelegateSpy()
        let session = InterpretationSession(
            apiKeyStore: InMemoryAPIKeyStore(initialKey: "sk-test"),
            audioCapture: audio,
            dualClient: dual,
            activeTickerIntervalNanoseconds: 20_000_000
        )
        session.delegate = delegate

        // When: 未確定字幕を作り、停止中の送信を解放する
        await session.start()
        await waitUntil { session.state == .listening }
        XCTAssertTrue(
            queue.enqueue(
                pcm16: Data(count: PCM16FramePacketizer.bytesPerFrame),
                generation: 1,
                discardedMilliseconds: 0,
                capturedAt: .now
            )
        )
        await waitUntil { dual.appendAudioFrameCallCount == 1 }
        for _ in 0..<40 {
            XCTAssertTrue(
                queue.enqueue(
                    pcm16: Data(count: PCM16FramePacketizer.bytesPerFrame),
                    generation: 1,
                    discardedMilliseconds: 0,
                    capturedAt: .now
                )
            )
        }
        dual.publishSourceDelta("こんにちは")
        dual.emit(
            target: .english,
            event: .outputTranscriptDelta(delta: "Hello", eventID: "loss-test", elapsedMs: nil)
        )
        gate.resume()
        await waitUntil { session.audioLossMetrics.lostMilliseconds == 800 }

        // Then: 欠落は記録されるが再接続せず、未確定字幕は確定一覧に残らない
        XCTAssertEqual(dual.startCallCount, 1)
        await waitUntil { delegate.latestSnapshot?.current.isEmpty == true }
        XCTAssertTrue(delegate.latestSnapshot?.current.isEmpty == true)
        XCTAssertFalse(
            delegate.finalizedSnapshots.contains {
                $0.sourceText == "こんにちは" && $0.translatedText == "Hello"
            }
        )
        await session.stop()
    }

    func testAudioLossResetsDualRoutingWithoutReconnect() async {
        // Given: 再接続閾値未満の欠落を受け取るListening session
        let audio = FakeRealtimeAudioCaptureService()
        let dual = FakeDualRealtimeTranslationClient()
        let session = InterpretationSession(
            apiKeyStore: InMemoryAPIKeyStore(initialKey: "sk-test"),
            audioCapture: audio,
            dualClient: dual
        )
        await session.start()
        await waitUntil { session.state == .listening }
        let resetCountBeforeLoss = dual.resetAudioRoutingCallCount

        // When: 800ms相当のsequence欠落を通知する
        audio.emit(sequence: 0)
        audio.emit(sequence: 9)
        await waitUntil {
            session.audioLossMetrics.lostMilliseconds == 800
                && dual.resetAudioRoutingCallCount == resetCountBeforeLoss + 1
        }

        // Then: dual routingだけを一度リセットし、再接続しない
        XCTAssertEqual(dual.resetAudioRoutingCallCount, resetCountBeforeLoss + 1)
        XCTAssertEqual(dual.startCallCount, 1)
        await session.stop()
    }

    func testFinalizedSubtitleSurvivesAudioLoss() async {
        // Given: 先に確定した字幕を持つsession
        let audio = FakeRealtimeAudioCaptureService()
        let dual = FakeDualRealtimeTranslationClient()
        let delegate = InterpretationSessionDelegateSpy()
        let session = InterpretationSession(
            apiKeyStore: InMemoryAPIKeyStore(initialKey: "sk-test"),
            audioCapture: audio,
            dualClient: dual,
            activeTickerIntervalNanoseconds: 20_000_000
        )
        session.delegate = delegate
        await session.start()
        await waitUntil { session.state == .listening }
        dual.publishSourceDelta("確定前")
        dual.emit(
            target: .english,
            event: .outputTranscriptDelta(delta: "Before", eventID: "finalized", elapsedMs: nil)
        )
        await waitUntil {
            delegate.latestSnapshot?.current.translatedText.contains("Before") == true
                && delegate.latestSnapshot?.current.state == .live
        }
        dual.emit(
            target: .english,
            event: .error(message: "socket closed", code: "transport", errorType: nil)
        )
        await waitUntil(timeout: 3) {
            delegate.finalizedSnapshots.contains {
                $0.sourceText == "確定前" && $0.translatedText == "Before"
            }
        }

        // When: 後続frameで音声欠落を通知する
        audio.emit(sequence: 0)
        audio.emit(sequence: 1, discardedMs: 500)
        await waitUntil { session.audioLossMetrics.lostMilliseconds == 500 }

        // Then: 欠落後も既に確定した字幕を保持する
        XCTAssertTrue(
            delegate.finalizedSnapshots.contains {
                $0.sourceText == "確定前" && $0.translatedText == "Before"
            }
        )
        await session.stop()
    }

    func testSustainedAudioLossReconnects() async {
        // Given: 再接続閾値を越える二つの送信キュー欠落
        let audio = FakeRealtimeAudioCaptureService()
        let dual = FakeDualRealtimeTranslationClient()
        let session = InterpretationSession(
            apiKeyStore: InMemoryAPIKeyStore(initialKey: "sk-test"),
            audioCapture: audio,
            dualClient: dual,
            activeTickerIntervalNanoseconds: 20_000_000
        )
        await session.start()
        await waitUntil { session.state == .listening }

        // When: 30秒窓内に32frame gapを2回投入する
        audio.emit(sequence: 0)
        audio.emit(sequence: 33)
        audio.emit(sequence: 34)
        audio.emit(sequence: 67)
        await waitUntil(timeout: 3) {
            dual.startCallCount >= 2 && session.state == .listening
        }

        // Then: pipeline overload経路で再接続しlisteningへ戻る
        XCTAssertGreaterThanOrEqual(dual.startCallCount, 2)
        XCTAssertEqual(session.state, .listening)
        await session.stop()
    }

    func testQueueGapAndPreConversionDiscardAreAddedOnce() async {
        // Given: 同一frameに送信キュー欠落と変換前破棄を持つsession
        let audio = FakeRealtimeAudioCaptureService()
        let dual = FakeDualRealtimeTranslationClient()
        let session = InterpretationSession(
            apiKeyStore: InMemoryAPIKeyStore(initialKey: "sk-test"),
            audioCapture: audio,
            dualClient: dual
        )
        await session.start()
        await waitUntil { session.state == .listening }

        // When: 32frame gapと700msの累積破棄を同時に投入する
        audio.emit(sequence: 0)
        audio.emit(sequence: 1)
        audio.emit(sequence: 2)
        audio.emit(sequence: 35, discardedMs: 700)
        await waitUntil { session.audioLossMetrics.lostMilliseconds == 3_900 }

        // Then: 二つの欠落区間を二重計上せず合算する
        XCTAssertEqual(session.audioLossMetrics.droppedFrames, 32)
        XCTAssertEqual(session.audioLossMetrics.lossEvents, 1)
        await session.stop()
    }

    func testReconnectFlushDoesNotFinalizeTaintedPair() async {
        // Given: 音声欠落のあと汚染窓内に完全ペアがある session
        let audio = FakeRealtimeAudioCaptureService()
        let dual = FakeDualRealtimeTranslationClient()
        let delegate = InterpretationSessionDelegateSpy()
        let session = InterpretationSession(
            apiKeyStore: InMemoryAPIKeyStore(initialKey: "sk-test"),
            audioCapture: audio,
            dualClient: dual,
            activeTickerIntervalNanoseconds: 20_000_000
        )
        session.delegate = delegate
        await session.start()
        await waitUntil { session.state == .listening }
        audio.emit(sequence: 0)
        audio.emit(sequence: 33)
        await waitUntil { session.audioLossMetrics.lostMilliseconds == 3_200 }

        dual.publishSourceDelta("こんにちは")
        dual.emit(
            target: .english,
            event: .outputTranscriptDelta(delta: "Hello", eventID: "tainted-flush", elapsedMs: nil)
        )
        await waitUntil {
            delegate.latestSnapshot?.current.translatedText.contains("Hello") == true
        }

        // When: transport error で再接続 flush する
        dual.emit(
            target: .english,
            event: .error(message: "socket closed", code: "transport", errorType: nil)
        )
        await waitUntil(timeout: 3) { session.state == .listening && dual.startCallCount >= 2 }

        // Then: 汚染ペアは確定一覧に残らない
        XCTAssertFalse(
            delegate.finalizedSnapshots.contains {
                $0.sourceText == "こんにちは" && $0.translatedText == "Hello"
            }
        )
        await session.stop()
    }
}
