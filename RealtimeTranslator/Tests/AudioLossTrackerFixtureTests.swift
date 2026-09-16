import XCTest
@testable import RealtimeTranslator

final class AudioLossTrackerFixtureTests: XCTestCase {
    func testLossCasesMatchSharedContract() throws {
        // Given: 独立した期待値を定義する共有音声欠落fixture
        let fixture = try SharedFixtures.load("audio")
        let loss = try XCTUnwrap(fixture["loss"] as? [String: Any])
        let reconnect = try XCTUnwrap(loss["reconnect"] as? [String: Any])
        let cases = try XCTUnwrap(loss["cases"] as? [[String: Any]])

        // When: 各観測列をtrackerへ再生する
        var policy = AudioLossPolicy.default
        let frameDuration = SharedFixtures.number(loss["frameDurationMs"])
        let threshold = SharedFixtures.number(reconnect["lostMsThreshold"])
        let window = SharedFixtures.number(reconnect["windowMs"])
        policy.frameDurationMilliseconds = frameDuration
        policy.reconnectLostMillisecondsThreshold = threshold
        policy.reconnectWindowMilliseconds = window
        XCTAssertEqual(policy, .default)
        XCTAssertEqual(RealtimeAudioFrameQueue.capacity, SharedFixtures.number(loss["sendQueueFrameCapacity"]))
        XCTAssertEqual(
            RealtimeSubtitleAssembler.audioLossTaintWindow * 1_000,
            Double(SharedFixtures.number(loss["taintedSegmentWindowMs"]))
        )

        for item in cases {
            let frames = try XCTUnwrap(item["frames"] as? [[String: Any]])
            let expected = try XCTUnwrap(item["expected"] as? [String: Any])
            let name = item["name"] as? String ?? "loss case"
            var tracker = AudioLossTracker(policy: policy)
            var reconnectAt: Int?
            for frame in frames {
                let observation = tracker.observe(
                    generation: SharedFixtures.number(frame["generation"]),
                    sequence: SharedFixtures.number(frame["sequence"]),
                    discardedMilliseconds: SharedFixtures.number(frame["discardedMs"]),
                    queueWaitMilliseconds: SharedFixtures.number(frame["queueWaitMs"]),
                    atMilliseconds: SharedFixtures.number(frame["atMs"])
                )
                if observation.shouldReconnect {
                    reconnectAt = SharedFixtures.number(frame["atMs"])
                }
            }

            // Then: metricsと再接続観測がfixture契約と一致する
            XCTAssertEqual(tracker.metrics.droppedFrames, SharedFixtures.number(expected["droppedFrames"]), name)
            XCTAssertEqual(tracker.metrics.lostMilliseconds, SharedFixtures.number(expected["lostMs"]), name)
            XCTAssertEqual(tracker.metrics.lossEvents, SharedFixtures.number(expected["lossEvents"]), name)
            XCTAssertEqual(tracker.metrics.maxQueueWaitMilliseconds, SharedFixtures.number(expected["maxQueueWaitMs"]), name)
            XCTAssertEqual(reconnectAt, SharedFixtures.optionalNumber(expected["reconnectAt"]), name)
        }
    }
}
