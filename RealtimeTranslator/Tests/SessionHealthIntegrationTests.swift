import XCTest
@testable import RealtimeTranslator

/// 受信停止監視（診断のみ）の InterpretationSession 統合テスト。
/// 単調時計・壁時計は差し替え、検知は delegate 通知と snapshot で観測する。
@MainActor
final class SessionHealthIntegrationTests: XCTestCase {
    private final class FakeHealthClock: @unchecked Sendable {
        var now: Duration = .zero
        var wallNow: TimeInterval = 1_700_000_000
    }

    private func makeSession(
        clock: FakeHealthClock,
        audio: FakeRealtimeAudioCaptureService,
        dual: FakeDualRealtimeTranslationClient,
        apiKey: String = "sk-test"
    ) -> InterpretationSession {
        InterpretationSession(
            apiKeyStore: InMemoryAPIKeyStore(initialKey: apiKey),
            audioCapture: audio,
            dualClient: dual,
            activeTickerIntervalNanoseconds: 20_000_000,
            healthNow: { clock.now },
            wallClockNow: { clock.wallNow }
        )
    }

    /// ピークが閾値を超える PCM16 frame（32767 を1サンプル含む）。
    private var activeFrame: Data {
        var frame = Data(repeating: 0, count: 4800)
        frame[0] = 0xFF
        frame[1] = 0x7F
        return frame
    }

    /// frame を emit し、capture/send が現在の単調時計で記録されるまで待つ。
    /// 先に時計を進めると ticker が古い lastCapture で captureStalled を出すため、
    /// 常に emit → 消費待ち → 時計進行 の順にする。
    private func emitActiveFrame(
        audio: FakeRealtimeAudioCaptureService,
        dual: FakeDualRealtimeTranslationClient
    ) async {
        let expected = dual.appendedFrameCount + 1
        audio.emit(activeFrame)
        await waitUntil { dual.appendedFrameCount >= expected }
    }

    // Given: 接続直後だけイベントが届き、その後受信が止まる
    // When: 単調時計を受信停止閾値まで進める
    // Then: receiveStalled が一度だけ delegate へ通知される
    func testReceiveStalledFiresOnce() async {
        let clock = FakeHealthClock()
        let audio = FakeRealtimeAudioCaptureService()
        let dual = FakeDualRealtimeTranslationClient()
        let delegate = InterpretationSessionDelegateSpy()
        let session = makeSession(clock: clock, audio: audio, dual: dual)
        session.delegate = delegate

        await session.start()
        await waitUntil { session.state == .listening }

        dual.publishSourceDelta("こんにちは")
        try? await Task.sleep(nanoseconds: 100_000_000)

        // 活動を保ったまま時計を進める（capture/send の停滞を避ける）。
        await emitActiveFrame(audio: audio, dual: dual)
        clock.now = .milliseconds(15_000)
        await emitActiveFrame(audio: audio, dual: dual)
        clock.now = .milliseconds(28_000)
        await emitActiveFrame(audio: audio, dual: dual)
        clock.now = .milliseconds(30_100)

        await waitUntil {
            delegate.healthDetections.contains { $0.kind == .receiveStalled }
        }

        clock.now = .milliseconds(61_000)
        try? await Task.sleep(nanoseconds: 200_000_000)

        XCTAssertEqual(
            delegate.healthDetections.filter { $0.kind == .receiveStalled }.count,
            1
        )
        await session.stop()
    }

    // Given: 選択 lane へ翻訳 delta が来ず、原文の進捗だけが続く
    // When: 原文進捗の停止が translationStall を超える
    // Then: translationStalled が選択 lane 付きで通知される
    func testSourceOnlyProgressEmitsTranslationStalled() async {
        let clock = FakeHealthClock()
        let audio = FakeRealtimeAudioCaptureService()
        let dual = FakeDualRealtimeTranslationClient()
        let delegate = InterpretationSessionDelegateSpy()
        let session = makeSession(clock: clock, audio: audio, dual: dual)
        session.delegate = delegate

        await session.start()
        await waitUntil { session.state == .listening }

        // 日本語の原文で en lane を選択させる。
        dual.publishSourceDelta("これは日本語のテストです")
        await waitUntil { dual.spokenLanguages.count == 1 }

        clock.now = .milliseconds(2_000)
        dual.publishSourceDelta("まだ話しています")
        try? await Task.sleep(nanoseconds: 100_000_000)
        await emitActiveFrame(audio: audio, dual: dual)

        clock.now = .milliseconds(15_000)
        await emitActiveFrame(audio: audio, dual: dual)
        clock.now = .milliseconds(17_500)

        await waitUntil {
            delegate.healthDetections.contains { $0.kind == .translationStalled }
        }
        let detection = delegate.healthDetections.first { $0.kind == .translationStalled }
        XCTAssertEqual(detection?.lane, .translation(.english))
        await session.stop()
    }

    // Given: 選択 lane がなく、未選択 lane の翻訳 delta だけが届く
    // When: 時計を小刻みに進めて evaluate する
    // Then: 検知は発火せず snapshot の selectedLane は nil のまま
    func testUnselectedLaneProgressEmitsNothing() async {
        let clock = FakeHealthClock()
        let audio = FakeRealtimeAudioCaptureService()
        let dual = FakeDualRealtimeTranslationClient()
        let delegate = InterpretationSessionDelegateSpy()
        let session = makeSession(clock: clock, audio: audio, dual: dual)
        session.delegate = delegate

        await session.start()
        await waitUntil { session.state == .listening }

        // 未選択の en lane に delta だけ届く（routing 判定前の echo 相当）。
        dual.emit(
            target: .english,
            event: .outputTranscriptDelta(delta: "unselected", eventID: nil, elapsedMs: nil)
        )
        // captureStall（3 s）を跨がないよう 2 s 刻みで活動を供給しながら進める。
        for step in 1...8 {
            await emitActiveFrame(audio: audio, dual: dual)
            clock.now = .milliseconds(2_000 * step)
        }
        try? await Task.sleep(nanoseconds: 100_000_000)

        XCTAssertTrue(delegate.healthDetections.isEmpty)
        XCTAssertNil(session.latestHealthSnapshot?.selectedLane)
        await session.stop()
    }

    // Given: session.expires_at 受信後に壁時計が +1 時間ずれる
    // When: 単調時計を expiryNear / expired の期限まで進める
    // Then: 検知時刻と remaining は壁時計の影響を受けない
    func testWallClockShiftDoesNotAffectExpiry() async {
        let clock = FakeHealthClock()
        let audio = FakeRealtimeAudioCaptureService()
        let dual = FakeDualRealtimeTranslationClient()
        let delegate = InterpretationSessionDelegateSpy()
        let session = makeSession(clock: clock, audio: audio, dual: dual)
        session.delegate = delegate

        // fake は start の終わりに deliveryState を張り替えるため、handshake 相当の
        // expiry 記録は audio start のゲート（start 完了後・listening 直前）で行う。
        audio.startGate = CheckedContinuationBox()
        let startTask = Task { await session.start() }
        await waitUntil { audio.startCallCount == 1 }
        dual.setSessionExpiry(Int(clock.wallNow) + 130, lane: .translation(.english))
        audio.startGate?.resume()
        audio.startGate = nil
        await waitUntil { session.state == .listening }

        await emitActiveFrame(audio: audio, dual: dual)

        // NTP 補正相当の壁時計ジャンプを挟む。
        clock.wallNow += 3_600
        clock.now = .milliseconds(10_100)

        await waitUntil {
            delegate.healthDetections.contains { $0.kind == .expiryNear }
        }
        let near = delegate.healthDetections.first { $0.kind == .expiryNear }
        XCTAssertEqual(near?.lane, .translation(.english))
        XCTAssertEqual(near?.elapsed, .milliseconds(10_100))
        // remaining は接続時に確定した期限への残りで、壁時計のずれを含まない。
        XCTAssertEqual(near?.remaining, .milliseconds(119_900))

        clock.now = .milliseconds(130_100)
        await waitUntil {
            delegate.healthDetections.contains { $0.kind == .expired }
        }
        await session.stop()
        startTask.cancel()
    }

    // Given: APIキー・原文・訳文・サーバー文言に相当する秘密文字列を仕込む
    // When: 検知・snapshot・終了診断の文字列表現を集める
    // Then: いずれの文字列にも秘密文字列が含まれない
    func testDiagnosticsContainNoSecrets() async {
        let markers = ["sk-test-secret-999", "秘密の原文", "secret translation", "raw server text"]
        let clock = FakeHealthClock()
        let audio = FakeRealtimeAudioCaptureService()
        let dual = FakeDualRealtimeTranslationClient()
        let delegate = InterpretationSessionDelegateSpy()
        let session = makeSession(
            clock: clock,
            audio: audio,
            dual: dual,
            apiKey: "sk-test-secret-999"
        )
        session.delegate = delegate

        await session.start()
        await waitUntil { session.state == .listening }

        await emitActiveFrame(audio: audio, dual: dual)
        dual.publishSourceDelta("秘密の原文")
        dual.emit(
            target: .english,
            event: .outputTranscriptDelta(
                delta: "secret translation",
                eventID: nil,
                elapsedMs: nil
            )
        )
        dual.emit(
            target: .english,
            event: .error(message: "raw server text", code: nil, errorType: nil)
        )
        try? await Task.sleep(nanoseconds: 150_000_000)

        // 診断文字列をすべて集める（検知の description / status 行 / snapshot / 終了診断）。
        var diagnostics = delegate.healthDetections.map(\.description)
        diagnostics += delegate.healthDetections.map(\.statusLineFragment)
        if let snapshot = session.latestHealthSnapshot {
            diagnostics.append(snapshot.description)
        }
        var monitor = SessionHealthMonitor()
        monitor.beginGeneration(generation: 1, epoch: 1, isRecovery: false, now: .zero)
        diagnostics.append(
            monitor.recordTermination(kind: .fatalServerError, now: .seconds(5)).description
        )

        for diagnostic in diagnostics {
            for marker in markers {
                XCTAssertFalse(
                    diagnostic.contains(marker),
                    "diagnostic leaked marker: \(diagnostic)"
                )
            }
        }
        await session.stop()
    }

    // Given: handshake で source lane の受信数が記録済みの接続
    // When: Listening 直後に活動 frame が届き、その後受信が止まる
    // Then: handshake 受信を新規受信と誤認せず receiveStalled が発火する（sourceStalled ではない）
    func testHandshakeReceivesDoNotMaskReceiveStall() async {
        let clock = FakeHealthClock()
        let audio = FakeRealtimeAudioCaptureService()
        let dual = FakeDualRealtimeTranslationClient()
        dual.handshakeReceiveCount = 2
        let delegate = InterpretationSessionDelegateSpy()
        let session = makeSession(clock: clock, audio: audio, dual: dual)
        session.delegate = delegate

        await session.start()
        await waitUntil { session.state == .listening }

        await emitActiveFrame(audio: audio, dual: dual)
        // 最初の tick が走るのを待ってから次の活動を送る。
        try? await Task.sleep(nanoseconds: 100_000_000)
        clock.now = .milliseconds(15_000)
        await emitActiveFrame(audio: audio, dual: dual)
        clock.now = .milliseconds(30_100)

        await waitUntil {
            delegate.healthDetections.contains { $0.kind == .receiveStalled }
        }
        XCTAssertFalse(delegate.healthDetections.contains { $0.kind == .sourceStalled })
        await session.stop()
    }

    // Given: 初回 handshake が認証失敗で落ちる接続
    // When: start が error へ終わる
    // Then: 世代未開始でも attempt 情報の終了診断が epoch=1・非負の duration で記録される
    func testInitialHandshakeFailureEmitsAttemptTermination() async {
        let clock = FakeHealthClock()
        let audio = FakeRealtimeAudioCaptureService()
        let dual = FakeDualRealtimeTranslationClient()
        dual.startError = RealtimeTranslationError.authenticationFailed
        let session = makeSession(clock: clock, audio: audio, dual: dual)

        await session.start()
        await waitUntil { session.state == .error }

        let diagnostic = session.latestHealthTermination
        XCTAssertEqual(diagnostic?.epoch, 1)
        XCTAssertEqual(diagnostic?.kind, .authenticationFailed)
        XCTAssertGreaterThanOrEqual(diagnostic?.connectionDuration ?? .zero, .zero)
        await session.stop()
    }

    // Given: Listening 中の接続（epoch 1）が recoverable 切断され、再接続 handshake が致命的に失敗する
    // When: 2 回目の試行が error へ終わる
    // Then: 終了診断は epoch=2 で、試行開始からの非負の duration を持つ
    func testReconnectHandshakeFailureEmitsAttemptTermination() async {
        let clock = FakeHealthClock()
        let audio = FakeRealtimeAudioCaptureService()
        let dual = FakeDualRealtimeTranslationClient()
        let session = makeSession(clock: clock, audio: audio, dual: dual)

        await session.start()
        await waitUntil { session.state == .listening }

        dual.startError = RealtimeTranslationError.fatalServerError(
            RealtimeTranslationError.SanitizedMessage("upstream boom")
        )
        dual.emit(
            target: .english,
            event: .error(message: "transport glitch", code: "server_error", errorType: nil)
        )
        await waitUntil { session.state == .error }

        let diagnostic = session.latestHealthTermination
        XCTAssertEqual(diagnostic?.epoch, 2)
        XCTAssertEqual(diagnostic?.kind, .fatalServerError)
        XCTAssertGreaterThanOrEqual(diagnostic?.connectionDuration ?? .zero, .zero)
        await session.stop()
    }
}
