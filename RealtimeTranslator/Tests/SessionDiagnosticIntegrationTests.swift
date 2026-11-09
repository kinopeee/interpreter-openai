import XCTest
@testable import RealtimeTranslator

@MainActor
final class SessionDiagnosticIntegrationTests: XCTestCase {
    private final class Clock: @unchecked Sendable {
        var now: Duration = .zero
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
        XCTAssertEqual(events.filter { if case .snapshot = $0 { return true }; return false }.count, 1)
        clock.now = .seconds(5)
        _ = bookkeeper.tick(feed: feed)
        if case .snapshot(_, let metrics) = events.last {
            XCTAssertEqual(metrics.intervalPeakPermille, 0)
            XCTAssertEqual(metrics.capturedFrames, 1)
        } else {
            XCTFail("周期 snapshot が必要")
        }
        XCTAssertGreaterThan(events.count, initialCount)
        XCTAssertTrue(events.contains { if case .detection(let value) = $0 { return value.kind == .captureStalled }; return false })
        await bookkeeper.recordTermination(kind: .userStopped)
        XCTAssertTrue(events.contains { if case .termination(let value) = $0 { return value.kind == .userStopped }; return false })
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
