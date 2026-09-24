import XCTest
@testable import RealtimeTranslator

final class Float32FrameAccumulatorTests: XCTestCase {
    func testExactlyOneFrameIsEmitted() {
        // Given: 2,400 samples ちょうどの入力
        var accumulator = Float32FrameAccumulator()
        let input = ramp(Float32FrameAccumulator.samplesPerFrame)

        // When: 追加する
        let frames = input.withUnsafeBufferPointer { accumulator.append($0) }

        // Then: 100 ms frame が1つ出て端数は残らない
        XCTAssertEqual(frames.count, 1)
        XCTAssertEqual(frames.first, input)
        XCTAssertEqual(accumulator.pendingSampleCount, 0)
    }

    func testChunkBoundariesDoNotChangeFrames() throws {
        // Given: 長さの違うチャンクに分けた2.5 frame分の入力
        let input = ramp(Float32FrameAccumulator.samplesPerFrame * 5 / 2)
        var whole = Float32FrameAccumulator()
        var chunked = Float32FrameAccumulator()

        // When: 一括と分割で追加し、分割側を flush する
        let expected = input.withUnsafeBufferPointer { whole.append($0) }
        var actual: [[Float]] = []
        for range in [0..<1000, 1000..<3700, 3700..<input.count] {
            let chunk = Array(input[range])
            actual += chunk.withUnsafeBufferPointer { chunked.append($0) }
        }
        let padded = try XCTUnwrap(chunked.flushWithSilencePadding())

        // Then: 同じ frame 列になり、端数は無音 padding される
        XCTAssertEqual(expected.count, 2)
        XCTAssertEqual(actual, expected)
        XCTAssertEqual(padded.count, Float32FrameAccumulator.samplesPerFrame)
        XCTAssertEqual(Array(padded.prefix(1200)), Array(input.suffix(1200)))
        XCTAssertTrue(padded.dropFirst(1200).allSatisfy { $0 == 0 })
        XCTAssertEqual(chunked.pendingSampleCount, 0)
    }

    func testFlushWithoutRemainderOrAfterResetReturnsNil() {
        // Given: 端数が無い accumulator
        var accumulator = Float32FrameAccumulator()
        XCTAssertNil(accumulator.flushWithSilencePadding())

        // When: 端数を追加してから reset する
        let partial = ramp(100)
        _ = partial.withUnsafeBufferPointer { accumulator.append($0) }
        XCTAssertEqual(accumulator.pendingSampleCount, 100)
        accumulator.reset()

        // Then: flush しても frame を返さない
        XCTAssertEqual(accumulator.pendingSampleCount, 0)
        XCTAssertNil(accumulator.flushWithSilencePadding())
    }

    private func ramp(_ count: Int) -> [Float] {
        (0..<count).map { Float($0) / 10_000 }
    }
}
