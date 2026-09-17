import XCTest
@testable import RealtimeTranslator

final class RealtimeAudioFrameQueueTests: XCTestCase {
    func testBufferingNewestKeepsNewestFramesAndRecordsDrops() async {
        // Given: 容量32の送信キューへ読み取り前に40フレームを投入する
        let queue = RealtimeAudioFrameQueue()
        for _ in 0..<40 {
            XCTAssertTrue(
                queue.enqueue(
                    pcm16: Data(count: 4_800),
                    generation: 1,
                    discardedMilliseconds: 0,
                    capturedAt: .now
                )
            )
        }
        queue.finish()

        // When: 終了後にキューを読み取る
        var received: [CapturedAudioFrame] = []
        for await frame in queue.frames {
            received.append(frame)
        }

        // Then: newest32件を保持し、置換dropを8件記録する
        XCTAssertEqual(received.map(\.sequence), Array(8...39))
        XCTAssertEqual(queue.droppedFrameCount, 8)
    }
}
