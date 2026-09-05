import Foundation
import os
import XCTest
@testable import RealtimeTranslator

final class EventDeliveryStateTests: XCTestCase {
    // Given: macOS の受信キュー fixture と実装上の容量
    // When: 容量を契約と比較する
    // Then: 接続・merge・保持・stop-drain の上限が一致する
    func testReceiveQueueFixtureMatchesMacOSLimits() throws {
        let fixture = try SharedFixtures.load("receive-queue")
        let capacities = try XCTUnwrap(fixture["capacities"] as? [String: Any])
        let macOS = try XCTUnwrap(capacities["macos"] as? [String: Any])

        XCTAssertEqual(
            SharedFixtures.number(macOS["connection"]),
            RealtimeTranslationConnection.eventBufferLimit
        )
        XCTAssertEqual(
            SharedFixtures.number(macOS["connection"]),
            RealtimeSourceTranscriptionConnection.eventBufferLimit
        )
        XCTAssertEqual(
            SharedFixtures.number(macOS["merge"]),
            DualRealtimeTranslationClient.mergedEventBufferLimit
        )
        XCTAssertEqual(
            SharedFixtures.number(macOS["unacknowledgedRetention"]),
            DualRealtimeTranslationClient.unacknowledgedRetentionLimit
        )
        XCTAssertEqual(
            SharedFixtures.number(macOS["stopDrainRetention"]),
            DualRealtimeTranslationClient.stopDrainRetentionLimit
        )

        let overflow = try XCTUnwrap(fixture["overflow"] as? [String: Any])
        XCTAssertEqual(
            RealtimeTranslationError.receiveOverflow.errorDescription,
            UiCopy.text(SharedFixtures.text(overflow["errorMessageKey"]))
        )
    }

    // Given: 上限ちょうどの oldest-buffered stream
    // When: 追加イベントを 1 件配送する
    // Then: loss が 1 回だけ記録され stream が終了する
    func testYielderRecordsOverflowAtConnectionBoundary() async {
        var continuation: AsyncStream<RealtimeTranslationStreamEvent>.Continuation!
        let stream = AsyncStream<RealtimeTranslationStreamEvent>(
            bufferingPolicy: .bufferingOldest(RealtimeTranslationConnection.eventBufferLimit)
        ) {
            continuation = $0
        }
        let state = EventDeliveryState(epoch: 1)
        let yielder = EventDeliveryYielder(
            continuation: continuation,
            deliveryState: state,
            stage: .translation(.english),
            capacity: RealtimeTranslationConnection.eventBufferLimit
        )
        let event = RealtimeTranslationStreamEvent(
            lane: .translation(.english),
            event: .outputTranscriptDelta(delta: "x", eventID: nil, elapsedMs: nil),
            epoch: 1
        )

        for _ in 0..<RealtimeTranslationConnection.eventBufferLimit {
            XCTAssertTrue(yielder.deliver(event))
        }
        XCTAssertFalse(yielder.deliver(event))
        XCTAssertTrue(state.didLoseEvents)
        XCTAssertEqual(state.lossStage, .some(.translation(.english)))
        XCTAssertEqual(
            state.lossCapacity,
            .some(RealtimeTranslationConnection.eventBufferLimit)
        )
        XCTAssertEqual(state.termination, .receiveOverflow)

        var count = 0
        for await _ in stream {
            count += 1
        }
        XCTAssertEqual(count, RealtimeTranslationConnection.eventBufferLimit)
    }

    // Given: fixture で定義された connection と merge の容量
    // When: 各容量ちょうどと 1 件超過を配送する
    // Then: ちょうどなら loss なし、超過なら overflow になる
    func testYielderHonorsFixtureConnectionAndMergeBoundaries() throws {
        let fixture = try SharedFixtures.load("receive-queue")
        let capacities = try XCTUnwrap(fixture["capacities"] as? [String: Any])
        let macOS = try XCTUnwrap(capacities["macos"] as? [String: Any])
        let connectionCapacity = SharedFixtures.number(macOS["connection"])
        let mergeCapacity = SharedFixtures.number(macOS["merge"])

        func event(epoch: Int) -> RealtimeTranslationStreamEvent {
            RealtimeTranslationStreamEvent(
                lane: .translation(.english),
                event: .outputTranscriptDelta(
                    delta: "x",
                    eventID: nil,
                    elapsedMs: nil
                ),
                epoch: epoch
            )
        }

        var connectionContinuation: AsyncStream<RealtimeTranslationStreamEvent>.Continuation!
        let connectionStream = AsyncStream<RealtimeTranslationStreamEvent>(
            bufferingPolicy: .bufferingOldest(connectionCapacity)
        ) {
            connectionContinuation = $0
        }
        let connectionState = EventDeliveryState(epoch: 1)
        let connectionYielder = EventDeliveryYielder(
            continuation: connectionContinuation,
            deliveryState: connectionState,
            stage: .translation(.english),
            capacity: connectionCapacity
        )
        // stream を捨てると continuation が .terminated になり、容量前に deliver が false になる。
        withExtendedLifetime(connectionStream) {
            for _ in 0..<connectionCapacity {
                XCTAssertTrue(connectionYielder.deliver(event(epoch: 1)))
            }
            XCTAssertFalse(connectionYielder.deliver(event(epoch: 1)))
            XCTAssertTrue(connectionState.didLoseEvents)
            XCTAssertEqual(connectionState.lossStage, .some(.translation(.english)))
            XCTAssertEqual(connectionState.lossCapacity, .some(connectionCapacity))
            XCTAssertEqual(connectionState.termination, .receiveOverflow)
        }

        var mergeContinuation: AsyncStream<RealtimeTranslationStreamEvent>.Continuation!
        let mergeStream = AsyncStream<RealtimeTranslationStreamEvent>(
            bufferingPolicy: .bufferingOldest(mergeCapacity)
        ) {
            mergeContinuation = $0
        }
        let mergeState = EventDeliveryState(epoch: 2)
        let mergeYielder = EventDeliveryYielder(
            continuation: mergeContinuation,
            deliveryState: mergeState,
            stage: .merge,
            capacity: mergeCapacity
        )
        withExtendedLifetime(mergeStream) {
            for _ in 0..<mergeCapacity {
                XCTAssertTrue(mergeYielder.deliver(event(epoch: 2)))
            }
            XCTAssertFalse(mergeYielder.deliver(event(epoch: 2)))
            XCTAssertTrue(mergeState.didLoseEvents)
            XCTAssertEqual(mergeState.lossStage, .some(.merge))
            XCTAssertEqual(mergeState.lossCapacity, .some(mergeCapacity))
            XCTAssertEqual(mergeState.termination, .receiveOverflow)
        }
    }

    // Given: waitForCompletion 中の Task を cancel する
    // When: その後に recordLoss する
    // Then: cancel だけでは completed にならず、loss で waiter が起きる
    func testWaitForCompletionCancelDoesNotCompleteDelivery() async {
        let state = EventDeliveryState(epoch: 1)
        let cancelledWait = Task {
            await state.waitForCompletion()
        }
        cancelledWait.cancel()
        await cancelledWait.value

        XCTAssertFalse(state.didLoseEvents)
        XCTAssertEqual(state.termination, .none)

        let sawCompletion = OSAllocatedUnfairLock(initialState: false)
        let pendingWait = Task {
            await state.waitForCompletion()
            sawCompletion.withLock { $0 = true }
        }
        await Task.yield()
        XCTAssertFalse(sawCompletion.withLock { $0 })

        state.recordLoss(stage: .merge, capacity: 512)
        await pendingWait.value
        XCTAssertTrue(sawCompletion.withLock { $0 })
        XCTAssertTrue(state.didLoseEvents)
        XCTAssertEqual(state.termination, .receiveOverflow)
    }

    // Given: 共通の優先順位を持つ termination 原因
    // When: overflow の後に各原因を記録する
    // Then: より高い原因だけが overflow を置き換える
    func testTerminationPrecedenceAndSanitization() {
        let state = EventDeliveryState(epoch: 1)
        state.recordLoss(stage: .merge, capacity: 513)
        XCTAssertTrue(state.tryRecordTermination(.fatalServerError("safe")))
        XCTAssertTrue(state.tryRecordTermination(.authenticationFailed))
        XCTAssertFalse(state.tryRecordTermination(.transportFailure))
        XCTAssertEqual(state.termination, .authenticationFailed)
        XCTAssertEqual(state.makeError(), .authenticationFailed)

        let fatal = EventDeliveryState.classify(
            code: "server_error",
            message: "bearer sk-secret must not escape"
        )
        XCTAssertEqual(fatal, .fatalServerError(RealtimeTranslationError.genericServerMessage))
    }
}
