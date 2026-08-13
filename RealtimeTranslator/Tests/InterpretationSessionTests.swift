import os
import XCTest
@testable import RealtimeTranslator

@MainActor
final class InterpretationSessionTests: XCTestCase {
    func testStartDoesNotCaptureBeforeBothSessionsReady() async {
        // Given: Dual clientのstartが完了するまで待機できるfake
        let apiKeyStore = InMemoryAPIKeyStore(initialKey: "sk-test")
        let audio = FakeRealtimeAudioCaptureService()
        let dual = FakeDualRealtimeTranslationClient()
        dual.startGate = CheckedContinuationBox()
        let session = InterpretationSession(
            apiKeyStore: apiKeyStore,
            audioCapture: audio,
            dualClient: dual,
            activeTickerIntervalNanoseconds: 50_000_000
        )

        // When: startを呼び、Dual start完了前を観測する
        let startTask = Task { await session.start() }
        await waitUntil { dual.startCallCount == 1 }
        XCTAssertEqual(audio.startCallCount, 0)

        // Then: Dual start解放後にcaptureが始まる
        dual.startGate?.resume()
        dual.startGate = nil
        await waitUntil { audio.startCallCount == 1 }
        XCTAssertEqual(session.state, .listening)
        await session.stop()
        startTask.cancel()
    }

    func testPairIsCachedForTheActiveConnection() async {
        // Given: 接続後に変更された言語ペア provider
        var pair = LanguagePair.jaEn
        let audio = FakeRealtimeAudioCaptureService()
        let dual = FakeDualRealtimeTranslationClient()
        let session = InterpretationSession(
            apiKeyStore: InMemoryAPIKeyStore(initialKey: "sk-test"),
            audioCapture: audio,
            dualClient: dual,
            languagePairProvider: { pair }
        )

        // When: 録音中に provider を変更して原文の言語反転を検出する
        await session.start()
        await waitUntil { session.state == .listening }
        await dual.publishSourceDelta("これは接続時ペアです")
        await waitUntil { dual.spokenLanguages.count == 1 }
        pair = .jaEs
        await dual.publishSourceDelta(" this remains the same pair")
        await waitUntil { dual.spokenLanguages.count == 2 }

        // Then: 接続開始時のペアで routing し、provider の変更を反映しない
        XCTAssertEqual(dual.spokenLanguages, [.japanese, .english])
        await session.stop()
    }

    func testReconnectKeepsLanguagePairFrozenFromStart() async {
        // Given: ja-en で開始したあと、録音中に provider を en-es へ変更する
        var pair = LanguagePair.jaEn
        let audio = FakeRealtimeAudioCaptureService()
        let dual = FakeDualRealtimeTranslationClient()
        let session = InterpretationSession(
            apiKeyStore: InMemoryAPIKeyStore(initialKey: "sk-test"),
            audioCapture: audio,
            dualClient: dual,
            languagePairProvider: { pair }
        )
        await session.start()
        await waitUntil { session.state == .listening }
        XCTAssertEqual(dual.lastLanguagePair, .jaEn)
        let startCountAtListening = dual.startCallCount
        pair = .enEs

        // When: transport error で再接続する
        dual.emit(
            target: .english,
            event: .error(message: "socket closed", code: "transport")
        )
        await waitUntil(timeout: 3) {
            session.state == .listening && dual.startCallCount > startCountAtListening
        }

        // Then: 再接続後も Start 時点の ja-en を使い、日本語は英語 target へ載る
        XCTAssertEqual(dual.lastLanguagePair, .jaEn)
        await dual.publishSourceDelta("これは再接続後も同じペアです")
        await waitUntil { dual.spokenLanguages == [.japanese] }
        XCTAssertEqual(dual.selectedTargets, [.english])

        // And: 停止して次の録音を開始したときだけ新しいペアが反映される
        await session.stop()
        await waitUntil { session.state == .idle }
        await session.start()
        await waitUntil { session.state == .listening }
        XCTAssertEqual(dual.lastLanguagePair, .enEs)
        await session.stop()
    }

    func testStopDuringStartDoesNotLeaveListening() async {
        // Given: 接続中に止められるセッション
        let apiKeyStore = InMemoryAPIKeyStore(initialKey: "sk-test")
        let audio = FakeRealtimeAudioCaptureService()
        let dual = FakeDualRealtimeTranslationClient()
        dual.startGate = CheckedContinuationBox()
        let session = InterpretationSession(
            apiKeyStore: apiKeyStore,
            audioCapture: audio,
            dualClient: dual
        )

        // When: start直後にstopする（stopはsessionTask排水を待つのでgateを先に解放）
        let startTask = Task { await session.start() }
        await waitUntil { dual.startCallCount == 1 }
        let stopTask = Task { await session.stop() }
        await waitUntil { session.state == .closing || session.state == .idle }
        dual.startGate?.resume()
        dual.startGate = nil
        await stopTask.value
        await startTask.value

        // Then: idleに戻りcaptureは開始されないか、開始後でも停止済み
        XCTAssertEqual(session.state, .idle)
        XCTAssertFalse(audio.isRunning)
    }

    func testStopDrainsSessionTaskBeforeReturningSoRestartIsStable() async {
        // Given: dual.start待ちで止まっているセッション
        let apiKeyStore = InMemoryAPIKeyStore(initialKey: "sk-test")
        let audio = FakeRealtimeAudioCaptureService()
        let dual = FakeDualRealtimeTranslationClient()
        let gate = CheckedContinuationBox()
        dual.startGate = gate
        let session = InterpretationSession(
            apiKeyStore: apiKeyStore,
            audioCapture: audio,
            dualClient: dual,
            activeTickerIntervalNanoseconds: 20_000_000
        )

        let firstStart = Task { await session.start() }
        await waitUntil { dual.startCallCount == 1 }

        // When: stop中に旧startを進め、排水後に再startする
        let stopTask = Task { await session.stop() }
        await waitUntil { session.state == .closing || session.state == .idle }
        dual.startGate = nil
        gate.resume()
        await stopTask.value
        await firstStart.value
        XCTAssertEqual(session.state, .idle)

        let forceCloseAfterStop = dual.forceCloseCallCount
        await session.start()
        await waitUntil { session.state == .listening }

        // Then: 旧sessionTaskの世代不一致forceCloseが新セッションへ飛ばない
        try? await Task.sleep(nanoseconds: 100_000_000)
        XCTAssertEqual(session.state, .listening)
        XCTAssertEqual(dual.forceCloseCallCount, forceCloseAfterStop)
    }

    func testDoubleStopIsIdempotent() async {
        // Given: 録音中のセッション
        let apiKeyStore = InMemoryAPIKeyStore(initialKey: "sk-test")
        let audio = FakeRealtimeAudioCaptureService()
        let dual = FakeDualRealtimeTranslationClient()
        let session = InterpretationSession(
            apiKeyStore: apiKeyStore,
            audioCapture: audio,
            dualClient: dual
        )
        await session.start()
        await waitUntil { session.state == .listening }

        // When: stopを二重に呼ぶ
        async let first: Void = session.stop()
        async let second: Void = session.stop()
        _ = await (first, second)

        // Then: 最終状態はidleで、graceful closeは1回
        XCTAssertEqual(session.state, .idle)
        XCTAssertEqual(dual.closeGracefullyCallCount, 1)
    }

    func testMissingAPIKeyEntersErrorWithoutConnecting() async {
        // Given: APIキー未設定
        let apiKeyStore = InMemoryAPIKeyStore()
        let audio = FakeRealtimeAudioCaptureService()
        let dual = FakeDualRealtimeTranslationClient()
        let delegate = InterpretationSessionDelegateSpy()
        let session = InterpretationSession(
            apiKeyStore: apiKeyStore,
            audioCapture: audio,
            dualClient: dual
        )
        session.delegate = delegate

        // When: startする
        await session.start()
        await waitUntil { session.state == .error }

        // Then: 接続せずエラーになり、秘密情報はdelegateへ出ない
        XCTAssertEqual(dual.startCallCount, 0)
        XCTAssertEqual(audio.startCallCount, 0)
        XCTAssertFalse(delegate.messages.contains(where: { $0.contains("sk-") }))
        XCTAssertEqual(delegate.messages.first, "APIキーが設定されていません")
    }

    func testRecoverableFailureTriggersReconnectThenListening() async {
        // Given: 1回目のstartだけ失敗するdual
        let apiKeyStore = InMemoryAPIKeyStore(initialKey: "sk-test")
        let audio = FakeRealtimeAudioCaptureService()
        let dual = FakeDualRealtimeTranslationClient()
        dual.startFailuresRemaining = 1
        let session = InterpretationSession(
            apiKeyStore: apiKeyStore,
            audioCapture: audio,
            dualClient: dual,
            activeTickerIntervalNanoseconds: 20_000_000
        )

        // When: startする
        await session.start()

        // Then: 再接続してlisteningへ戻る
        await waitUntil(timeout: 3) { session.state == .listening && dual.startCallCount >= 2 }
        XCTAssertGreaterThanOrEqual(dual.startCallCount, 2)
        await session.stop()
    }

    func testUnknownErrorEntersErrorWithoutReconnect() async {
        // Given: capture start が未知エラーを投げる
        struct UnknownCaptureError: Error {}
        let apiKeyStore = InMemoryAPIKeyStore(initialKey: "sk-test")
        let audio = FakeRealtimeAudioCaptureService()
        audio.startError = UnknownCaptureError()
        let dual = FakeDualRealtimeTranslationClient()
        let session = InterpretationSession(
            apiKeyStore: apiKeyStore,
            audioCapture: audio,
            dualClient: dual,
            activeTickerIntervalNanoseconds: 20_000_000
        )

        // When: startする
        await session.start()
        await waitUntil { session.state == .error }

        // Then: 再接続せず即 error。dual.start は1回だけ
        XCTAssertEqual(session.state, .error)
        XCTAssertEqual(dual.startCallCount, 1)
        XCTAssertEqual(audio.startCallCount, 1)
    }

    func testURLErrorTriggersReconnectThenListening() async {
        // Given: 1回目の dual.start だけ URLError を投げる（startError は one-shot）
        let apiKeyStore = InMemoryAPIKeyStore(initialKey: "sk-test")
        let audio = FakeRealtimeAudioCaptureService()
        let dual = FakeDualRealtimeTranslationClient()
        dual.startError = URLError(.timedOut)
        let session = InterpretationSession(
            apiKeyStore: apiKeyStore,
            audioCapture: audio,
            dualClient: dual,
            activeTickerIntervalNanoseconds: 20_000_000
        )

        // When: startする
        await session.start()
        await waitUntil(timeout: 3) { session.state == .listening && dual.startCallCount >= 2 }

        // Then: URLError でも再接続して listening へ戻る
        XCTAssertEqual(session.state, .listening)
        XCTAssertGreaterThanOrEqual(dual.startCallCount, 2)
        await session.stop()
    }

    func testPOSIXTransportErrorTriggersReconnectThenListening() async {
        // Given: 1回目の dual.start が NSPOSIXErrorDomain の切断を投げる
        let apiKeyStore = InMemoryAPIKeyStore(initialKey: "sk-test")
        let audio = FakeRealtimeAudioCaptureService()
        let dual = FakeDualRealtimeTranslationClient()
        // ENOTCONN (57): URLSession WebSocket 切断でよく見える POSIX コード
        dual.startError = NSError(domain: NSPOSIXErrorDomain, code: 57)
        let session = InterpretationSession(
            apiKeyStore: apiKeyStore,
            audioCapture: audio,
            dualClient: dual,
            activeTickerIntervalNanoseconds: 20_000_000
        )

        // When: startする
        await session.start()
        await waitUntil(timeout: 3) { session.state == .listening && dual.startCallCount >= 2 }

        // Then: URLError 以外の低レベル切断でも再接続する
        XCTAssertEqual(session.state, .listening)
        XCTAssertGreaterThanOrEqual(dual.startCallCount, 2)
        await session.stop()
    }

    func testWebSocketTransportErrorTriggersReconnectThenListening() async {
        // Given: 1回目の dual.start が URLSessionWebSocketTransportError を投げる
        let apiKeyStore = InMemoryAPIKeyStore(initialKey: "sk-test")
        let audio = FakeRealtimeAudioCaptureService()
        let dual = FakeDualRealtimeTranslationClient()
        dual.startError = URLSessionWebSocketTransportError.notConnected
        let session = InterpretationSession(
            apiKeyStore: apiKeyStore,
            audioCapture: audio,
            dualClient: dual,
            activeTickerIntervalNanoseconds: 20_000_000
        )

        // When: startする
        await session.start()
        await waitUntil(timeout: 3) { session.state == .listening && dual.startCallCount >= 2 }

        // Then: transport 境界エラーでも再接続する
        XCTAssertEqual(session.state, .listening)
        XCTAssertGreaterThanOrEqual(dual.startCallCount, 2)
        await session.stop()
    }

    func testInputDeviceChangedReconnectsWithMicBanner() async {
        // Given: listening 中のセッション
        let apiKeyStore = InMemoryAPIKeyStore(initialKey: "sk-test")
        let audio = FakeRealtimeAudioCaptureService()
        let dual = FakeDualRealtimeTranslationClient()
        let delegate = InterpretationSessionDelegateSpy()
        let session = InterpretationSession(
            apiKeyStore: apiKeyStore,
            audioCapture: audio,
            dualClient: dual,
            activeTickerIntervalNanoseconds: 20_000_000
        )
        session.delegate = delegate
        await session.start()
        await waitUntil { session.state == .listening }
        let startCountAtListening = dual.startCallCount

        // When: 入力デバイス変更で capture が終端する
        audio.terminate(with: RealtimeAudioCaptureError.inputDeviceChanged)

        // Then: マイク理由をバナーに載せたうえで再接続し listening へ戻る
        await waitUntil(timeout: 3) {
            session.state == .reconnecting
                && delegate.latestSnapshot?.statusBanner?.contains(UiCopy.text("error.micDeviceChanged")) == true
        }
        await waitUntil(timeout: 3) {
            session.state == .listening && dual.startCallCount > startCountAtListening
        }
        XCTAssertEqual(session.state, .listening)
        await session.stop()
    }

    func testUnknownCaptureTerminationEntersErrorWithoutReconnect() async {
        // Given: listening 中のセッション
        struct UnknownFeederError: Error {}
        let apiKeyStore = InMemoryAPIKeyStore(initialKey: "sk-test")
        let audio = FakeRealtimeAudioCaptureService()
        let dual = FakeDualRealtimeTranslationClient()
        let session = InterpretationSession(
            apiKeyStore: apiKeyStore,
            audioCapture: audio,
            dualClient: dual,
            activeTickerIntervalNanoseconds: 20_000_000
        )
        await session.start()
        await waitUntil { session.state == .listening }
        let startCountAtListening = dual.startCallCount

        // When: feeder が未知エラーで終端する
        audio.terminate(with: UnknownFeederError())
        await waitUntil { session.state == .error }

        // Then: 再接続せず即 error
        XCTAssertEqual(session.state, .error)
        XCTAssertEqual(dual.startCallCount, startCountAtListening)
    }

    func testNonTransientURLErrorEntersErrorWithoutReconnect() async {
        // Given: 証明書系 URLError は再接続対象外
        let apiKeyStore = InMemoryAPIKeyStore(initialKey: "sk-test")
        let audio = FakeRealtimeAudioCaptureService()
        let dual = FakeDualRealtimeTranslationClient()
        dual.startError = URLError(.serverCertificateUntrusted)
        let session = InterpretationSession(
            apiKeyStore: apiKeyStore,
            audioCapture: audio,
            dualClient: dual,
            activeTickerIntervalNanoseconds: 20_000_000
        )

        // When: startする
        await session.start()
        await waitUntil { session.state == .error }

        // Then: 再接続せず即 error
        XCTAssertEqual(session.state, .error)
        XCTAssertEqual(dual.startCallCount, 1)
        XCTAssertEqual(audio.startCallCount, 0)
    }

    func testRuntimeTransportErrorCancelsAudioFeedAndReconnects() async {
        // Given: listening中で、audio feedは次frame待ちのまま
        let apiKeyStore = InMemoryAPIKeyStore(initialKey: "sk-test")
        let audio = FakeRealtimeAudioCaptureService()
        let dual = FakeDualRealtimeTranslationClient()
        let session = InterpretationSession(
            apiKeyStore: apiKeyStore,
            audioCapture: audio,
            dualClient: dual,
            activeTickerIntervalNanoseconds: 20_000_000
        )
        await session.start()
        await waitUntil { session.state == .listening }
        let startCountAtListening = dual.startCallCount
        let forceCloseAtListening = dual.forceCloseCallCount

        // When: ストリーミング中にtransport errorが届く
        dual.emit(
            target: .english,
            event: .error(message: "socket closed", code: "transport")
        )

        // Then: feed側のframe待ちでraceが固まらず、再接続してlisteningへ戻る
        await waitUntil(timeout: 3) {
            dual.forceCloseCallCount > forceCloseAtListening
        }
        await waitUntil(timeout: 3) {
            session.state == .listening && dual.startCallCount > startCountAtListening
        }
        XCTAssertGreaterThan(dual.startCallCount, startCountAtListening)
        await session.stop()
    }

    func testStopDrainsPendingSubtitleUpdate() async throws {
        // Given: 原文と訳文が揃ったlisteningセッション
        let apiKeyStore = InMemoryAPIKeyStore(initialKey: "sk-test")
        let audio = FakeRealtimeAudioCaptureService()
        let dual = FakeDualRealtimeTranslationClient()
        let delegate = InterpretationSessionDelegateSpy()
        let session = InterpretationSession(
            apiKeyStore: apiKeyStore,
            audioCapture: audio,
            dualClient: dual,
            activeTickerIntervalNanoseconds: 50_000_000
        )
        session.delegate = delegate
        await session.start()
        await waitUntil { session.state == .listening }

        // When: deltaを流してからstopする
        dual.emit(
            target: .english,
            event: .inputTranscriptDelta(delta: "こんにちは", eventID: "s1", elapsedMs: 10)
        )
        dual.emit(
            target: .english,
            event: .outputTranscriptDelta(delta: "Hello", eventID: "t1", elapsedMs: 20)
        )
        await waitUntil {
            delegate.latestSnapshot?.current.sourceText.contains("こんにちは") == true
        }
        await session.stop()

        // Then: 停止直後は完全ペアがcurrentに残り、すぐには消えない
        let finalSnapshot = try XCTUnwrap(delegate.latestSnapshot)
        XCTAssertFalse(finalSnapshot.current.sourceText.isEmpty)
        XCTAssertFalse(finalSnapshot.current.translatedText.isEmpty)
        XCTAssertEqual(session.state, .idle)
    }

    func testStopIngestsCompletePairPublishedDuringGracefulClose() async throws {
        // Given: 停止時点では字幕がまだ無く、commit/session.close の drain で完全ペアが届く
        let apiKeyStore = InMemoryAPIKeyStore(initialKey: "sk-test")
        let audio = FakeRealtimeAudioCaptureService()
        let dual = FakeDualRealtimeTranslationClient()
        let delegate = InterpretationSessionDelegateSpy()
        let session = InterpretationSession(
            apiKeyStore: apiKeyStore,
            audioCapture: audio,
            dualClient: dual,
            activeTickerIntervalNanoseconds: 50_000_000
        )
        session.delegate = delegate
        await session.start()
        await waitUntil { session.state == .listening }
        let epoch = await dual.connectionEpoch
        dual.closeGracefullyEvents = [
            RealtimeTranslationStreamEvent(
                target: .english,
                event: .inputTranscriptDelta(
                    delta: "停止時の最終原文",
                    eventID: "close-s1",
                    elapsedMs: 10
                ),
                epoch: epoch
            ),
            RealtimeTranslationStreamEvent(
                target: .english,
                event: .outputTranscriptDelta(
                    delta: "Final source at stop",
                    eventID: "close-t1",
                    elapsedMs: 20
                ),
                epoch: epoch
            ),
        ]

        // When: 利用者が録音を停止する
        await session.stop()

        // Then: close drain の原文+訳文を取り込んで確定する
        XCTAssertEqual(session.state, .idle)
        XCTAssertEqual(dual.beginStopDrainCaptureCallCount, 1)
        let finalized = try XCTUnwrap(
            delegate.finalizedSnapshots.last {
                $0.sourceText.contains("停止時の最終原文")
            }
        )
        XCTAssertEqual(finalized.translatedText, "Final source at stop")
    }

    func testStopIngestsCloseDrainEventsEvenWhenGracefulCloseFails() async throws {
        // Given: close 自体は失敗するが、drain 済みの完全ペアは返せている
        let apiKeyStore = InMemoryAPIKeyStore(initialKey: "sk-test")
        let audio = FakeRealtimeAudioCaptureService()
        let dual = FakeDualRealtimeTranslationClient()
        let delegate = InterpretationSessionDelegateSpy()
        let session = InterpretationSession(
            apiKeyStore: apiKeyStore,
            audioCapture: audio,
            dualClient: dual,
            activeTickerIntervalNanoseconds: 50_000_000
        )
        session.delegate = delegate
        await session.start()
        await waitUntil { session.state == .listening }
        let epoch = await dual.connectionEpoch
        dual.closeGracefullyShouldFail = true
        dual.closeGracefullyEvents = [
            RealtimeTranslationStreamEvent(
                target: .english,
                event: .inputTranscriptDelta(
                    delta: "失敗経路の最終原文",
                    eventID: "close-fail-s1",
                    elapsedMs: 10
                ),
                epoch: epoch
            ),
            RealtimeTranslationStreamEvent(
                target: .english,
                event: .outputTranscriptDelta(
                    delta: "Final source on close failure",
                    eventID: "close-fail-t1",
                    elapsedMs: 20
                ),
                epoch: epoch
            ),
        ]

        // When: 利用者が録音を停止する
        await session.stop()

        // Then: close 失敗でも drain イベントを取り込んで確定する
        XCTAssertEqual(session.state, .idle)
        XCTAssertEqual(dual.closeGracefullyCallCount, 1)
        XCTAssertGreaterThanOrEqual(dual.forceCloseCallCount, 1)
        let finalized = try XCTUnwrap(
            delegate.finalizedSnapshots.last {
                $0.sourceText.contains("失敗経路の最終原文")
            }
        )
        XCTAssertEqual(finalized.translatedText, "Final source on close failure")
    }

    func testReconnectFinalizesCompletePairBeforeNewEpoch() async throws {
        // Given: idle finalize 前の完全な原文+訳文ペア
        let apiKeyStore = InMemoryAPIKeyStore(initialKey: "sk-test")
        let audio = FakeRealtimeAudioCaptureService()
        let dual = FakeDualRealtimeTranslationClient()
        let delegate = InterpretationSessionDelegateSpy()
        let session = InterpretationSession(
            apiKeyStore: apiKeyStore,
            audioCapture: audio,
            dualClient: dual,
            activeTickerIntervalNanoseconds: 20_000_000
        )
        session.delegate = delegate
        await session.start()
        await waitUntil { session.state == .listening }
        let startCountAtListening = dual.startCallCount

        dual.emit(
            target: .english,
            event: .inputTranscriptDelta(delta: "会議を始めます", eventID: "s1", elapsedMs: 10)
        )
        await waitUntil { dual.spokenLanguages.contains(.japanese) }
        dual.emit(
            target: .english,
            event: .outputTranscriptDelta(
                delta: "Let's start the meeting",
                eventID: "t1",
                elapsedMs: 20
            )
        )
        await waitUntil {
            delegate.latestSnapshot?.current.translatedText.contains("Let's start the meeting")
                == true
                && delegate.latestSnapshot?.current.state == .live
        }

        // When: transport error で再接続し beginNewEpoch する
        dual.emit(
            target: .english,
            event: .error(message: "socket closed", code: "transport")
        )

        // Then: 捨てる前に .finalized が発行され、オプトイン字幕記録へ届く
        await waitUntil(timeout: 3) {
            delegate.finalizedSnapshots.contains {
                $0.sourceText.contains("会議を始めます")
                    && $0.translatedText.contains("Let's start the meeting")
            }
        }
        await waitUntil(timeout: 3) {
            session.state == .listening && dual.startCallCount > startCountAtListening
        }
        let finalized = try XCTUnwrap(
            delegate.finalizedSnapshots.last {
                $0.sourceText.contains("会議を始めます")
            }
        )
        XCTAssertEqual(finalized.translatedText, "Let's start the meeting")
        await session.stop()
    }

    func testFatalErrorFinalizesCompletePairBeforeStopping() async throws {
        // Given: idle finalize 前の完全ペア
        let apiKeyStore = InMemoryAPIKeyStore(initialKey: "sk-test")
        let audio = FakeRealtimeAudioCaptureService()
        let dual = FakeDualRealtimeTranslationClient()
        let delegate = InterpretationSessionDelegateSpy()
        let session = InterpretationSession(
            apiKeyStore: apiKeyStore,
            audioCapture: audio,
            dualClient: dual,
            activeTickerIntervalNanoseconds: 20_000_000
        )
        session.delegate = delegate
        await session.start()
        await waitUntil { session.state == .listening }
        let startCountAtListening = dual.startCallCount

        dual.emit(
            target: .english,
            event: .inputTranscriptDelta(delta: "ありがとうございます", eventID: "s1", elapsedMs: 10)
        )
        await waitUntil { dual.spokenLanguages.contains(.japanese) }
        dual.emit(
            target: .english,
            event: .outputTranscriptDelta(delta: "Thank you", eventID: "t1", elapsedMs: 20)
        )
        await waitUntil {
            delegate.latestSnapshot?.current.translatedText.contains("Thank you") == true
                && delegate.latestSnapshot?.current.state == .live
        }

        // When: 認証失敗でセッションが止まる
        dual.emit(
            target: .english,
            event: .error(message: "Incorrect API key provided", code: "invalid_api_key")
        )
        await waitUntil { session.state == .error }

        // Then: エラー遷移前に .finalized が発行される
        await waitUntil {
            delegate.finalizedSnapshots.contains {
                $0.sourceText.contains("ありがとうございます")
                    && $0.translatedText.contains("Thank you")
            }
        }
        XCTAssertEqual(dual.startCallCount, startCountAtListening)
        let finalized = try XCTUnwrap(
            delegate.finalizedSnapshots.last {
                $0.sourceText.contains("ありがとうございます")
            }
        )
        XCTAssertEqual(finalized.translatedText, "Thank you")
    }

    func testPostStopSubtitleClearsAfterRetention() async throws {
        // Given: 短い保持時間と、原文・訳文が揃ったlisteningセッション
        let apiKeyStore = InMemoryAPIKeyStore(initialKey: "sk-test")
        let audio = FakeRealtimeAudioCaptureService()
        let dual = FakeDualRealtimeTranslationClient()
        let delegate = InterpretationSessionDelegateSpy()
        let session = InterpretationSession(
            apiKeyStore: apiKeyStore,
            audioCapture: audio,
            dualClient: dual,
            activeTickerIntervalNanoseconds: 50_000_000,
            postStopSubtitleRetentionNanoseconds: 50_000_000
        )
        session.delegate = delegate
        await session.start()
        await waitUntil { session.state == .listening }

        dual.emit(
            target: .english,
            event: .inputTranscriptDelta(delta: "こんにちは", eventID: "s1", elapsedMs: 10)
        )
        dual.emit(
            target: .english,
            event: .outputTranscriptDelta(delta: "Hello", eventID: "t1", elapsedMs: 20)
        )
        await waitUntil {
            delegate.latestSnapshot?.current.sourceText.contains("こんにちは") == true
        }

        // When: 録音を止め、保持時間を超えるまで待つ
        await session.stop()
        XCTAssertEqual(session.state, .idle)
        XCTAssertFalse(delegate.latestSnapshot?.current.isEmpty == true)
        await waitUntil(timeout: 2) {
            delegate.latestSnapshot?.current.isEmpty == true
        }

        // Then: 一定時間後に字幕ブロックが空になる
        let cleared = try XCTUnwrap(delegate.latestSnapshot)
        XCTAssertTrue(cleared.current.isEmpty)
        XCTAssertNil(cleared.statusBanner)
    }

    func testRestartCancelsPendingPostStopSubtitleClear() async throws {
        // Given: 停止直後に字幕が残っているセッション
        let apiKeyStore = InMemoryAPIKeyStore(initialKey: "sk-test")
        let audio = FakeRealtimeAudioCaptureService()
        let dual = FakeDualRealtimeTranslationClient()
        let delegate = InterpretationSessionDelegateSpy()
        let session = InterpretationSession(
            apiKeyStore: apiKeyStore,
            audioCapture: audio,
            dualClient: dual,
            activeTickerIntervalNanoseconds: 50_000_000,
            postStopSubtitleRetentionNanoseconds: 120_000_000
        )
        session.delegate = delegate
        await session.start()
        await waitUntil { session.state == .listening }
        dual.emit(
            target: .english,
            event: .inputTranscriptDelta(delta: "残す文", eventID: "s1", elapsedMs: 10)
        )
        dual.emit(
            target: .english,
            event: .outputTranscriptDelta(delta: "Keep this", eventID: "t1", elapsedMs: 20)
        )
        await waitUntil {
            delegate.latestSnapshot?.current.sourceText.contains("残す文") == true
        }
        await session.stop()
        XCTAssertFalse(delegate.latestSnapshot?.current.isEmpty == true)

        // When: 保持時間前に再録音し、新しい字幕を出す
        await session.start()
        await waitUntil { session.state == .listening }
        dual.emit(
            target: .english,
            event: .inputTranscriptDelta(delta: "新しい文", eventID: "s2", elapsedMs: 10)
        )
        dual.emit(
            target: .english,
            event: .outputTranscriptDelta(delta: "New sentence", eventID: "t2", elapsedMs: 20)
        )
        await waitUntil {
            delegate.latestSnapshot?.current.sourceText.contains("新しい文") == true
        }
        try? await Task.sleep(nanoseconds: 200_000_000)

        // Then: 前回停止の遅延消去が新セッションの字幕を消さない
        XCTAssertEqual(session.state, .listening)
        XCTAssertTrue(delegate.latestSnapshot?.current.sourceText.contains("新しい文") == true)
        await session.stop()
    }

    func testLanguageFlipFinalizesAndReroutes() async throws {
        // Given: 日本語でルーティング済みのlisteningセッション
        let apiKeyStore = InMemoryAPIKeyStore(initialKey: "sk-test")
        let audio = FakeRealtimeAudioCaptureService()
        let dual = FakeDualRealtimeTranslationClient()
        let delegate = InterpretationSessionDelegateSpy()
        let session = InterpretationSession(
            apiKeyStore: apiKeyStore,
            audioCapture: audio,
            dualClient: dual,
            activeTickerIntervalNanoseconds: 50_000_000
        )
        session.delegate = delegate
        await session.start()
        await waitUntil { session.state == .listening }

        dual.emit(
            target: .english,
            event: .inputTranscriptDelta(delta: "今日は会議です", eventID: "s1", elapsedMs: 1)
        )
        dual.emit(
            target: .english,
            event: .outputTranscriptDelta(delta: "Today is a meeting", eventID: "t1", elapsedMs: 2)
        )
        await waitUntil { dual.spokenLanguages == [.japanese] }
        await waitUntil {
            delegate.latestSnapshot?.current.translatedText.contains("Today") == true
        }
        let resetsAfterJapanese = dual.resetAudioRoutingCallCount

        // When: 間を空けず英語原文が続く
        dual.emit(
            target: .english,
            event: .inputTranscriptDelta(
                delta: " Hello how are you today",
                eventID: "s2",
                elapsedMs: 3
            )
        )

        // Then: 言語切替で再ルーティングし、前セグメントが確定する
        await waitUntil { dual.spokenLanguages == [.japanese, .english] }
        XCTAssertGreaterThan(dual.resetAudioRoutingCallCount, resetsAfterJapanese)
        await waitUntil {
            delegate.latestSnapshot?.current.state == .finalized
                || delegate.latestSnapshot?.current.sourceText.contains("Hello") == true
        }
        await session.stop()
    }

    func testApplyTuningChangeForwardsWhileListening() async throws {
        // Given: listening中のセッションとカスタムtuningProvider
        let apiKeyStore = InMemoryAPIKeyStore(initialKey: "sk-test")
        let audio = FakeRealtimeAudioCaptureService()
        let dual = FakeDualRealtimeTranslationClient()
        var currentTuning = RealtimeSessionTuning.default
        let session = InterpretationSession(
            apiKeyStore: apiKeyStore,
            audioCapture: audio,
            dualClient: dual,
            activeTickerIntervalNanoseconds: 50_000_000,
            tuningProvider: { currentTuning }
        )
        await session.start()
        await waitUntil { session.state == .listening }
        XCTAssertEqual(dual.updateTranscriptionTuningCallCount, 0)

        // When: tuningを変えてapplyTuningChangeする
        currentTuning = RealtimeSessionTuning(
            noiseReduction: .nearField,
            transcriptionDelay: .high,
            transcriptionPrompt: "Updated glossary",
            transcriptionKeywords: ["Acme"]
        )
        await session.applyTuningChange()

        // Then: dualへ最新tuningが転送される
        XCTAssertEqual(dual.updateTranscriptionTuningCallCount, 1)
        XCTAssertEqual(dual.lastTuning?.transcriptionPrompt, "Updated glossary")
        XCTAssertEqual(dual.lastTuning?.transcriptionKeywords, ["Acme"])
        await session.stop()
    }

    func testApplyTuningChangeIsNoOpWhenIdle() async throws {
        // Given: idleのセッション
        let apiKeyStore = InMemoryAPIKeyStore(initialKey: "sk-test")
        let audio = FakeRealtimeAudioCaptureService()
        let dual = FakeDualRealtimeTranslationClient()
        let session = InterpretationSession(
            apiKeyStore: apiKeyStore,
            audioCapture: audio,
            dualClient: dual
        )

        // When: idleでapplyTuningChangeする
        await session.applyTuningChange()

        // Then: 転送されない
        XCTAssertEqual(dual.updateTranscriptionTuningCallCount, 0)
    }

    func testInvalidAPIKeyRuntimeErrorDoesNotLeakKeyMaterial() async throws {
        // Given: listening中のセッション
        let apiKeyStore = InMemoryAPIKeyStore(initialKey: "sk-test")
        let audio = FakeRealtimeAudioCaptureService()
        let dual = FakeDualRealtimeTranslationClient()
        let delegate = InterpretationSessionDelegateSpy()
        let session = InterpretationSession(
            apiKeyStore: apiKeyStore,
            audioCapture: audio,
            dualClient: dual,
            activeTickerIntervalNanoseconds: 50_000_000
        )
        session.delegate = delegate
        await session.start()
        await waitUntil { session.state == .listening }

        // When: キー断片を含むinvalid_api_keyエラーが届く
        dual.emit(
            target: .english,
            event: .error(
                message: "Incorrect API key provided: sk-leak-example",
                code: "invalid_api_key"
            )
        )
        await waitUntil { session.state == .error }

        // Then: 認証エラーになり、sk-や原文メッセージはdelegateへ出ない
        XCTAssertEqual(delegate.messages.first, "OpenAI APIキーが無効です")
        XCTAssertFalse(delegate.messages.contains(where: { $0.contains("sk-") }))
        XCTAssertFalse(
            delegate.latestSnapshot?.statusBanner?.contains("sk-") == true
        )
        await session.stop()
    }

    func testNonAuthServerErrorRedactsAPIKeyLikePayload() async throws {
        // Given: listening中のセッション
        let apiKeyStore = InMemoryAPIKeyStore(initialKey: "sk-test")
        let audio = FakeRealtimeAudioCaptureService()
        let dual = FakeDualRealtimeTranslationClient()
        let delegate = InterpretationSessionDelegateSpy()
        let session = InterpretationSession(
            apiKeyStore: apiKeyStore,
            audioCapture: audio,
            dualClient: dual,
            activeTickerIntervalNanoseconds: 50_000_000
        )
        session.delegate = delegate
        await session.start()
        await waitUntil { session.state == .listening }

        // When: codeは非認証だが文言にAPIキー断片が含まれる
        dual.emit(
            target: .english,
            event: .error(
                message: "Provider echo included sk-should-not-appear",
                code: "server_error"
            )
        )
        await waitUntil { session.state == .error }

        // Then: 汎用エラー文言に置換され秘密情報は出ない
        XCTAssertEqual(delegate.messages.first, "翻訳サーバーでエラーが発生しました")
        XCTAssertFalse(delegate.messages.contains(where: { $0.contains("sk-") }))
        await session.stop()
    }

    func testAuthorityLikeServerErrorIsNotTreatedAsInvalidAPIKey() async throws {
        // Given: listening中のセッション
        let apiKeyStore = InMemoryAPIKeyStore(initialKey: "sk-test")
        let audio = FakeRealtimeAudioCaptureService()
        let dual = FakeDualRealtimeTranslationClient()
        let delegate = InterpretationSessionDelegateSpy()
        let session = InterpretationSession(
            apiKeyStore: apiKeyStore,
            audioCapture: audio,
            dualClient: dual,
            activeTickerIntervalNanoseconds: 50_000_000
        )
        session.delegate = delegate
        await session.start()
        await waitUntil { session.state == .listening }

        // When: auth部分文字列を含むが非認証のエラーが届く
        dual.emit(
            target: .english,
            event: .error(
                message: "certificate authority rejected the peer (code 4010)",
                code: "authority_mismatch"
            )
        )
        await waitUntil { session.state == .error }

        // Then: 無効APIキー扱いではなく、サーバー文言（またはサニタイズ結果）経路になる
        XCTAssertNotEqual(delegate.messages.first, "OpenAI APIキーが無効です")
        XCTAssertEqual(
            delegate.messages.first,
            "certificate authority rejected the peer (code 4010)"
        )
        await session.stop()
    }

    func testNonFlippingSourceDeltaStreamDoesNotGrowRoutingBufferWithoutBound() async throws {
        // Given: 文字種の反転を起こさない英語 delta がサーバから連続で流れ続ける
        let apiKeyStore = InMemoryAPIKeyStore(initialKey: "sk-test")
        let audio = FakeRealtimeAudioCaptureService()
        let dual = FakeDualRealtimeTranslationClient()
        let session = InterpretationSession(
            apiKeyStore: apiKeyStore,
            audioCapture: audio,
            dualClient: dual,
            activeTickerIntervalNanoseconds: 50_000_000
        )
        await session.start()
        await waitUntil { session.state == .listening }

        dual.emit(
            target: .english,
            event: .inputTranscriptDelta(
                delta: "we keep talking in english ",
                eventID: "s0",
                elapsedMs: 1
            )
        )
        await waitUntil { dual.spokenLanguages.count > 0 }

        var processedDeltaCount = 0
        session.beforeAssemblerIngestForTests = {
            processedDeltaCount += 1
        }
        let nonFlippingDeltaCount = 200
        for index in 0..<nonFlippingDeltaCount {
            dual.emit(
                target: .english,
                event: .inputTranscriptDelta(
                    delta: "and we never flip the script ",
                    eventID: "s\(index + 1)",
                    elapsedMs: index + 2
                )
            )
        }

        // When: 反転前に大量 delta の取り込み完了を待ち、その時点で上限を検証する
        await waitUntil { processedDeltaCount >= nonFlippingDeltaCount }
        XCTAssertLessThanOrEqual(
            session.routingSourceTextLengthForTests,
            InterpretationSession.routingSourceTextMaxLength,
            "routing buffer length \(session.routingSourceTextLengthForTests) exceeded the cap before flip"
        )

        dual.emit(
            target: .english,
            event: .inputTranscriptDelta(
                delta: "ここで日本語へ反転します",
                eventID: "flip",
                elapsedMs: 999
            )
        )
        await waitUntil { dual.spokenLanguages.count > 1 }

        // Then: routing 判定バッファは上限までで打ち切られ、その後の反転検出も壊れない
        XCTAssertEqual(dual.spokenLanguages, [.english, .japanese])
        XCTAssertLessThanOrEqual(
            session.routingSourceTextLengthForTests,
            InterpretationSession.routingSourceTextMaxLength,
            "routing buffer length \(session.routingSourceTextLengthForTests) exceeded the cap after flip"
        )
        await session.stop()
    }

    func testWideWhitespaceBetweenLatinWordsStillFlipsJapaneseToEnglish() async throws {
        // Given: 日本語セグメントのあと、長い空白 run で隔てられた複数語の英語 delta
        let apiKeyStore = InMemoryAPIKeyStore(initialKey: "sk-test")
        let audio = FakeRealtimeAudioCaptureService()
        let dual = FakeDualRealtimeTranslationClient()
        let session = InterpretationSession(
            apiKeyStore: apiKeyStore,
            audioCapture: audio,
            dualClient: dual,
            activeTickerIntervalNanoseconds: 50_000_000
        )
        await session.start()
        await waitUntil { session.state == .listening }

        dual.emit(
            target: .english,
            event: .inputTranscriptDelta(
                delta: "これはテストです",
                eventID: "s1",
                elapsedMs: 1
            )
        )
        await waitUntil { dual.spokenLanguages.count > 0 }
        XCTAssertEqual(dual.spokenLanguages.first, .japanese)

        // When: UTF-16 文字数キャップだけだと末尾 1 語しか残らない入力を取り込む
        let gap = String(
            repeating: " ",
            count: InterpretationSession.routingSourceTextMaxLength + 32
        )
        dual.emit(
            target: .english,
            event: .inputTranscriptDelta(
                delta: "aa bb cc dd ee ff gg" + gap + " hh",
                eventID: "s2",
                elapsedMs: 2
            )
        )
        await waitUntil { dual.spokenLanguages.count > 1 }

        // Then: RecentEvidence ウィンドウを保ち英語反転できる
        XCTAssertEqual(dual.spokenLanguages, [.japanese, .english])
        XCTAssertLessThanOrEqual(
            session.routingSourceTextLengthForTests,
            InterpretationSession.routingSourceTextMaxLength,
            "routing buffer length \(session.routingSourceTextLengthForTests) exceeded the cap"
        )
        await session.stop()
    }

    func testTrimRoutingSourceTextKeepsRecentEvidenceWindow() {
        // Given: 末尾ウィンドウより長い原文
        let prefix = String(repeating: "あ", count: 64)
        let tail = "hello world today"
        let trimmed = InterpretationSession.trimRoutingSourceText(prefix + tail, pair: .jaEn)

        // When/Then: 末尾の非空白 scalar ウィンドウ相当が残り、上限を超えない
        XCTAssertTrue(trimmed.hasSuffix(tail) || trimmed.contains("world"))
        XCTAssertLessThanOrEqual(
            trimmed.utf16.count,
            InterpretationSession.routingSourceTextMaxLength
        )
    }

    func testEnEsLongWordWindowIsPreservedForRouting() async {
        // Given: en-es で英語 target 確定後、scalar 上限を超える長いスペイン語語窓
        let dual = FakeDualRealtimeTranslationClient()
        let session = InterpretationSession(
            apiKeyStore: InMemoryAPIKeyStore(initialKey: "sk-test"),
            audioCapture: FakeRealtimeAudioCaptureService(),
            dualClient: dual,
            activeTickerIntervalNanoseconds: 50_000_000,
            languagePairProvider: { .enEs }
        )
        await session.start()
        await waitUntil { session.state == .listening }

        dual.publishSourceDelta("the and is are of to it that")
        await waitUntil { dual.selectedTargets == [.spanish] }

        // When: 先頭側にだけスペイン語証拠があり、後ろは長い filler。scalar 切り詰めだと証拠が消える。
        let longToken = String(repeating: "x", count: 40)
        let filler = Array(
            repeating: longToken,
            count: SpokenLanguageDetector.enEsWindow - 2
        ).joined(separator: " ")
        let spanishWindow = "está aquí " + filler
        dual.publishSourceDelta(spanishWindow)
        dual.publishSourceDelta(" " + spanishWindow)

        // Then: 語窓を保ち English target へ切り替わる
        await waitUntil {
            dual.selectedTargets == [.spanish, .english]
        }
        XCTAssertEqual(dual.selectedTargets, [.spanish, .english])
        await session.stop()
    }
}

// MARK: - Fakes

@MainActor
final class FakeRealtimeAudioCaptureService: RealtimeAudioCaptureServicing {
    private(set) var frames: AsyncStream<Data>
    private var continuation: AsyncStream<Data>.Continuation?
    private(set) var startCallCount = 0
    private(set) var stopCallCount = 0
    private(set) var isRunning = false
    private(set) var terminationError: Error?
    var startError: Error?

    init() {
        var continuation: AsyncStream<Data>.Continuation!
        frames = AsyncStream { continuation = $0 }
        self.continuation = continuation
    }

    func start() async throws {
        startCallCount += 1
        terminationError = nil
        if let startError {
            throw startError
        }
        continuation?.finish()
        var next: AsyncStream<Data>.Continuation!
        frames = AsyncStream { next = $0 }
        continuation = next
        isRunning = true
    }

    func stop() async {
        stopCallCount += 1
        isRunning = false
        continuation?.finish()
        continuation = nil
    }

    func emit(_ frame: Data) {
        _ = continuation?.yield(frame)
    }

    func terminate(with error: Error) {
        terminationError = error
        continuation?.finish()
        continuation = nil
    }
}

final class FakeDualRealtimeTranslationClient: DualRealtimeTranslationClienting, @unchecked Sendable {
    private let state = OSAllocatedUnfairLock(initialState: ClientState())

    private struct ClientState {
        var eventStream: AsyncStream<RealtimeTranslationStreamEvent>
        var eventContinuation: AsyncStream<RealtimeTranslationStreamEvent>.Continuation?
        var connectionEpoch = 0
        var appendedFrames: [Data] = []

        init() {
            var continuation: AsyncStream<RealtimeTranslationStreamEvent>.Continuation!
            eventStream = AsyncStream { continuation = $0 }
            eventContinuation = continuation
        }
    }

    private(set) var startCallCount = 0
    private(set) var closeGracefullyCallCount = 0
    private(set) var forceCloseCallCount = 0
    private(set) var spokenLanguages: [SpokenLanguage] = []
    private(set) var selectedTargets: [RealtimeTranslationOutputLanguage] = []
    private(set) var resetAudioRoutingCallCount = 0
    private(set) var updateTranscriptionTuningCallCount = 0
    private(set) var lastTuning: RealtimeSessionTuning?
    private(set) var lastLanguagePair: LanguagePair = .jaEn
    var startGate: CheckedContinuationBox?
    var startFailuresRemaining = 0
    var startError: Error?
    /// CloseGracefully 時に返す close drain イベント（停止時取り込みの回帰用）。
    var closeGracefullyEvents: [RealtimeTranslationStreamEvent] = []
    /// CloseGracefully 中に失敗したことにして forceClose へ回す（drain 自体は返す）。
    var closeGracefullyShouldFail = false
    private(set) var beginStopDrainCaptureCallCount = 0

    var connectionEpoch: Int {
        get async {
            state.withLock(\.connectionEpoch)
        }
    }

    var events: AsyncStream<RealtimeTranslationStreamEvent> {
        get async {
            state.withLock(\.eventStream)
        }
    }

    func start(
        apiKey: String,
        tuning: RealtimeSessionTuning,
        pair: LanguagePair
    ) async throws {
        startCallCount += 1
        lastTuning = tuning
        lastLanguagePair = pair
        if let startGate {
            try await withTaskCancellationHandler {
                try await withCheckedThrowingContinuation {
                    (continuation: CheckedContinuation<Void, Error>) in
                    if Task.isCancelled {
                        continuation.resume(throwing: CancellationError())
                        return
                    }
                    startGate.throwingContinuation = continuation
                }
            } onCancel: {
                startGate.resumeThrowing(CancellationError())
            }
        }
        try Task.checkCancellation()
        if let startError {
            // one-shot: 再接続後の start を成功させる
            self.startError = nil
            throw startError
        }
        if startFailuresRemaining > 0 {
            startFailuresRemaining -= 1
            throw RealtimeTranslationError.recoverableTransportFailure("forced start failure")
        }

        state.withLock { state in
            state.connectionEpoch += 1
            state.eventContinuation?.finish()
            var continuation: AsyncStream<RealtimeTranslationStreamEvent>.Continuation!
            state.eventStream = AsyncStream { continuation = $0 }
            state.eventContinuation = continuation
        }
    }

    func appendAudioFrame(_ pcm16LE: Data) async throws {
        state.withLock { state in
            state.appendedFrames.append(pcm16LE)
        }
    }

    func selectTranslationTarget(_ target: RealtimeTranslationOutputLanguage?) async throws {
        guard let target else { return }
        selectedTargets.append(target)
        if let language = lastLanguagePair.counterpart(of: target) {
            spokenLanguages.append(language)
        }
    }

    func publishSourceDelta(_ delta: String) {
        state.withLock { state in
            state.eventContinuation?.yield(
                RealtimeTranslationStreamEvent(
                    lane: .source,
                    event: .inputTranscriptDelta(
                        delta: delta,
                        eventID: UUID().uuidString,
                        elapsedMs: nil
                    ),
                    epoch: state.connectionEpoch
                )
            )
        }
    }

    func updateTranscriptionTuning(_ tuning: RealtimeSessionTuning) async throws {
        updateTranscriptionTuningCallCount += 1
        lastTuning = tuning
    }

    func resetAudioRouting() async {
        resetAudioRoutingCallCount += 1
    }

    func beginStopDrainCapture() async {
        beginStopDrainCaptureCallCount += 1
    }

    @discardableResult
    func closeGracefully() async -> [RealtimeTranslationStreamEvent] {
        closeGracefullyCallCount += 1
        let drained = closeGracefullyEvents
        closeGracefullyEvents = []
        finishEvents()
        if closeGracefullyShouldFail {
            closeGracefullyShouldFail = false
            await forceClose()
        }
        return drained
    }

    func forceClose() async {
        forceCloseCallCount += 1
        state.withLock { state in
            state.connectionEpoch += 1
        }
        finishEvents()
    }

    func emit(
        target: RealtimeTranslationOutputLanguage,
        event: RealtimeTranslationServerEvent,
        epoch: Int? = nil
    ) {
        let payload = state.withLock { state -> (AsyncStream<RealtimeTranslationStreamEvent>.Continuation?, Int) in
            (state.eventContinuation, epoch ?? state.connectionEpoch)
        }
        payload.0?.yield(
            RealtimeTranslationStreamEvent(
                target: target,
                event: event,
                epoch: payload.1
            )
        )
    }

    private func finishEvents() {
        state.withLock { state in
            state.eventContinuation?.finish()
            state.eventContinuation = nil
        }
    }
}

final class CheckedContinuationBox: @unchecked Sendable {
    var continuation: CheckedContinuation<Void, Never>?
    var throwingContinuation: CheckedContinuation<Void, Error>?

    func resume() {
        if let throwingContinuation {
            self.throwingContinuation = nil
            throwingContinuation.resume()
            return
        }
        continuation?.resume()
        continuation = nil
    }

    func resumeThrowing(_ error: Error) {
        if let throwingContinuation {
            self.throwingContinuation = nil
            throwingContinuation.resume(throwing: error)
            return
        }
        continuation?.resume()
        continuation = nil
    }
}

@MainActor
final class InterpretationSessionDelegateSpy: InterpretationSessionDelegate {
    private(set) var states: [TranslationState] = []
    private(set) var messages: [String] = []
    private(set) var latestSnapshot: SubtitleSnapshot?
    private(set) var finalizedSnapshots: [LiveSubtitle] = []

    func interpretationSession(
        _ session: InterpretationSession,
        didUpdateState state: TranslationState
    ) {
        states.append(state)
    }

    func interpretationSession(
        _ session: InterpretationSession,
        didUpdateSubtitles snapshot: SubtitleSnapshot
    ) {
        latestSnapshot = snapshot
        if snapshot.current.state == .finalized {
            finalizedSnapshots.append(snapshot.current)
        }
    }

    func interpretationSession(
        _ session: InterpretationSession,
        didEncounterMessage message: String
    ) {
        messages.append(message)
    }
}

@MainActor
func waitUntil(
    timeout: TimeInterval = 1.5,
    file: StaticString = #filePath,
    line: UInt = #line,
    _ condition: @escaping () -> Bool
) async {
    let deadline = Date().addingTimeInterval(timeout)
    while Date() < deadline {
        if condition() { return }
        try? await Task.sleep(nanoseconds: 10_000_000)
    }
    XCTFail("Condition not met before timeout", file: file, line: line)
}
