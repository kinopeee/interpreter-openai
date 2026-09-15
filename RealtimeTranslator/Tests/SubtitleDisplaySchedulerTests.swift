import XCTest
@testable import RealtimeTranslator

@MainActor
final class SubtitleDisplaySchedulerTests: XCTestCase {
    func testFinalizeRendersImmediatelyWithoutSleep() {
        // Given: 確定すべき字幕更新
        let sleeper = SleeperSpy()
        let delegate = SchedulerDelegateSpy()
        let scheduler = SubtitleDisplayScheduler(
            sleeper: { nanoseconds in await sleeper.sleep(nanoseconds) }
        )
        scheduler.delegate = delegate
        let update = makeUpdate(source: "確定", shouldFinalize: true)

        // When: 確定更新を投入する
        scheduler.enqueue(update)

        // Then: 待機せず即時に描画要求が出る
        XCTAssertEqual(delegate.rendered, [update])
        XCTAssertTrue(sleeper.calls.isEmpty)
    }

    func testInProgressUpdateCoalescesWithinRenderInterval() async {
        // Given: 直前に描画した直後で、待機を制御できる時計とスリーパ
        let clock = ManualClock()
        let sleeper = SleeperSpy()
        sleeper.waitsForRelease = true
        let delegate = SchedulerDelegateSpy()
        let scheduler = SubtitleDisplayScheduler(
            nowProvider: { clock.now },
            sleeper: { nanoseconds in await sleeper.sleep(nanoseconds) }
        )
        scheduler.delegate = delegate
        scheduler.renderNow(makeUpdate(source: "直前の描画"))
        clock.advance(by: 0.1)

        // When: 間隔内に2件の途中更新を投入する
        let first = makeUpdate(source: "途中1")
        let latest = makeUpdate(source: "途中2")
        scheduler.enqueue(first)
        scheduler.enqueue(latest)

        // Then: 残り約60msだけ待機が予約され、描画はまだ出ない
        await waitUntil { sleeper.calls.count == 1 }
        XCTAssertEqual(sleeper.calls.count, 1)
        let reservedDelay = Int64(bitPattern: sleeper.calls[0])
        XCTAssertGreaterThanOrEqual(reservedDelay, 59_000_000)
        XCTAssertLessThanOrEqual(reservedDelay, 61_000_000)
        XCTAssertEqual(delegate.rendered.map(\.sourceText), ["直前の描画"])

        // When: 予約された待機を解放する
        sleeper.releaseAll()
        await waitUntil { delegate.rendered.count == 2 }

        // Then: 最新の保留だけが1回描画される
        XCTAssertEqual(delegate.rendered.last, latest)
    }

    func testEnqueueAfterIntervalElapsesRendersPromptly() async {
        // Given: 前回描画から間隔を超えて経過した時計
        let clock = ManualClock()
        let sleeper = SleeperSpy()
        let delegate = SchedulerDelegateSpy()
        let scheduler = SubtitleDisplayScheduler(
            nowProvider: { clock.now },
            sleeper: { nanoseconds in await sleeper.sleep(nanoseconds) }
        )
        scheduler.delegate = delegate
        scheduler.renderNow(makeUpdate(source: "直前の描画"))
        clock.advance(by: 1.0)

        // When: 途中更新を投入する
        let update = makeUpdate(source: "間隔経過後")
        scheduler.enqueue(update)
        await waitUntil { delegate.rendered.count == 2 }

        // Then: 待機なしで次の描画へ進む
        XCTAssertEqual(delegate.rendered.last, update)
        XCTAssertTrue(sleeper.calls.isEmpty)
    }

    func testTakePendingUpdateReturnsLatestAndSuppressesScheduledRender() async {
        // Given: 間引き待ちの途中更新がある
        let clock = ManualClock()
        let sleeper = SleeperSpy()
        sleeper.waitsForRelease = true
        let delegate = SchedulerDelegateSpy()
        let scheduler = SubtitleDisplayScheduler(
            nowProvider: { clock.now },
            sleeper: { nanoseconds in await sleeper.sleep(nanoseconds) }
        )
        scheduler.delegate = delegate
        scheduler.renderNow(makeUpdate(source: "直前の描画"))
        let pending = makeUpdate(source: "保留中")
        scheduler.enqueue(pending)

        // When: 停止処理が保留を取り出す
        await waitUntil { sleeper.calls.count == 1 }
        let taken = scheduler.takePendingUpdate()
        sleeper.releaseAll()
        try? await Task.sleep(nanoseconds: 50_000_000)

        // Then: 保留が返り、予約済みの遅延描画は走らない
        XCTAssertEqual(taken, pending)
        XCTAssertEqual(delegate.rendered.map(\.sourceText), ["直前の描画"])
    }

    func testDiscardPendingDropsUpdateWithoutRender() async {
        // Given: 間引き待ちの途中更新がある
        let clock = ManualClock()
        let sleeper = SleeperSpy()
        sleeper.waitsForRelease = true
        let delegate = SchedulerDelegateSpy()
        let scheduler = SubtitleDisplayScheduler(
            nowProvider: { clock.now },
            sleeper: { nanoseconds in await sleeper.sleep(nanoseconds) }
        )
        scheduler.delegate = delegate
        scheduler.renderNow(makeUpdate(source: "直前の描画"))
        scheduler.enqueue(makeUpdate(source: "捨てる更新"))

        // When: 受信欠落などで保留を破棄する
        await waitUntil { sleeper.calls.count == 1 }
        scheduler.discardPending()
        sleeper.releaseAll()
        try? await Task.sleep(nanoseconds: 50_000_000)

        // Then: 破棄した更新は描画されない
        XCTAssertEqual(delegate.rendered.map(\.sourceText), ["直前の描画"])
    }

    func testFirstInProgressUpdateFromDistantPastRendersWithoutOverflow() async {
        // Given: 未描画（lastRenderedAt が distantPast）のスケジューラ
        let sleeper = SleeperSpy()
        let delegate = SchedulerDelegateSpy()
        let scheduler = SubtitleDisplayScheduler(
            sleeper: { nanoseconds in await sleeper.sleep(nanoseconds) }
        )
        scheduler.delegate = delegate
        let update = makeUpdate(source: "初回の途中")

        // When: 初回の途中更新を投入する
        scheduler.enqueue(update)
        await waitUntil { delegate.rendered.count == 1 }

        // Then: 経過ナノ秒の UInt64 化で trap せず、待機なしで描画される
        XCTAssertEqual(delegate.rendered, [update])
        XCTAssertTrue(sleeper.calls.isEmpty)
    }

    func testInvalidationRenderDoesNotRecordRenderTime() async {
        // Given: 無効化更新を描画した直後
        let sleeper = SleeperSpy()
        sleeper.waitsForRelease = true
        let delegate = SchedulerDelegateSpy()
        let scheduler = SubtitleDisplayScheduler(
            sleeper: { nanoseconds in await sleeper.sleep(nanoseconds) }
        )
        scheduler.delegate = delegate
        scheduler.renderNow(makeUpdate(source: "", isInvalidation: true))

        // When: 直後に途中更新を投入する
        scheduler.enqueue(makeUpdate(source: "次の発話"))

        // Then: 無効化は描画時刻として扱わず、間引き待機も予約されない
        XCTAssertTrue(sleeper.calls.isEmpty)
        sleeper.releaseAll()
        await waitUntil { delegate.rendered.count == 2 }
    }

    func testPostStopClearRunsWhenShouldClearHolds() async {
        // Given: 停止後消去の予約と制御できる待機
        let sleeper = SleeperSpy()
        sleeper.waitsForRelease = true
        let delegate = SchedulerDelegateSpy()
        let scheduler = SubtitleDisplayScheduler(
            sleeper: { nanoseconds in await sleeper.sleep(nanoseconds) }
        )
        scheduler.delegate = delegate
        var cleared = false

        // When: 5秒の消去予約を立てて解放する
        scheduler.schedulePostStopClear(
            afterNanoseconds: 5_000_000_000,
            shouldClear: { true },
            onClear: { cleared = true }
        )
        await waitUntil { sleeper.calls.count == 1 }
        XCTAssertEqual(sleeper.calls, [5_000_000_000])
        sleeper.releaseAll()
        await waitUntil { cleared }

        // Then: 条件を満たすので消去が実行される
        XCTAssertTrue(cleared)
    }

    func testPostStopClearSkipsWhenShouldClearFails() async {
        // Given: 消去条件が成立しない予約
        let sleeper = SleeperSpy()
        sleeper.waitsForRelease = true
        let delegate = SchedulerDelegateSpy()
        let scheduler = SubtitleDisplayScheduler(
            sleeper: { nanoseconds in await sleeper.sleep(nanoseconds) }
        )
        scheduler.delegate = delegate
        var cleared = false

        // When: 予約を解放する
        scheduler.schedulePostStopClear(
            afterNanoseconds: 1_000,
            shouldClear: { false },
            onClear: { cleared = true }
        )
        await waitUntil { sleeper.calls.count == 1 }
        sleeper.releaseAll()
        try? await Task.sleep(nanoseconds: 50_000_000)

        // Then: 消去は実行されない
        XCTAssertFalse(cleared)
    }

    func testCancelPostStopClearPreventsRun() async {
        // Given: 消去予約を立ててから取消す
        let sleeper = SleeperSpy()
        sleeper.waitsForRelease = true
        let delegate = SchedulerDelegateSpy()
        let scheduler = SubtitleDisplayScheduler(
            sleeper: { nanoseconds in await sleeper.sleep(nanoseconds) }
        )
        scheduler.delegate = delegate
        var cleared = false
        scheduler.schedulePostStopClear(
            afterNanoseconds: 1_000,
            shouldClear: { true },
            onClear: { cleared = true }
        )

        // When: 待機解放前に取消す
        scheduler.cancelPostStopClear()
        sleeper.releaseAll()
        try? await Task.sleep(nanoseconds: 50_000_000)

        // Then: 消去は実行されない
        XCTAssertFalse(cleared)
    }

    private func makeUpdate(
        source: String,
        shouldFinalize: Bool = false,
        isInvalidation: Bool = false
    ) -> RealtimeSubtitleUpdate {
        RealtimeSubtitleUpdate(
            sourceText: source,
            translatedText: "",
            isTranslationCurrent: true,
            shouldFinalize: shouldFinalize,
            segmentGeneration: 0,
            isInvalidation: isInvalidation
        )
    }
}

@MainActor
private final class SchedulerDelegateSpy: SubtitleDisplaySchedulerDelegate {
    private(set) var rendered: [RealtimeSubtitleUpdate] = []

    func subtitleDisplayScheduler(
        _ scheduler: SubtitleDisplayScheduler,
        requestsRenderOf update: RealtimeSubtitleUpdate
    ) {
        rendered.append(update)
    }
}

@MainActor
private final class SleeperSpy {
    private(set) var calls: [UInt64] = []
    private var gates: [CheckedContinuation<Void, Never>] = []
    var waitsForRelease = false

    func sleep(_ nanoseconds: UInt64) async {
        calls.append(nanoseconds)
        guard waitsForRelease else { return }
        await withCheckedContinuation { continuation in
            gates.append(continuation)
        }
    }

    func releaseAll() {
        let pending = gates
        gates.removeAll()
        for gate in pending {
            gate.resume()
        }
    }
}

@MainActor
private final class ManualClock {
    private(set) var now = Date(timeIntervalSince1970: 1_700_000_000)

    func advance(by seconds: TimeInterval) {
        now = now.addingTimeInterval(seconds)
    }
}
