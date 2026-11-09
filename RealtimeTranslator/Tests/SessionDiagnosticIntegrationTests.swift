import XCTest
@testable import RealtimeTranslator

@MainActor
final class SessionDiagnosticIntegrationTests: XCTestCase {
    private final class Clock: @unchecked Sendable {
        var now: Duration = .zero
    }

    // Given: 接続開始時の予約epochは0で、接続開始後にepochが1へ進む
    // When: epochを更新しListening世代を開始してsnapshotを記録する
    // Then: attempt開始とepoch bindingを分け、snapshotと同じ接続を相関できる
    func testAttemptEpochBindingPrecedesSnapshot() throws {
        var events: [SessionDiagnosticEvent] = []
        let bookkeeper = SessionHealthBookkeeper(
            thresholds: SessionHealthThresholds(), healthNow: { .zero },
            wallClockNow: { 0 }, reservedEpochProvider: { 1 }, diagnosticSink: { events.append($0) }
        )
        bookkeeper.beginAttempt(lifecycleGeneration: 3, reservedEpoch: 0)
        bookkeeper.updateAttemptEpoch(1)
        bookkeeper.updateAttemptEpoch(1)

        let state = EventDeliveryState(epoch: 1)
        bookkeeper.beginGeneration(generation: 3, epoch: 1, deliveryState: state)
        let feed = EventFeed(events: AsyncStream { $0.finish() }, runToken: 1, deliveryState: state)
        _ = bookkeeper.tick(feed: feed)

        XCTAssertEqual(events.count, 3)
        guard case .attemptStarted(let startGeneration, let attempt) = events[0] else {
            return XCTFail("attemptStarted が必要")
        }
        XCTAssertEqual(startGeneration, 3)
        XCTAssertEqual(attempt, 1)
        XCTAssertFalse(events[0].description.contains("epoch="))
        guard case .attemptEpoch(let epochGeneration, let epochAttempt, let epoch) = events[1] else {
            return XCTFail("attemptEpoch が必要")
        }
        XCTAssertEqual(epochGeneration, startGeneration)
        XCTAssertEqual(epochAttempt, attempt)
        XCTAssertEqual(epoch, 1)
        guard case .snapshot(let snapshot, _) = events[2] else {
            return XCTFail("snapshot が必要")
        }
        XCTAssertEqual(snapshot.generation, epochGeneration)
        XCTAssertEqual(snapshot.epoch, epoch)
    }

    // Given: 接続開始後のepoch更新通知がないattempt
    // When: Listening世代を開始する
    // Then: beginGenerationのepochでattempt bindingを補完する
    func testBeginGenerationBindsUnreportedAttemptEpoch() throws {
        var events: [SessionDiagnosticEvent] = []
        let bookkeeper = SessionHealthBookkeeper(
            thresholds: SessionHealthThresholds(), healthNow: { .zero },
            wallClockNow: { 0 }, reservedEpochProvider: { 1 }, diagnosticSink: { events.append($0) }
        )
        bookkeeper.beginAttempt(lifecycleGeneration: 1, reservedEpoch: 0)
        bookkeeper.beginGeneration(
            generation: 1, epoch: 1, deliveryState: EventDeliveryState(epoch: 1)
        )

        XCTAssertEqual(events.count, 2)
        guard case .attemptEpoch(let generation, let attempt, let epoch) = events[1] else {
            return XCTFail("attemptEpoch が必要")
        }
        XCTAssertEqual(generation, 1)
        XCTAssertEqual(attempt, 1)
        XCTAssertEqual(epoch, 1)
    }

    // Given: handshake前にepoch0で始まったattemptと予約済みepoch7
    // When: Listening前に停止する
    // Then: termination直前にepoch7をbindingし、古いepochをattempt開始へ付けない
    func testPreListeningTerminationBindsReservedEpoch() async throws {
        var events: [SessionDiagnosticEvent] = []
        let bookkeeper = SessionHealthBookkeeper(
            thresholds: SessionHealthThresholds(), healthNow: { .zero },
            wallClockNow: { 0 }, reservedEpochProvider: { 7 }, diagnosticSink: { events.append($0) }
        )
        bookkeeper.beginAttempt(lifecycleGeneration: 1, reservedEpoch: 0)

        await bookkeeper.recordTermination(kind: .userStopped)

        XCTAssertEqual(events.count, 3)
        guard case .attemptStarted(let startGeneration, let attempt) = events[0] else {
            return XCTFail("attemptStarted が必要")
        }
        XCTAssertEqual(startGeneration, 1)
        XCTAssertEqual(attempt, 1)
        XCTAssertFalse(events[0].description.contains("epoch="))
        guard case .attemptEpoch(let epochGeneration, let epochAttempt, let epoch) = events[1] else {
            return XCTFail("attemptEpoch が必要")
        }
        XCTAssertEqual(epochGeneration, startGeneration)
        XCTAssertEqual(epochAttempt, attempt)
        XCTAssertEqual(epoch, 7)
        guard case .termination(let termination) = events[2] else {
            return XCTFail("termination が必要")
        }
        XCTAssertEqual(termination.generation, epochGeneration)
        XCTAssertEqual(termination.epoch, epoch)
    }

    // Given: 原文・翻訳イベントと音声送信のある接続
    // When: tick と停止診断を実行する
    // Then: 5秒周期のメトリクス・停止検知・終了理由を DEBUG に依存せず出力する
    func testMetricsCadenceDetectionAndTermination() async throws {
        let clock = Clock()
        var events: [SessionDiagnosticEvent] = []
        let bookkeeper = SessionHealthBookkeeper(
            thresholds: SessionHealthThresholds(), healthNow: { clock.now },
            wallClockNow: { 0 }, reservedEpochProvider: { 1 }, diagnosticSink: { events.append($0) }
        )
        let state = EventDeliveryState(epoch: 1)
        let feed = EventFeed(events: AsyncStream { $0.finish() }, runToken: 1, deliveryState: state)
        bookkeeper.beginAttempt(lifecycleGeneration: 1, reservedEpoch: 1)
        bookkeeper.beginGeneration(generation: 1, epoch: 1, deliveryState: state)
        bookkeeper.recordCapture(hasAudioActivity: true, normalizedPeak: 0.25)
        bookkeeper.recordSendStart()
        bookkeeper.recordSendSuccess()
        bookkeeper.recordSourceProgress()
        bookkeeper.recordTranslationProgress(lane: .translation(.japanese))
        state.recordReceive(lane: .source)
        state.recordReceive(lane: .translation(.japanese))
        _ = bookkeeper.tick(feed: feed)
        let snapshots = events.compactMap { event -> SessionDiagnosticMetrics? in
            if case .snapshot(_, let metrics) = event { return metrics }
            return nil
        }
        let first = try XCTUnwrap(snapshots.first)
        XCTAssertEqual(first.capturedFrames, 1)
        XCTAssertEqual(first.sentFrames, 1)
        XCTAssertEqual(first.sourceReceivedEvents, 1)
        XCTAssertEqual(first.translationReceivedEvents, 1)
        XCTAssertEqual(first.intervalPeakPermille, 250)
        let initialCount = events.count
        clock.now = .seconds(4)
        _ = bookkeeper.tick(feed: feed)
        // capture 停止検知は即時で、周期 snapshot はまだ増えない。
        XCTAssertEqual(
            events.filter {
                if case .snapshot = $0 { return true }; return false
            }.count, 1)
        clock.now = .seconds(5)
        _ = bookkeeper.tick(feed: feed)
        if case .snapshot(_, let metrics) = events.last {
            XCTAssertEqual(metrics.intervalPeakPermille, 0)
            XCTAssertEqual(metrics.capturedFrames, 1)
        } else {
            XCTFail("周期 snapshot が必要")
        }
        XCTAssertGreaterThan(events.count, initialCount)
        XCTAssertTrue(
            events.contains {
                if case .detection(let value) = $0 { return value.kind == .captureStalled }; return false
            })
        await bookkeeper.recordTermination(kind: .userStopped)
        XCTAssertTrue(
            events.contains {
                if case .termination(let value) = $0 { return value.kind == .userStopped }; return false
            })
    }

    // Given: 前の接続で増えたフレーム数とピーク
    // When: 再接続試行を開始する
    // Then: 新接続の数値へ持ち越さず、非有限ピークも記録しない
    func testMetricsResetOnReconnect() throws {
        var events: [SessionDiagnosticEvent] = []
        let bookkeeper = SessionHealthBookkeeper(
            thresholds: SessionHealthThresholds(), healthNow: { .zero },
            wallClockNow: { 0 }, reservedEpochProvider: { 2 }, diagnosticSink: { events.append($0) }
        )
        bookkeeper.beginAttempt(lifecycleGeneration: 1, reservedEpoch: 1)
        bookkeeper.recordCapture(hasAudioActivity: true, normalizedPeak: 0.9)
        bookkeeper.recordSendSuccess()
        bookkeeper.beginAttempt(lifecycleGeneration: 1, reservedEpoch: 2)
        let state = EventDeliveryState(epoch: 2)
        bookkeeper.beginGeneration(generation: 1, epoch: 2, deliveryState: state)
        bookkeeper.recordCapture(hasAudioActivity: false, normalizedPeak: .nan)
        let feed = EventFeed(events: AsyncStream { $0.finish() }, runToken: 2, deliveryState: state)
        _ = bookkeeper.tick(feed: feed)
        guard case .snapshot(_, let metrics) = events.last else { return XCTFail("snapshot が必要") }
        XCTAssertEqual(metrics.capturedFrames, 1)
        XCTAssertEqual(metrics.sentFrames, 0)
        XCTAssertEqual(metrics.intervalPeakPermille, 0)
    }
}
