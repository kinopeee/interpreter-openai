@preconcurrency import AVFoundation
import XCTest
@testable import RealtimeTranslator

final class RealtimeAudioFrameYieldOutcomeTests: XCTestCase {
    func testBufferingNewestDropOfOldestIsAccepted() async {
        // Given: capacity 1のbufferingNewest（満杯でoldestがdropされる）
        let (stream, continuation) = AsyncStream<Data>.makeStream(
            bufferingPolicy: .bufferingNewest(1)
        )
        var iterator = stream.makeAsyncIterator()
        XCTAssertTrue(
            RealtimeAudioFrameYieldOutcome.didAccept(continuation.yield(Data([1])))
        )

        // When: 2件目をyieldしてoldest dropが返る
        let secondResult = continuation.yield(Data([2]))
        let accepted = RealtimeAudioFrameYieldOutcome.didAccept(secondResult)

        // Then: 新規frameは受理済みなので継続扱い（pipelineOverloadedにしない）
        guard case .dropped(let dropped) = secondResult else {
            XCTFail("Expected dropped oldest for bufferingNewest")
            return
        }
        XCTAssertEqual(dropped, Data([1]))
        XCTAssertTrue(accepted)
        let kept = await iterator.next()
        XCTAssertEqual(kept, Data([2]))
        continuation.finish()
    }

    func testTerminatedYieldIsRejected() {
        // Given: 終了済みcontinuation
        let (stream, continuation) = AsyncStream<Data>.makeStream(
            bufferingPolicy: .bufferingNewest(1)
        )
        _ = stream
        continuation.finish()

        // When: finish後にyieldする
        let accepted = RealtimeAudioFrameYieldOutcome.didAccept(
            continuation.yield(Data([9]))
        )

        // Then: 終了は失敗として扱う
        XCTAssertFalse(accepted)
    }
}

@MainActor
final class AudioBufferOwnershipTests: XCTestCase {
    func testTapYieldsDeepOwnedBufferCopy() async throws {
        // Given: 2チャンネルの非インターリーブ音声とtapの非同期ストリーム
        let format = try XCTUnwrap(
            AVAudioFormat(
                commonFormat: .pcmFormatFloat32,
                sampleRate: 48_000,
                channels: 2,
                interleaved: false
            )
        )
        let source = try XCTUnwrap(
            AVAudioPCMBuffer(pcmFormat: format, frameCapacity: 4)
        )
        source.frameLength = 4
        let sourceChannels = try XCTUnwrap(source.floatChannelData)
        sourceChannels[0][0] = 1.25
        sourceChannels[1][3] = 2.5
        let (stream, continuation) = AsyncStream<CapturedAudioBuffer>.makeStream()
        let bufferPool = try XCTUnwrap(
            CapturedAudioBufferPool(
                format: format,
                frameCapacity: 4,
                capacity: 1
            )
        )
        let tap = AnalyzerAudioTap(
            continuation: continuation,
            bufferPool: bufferPool
        )

        // When: tapへ渡した直後に音声エンジン所有の元バッファを書き換える
        tap.receive(source)
        sourceChannels[0][0] = 9
        sourceChannels[1][3] = 10
        var iterator = stream.makeAsyncIterator()
        let next = await iterator.next()
        let captured = try XCTUnwrap(next)

        // Then: 非同期側は別オブジェクトで変更前の全チャンネル値を保持する
        let capturedChannels = try XCTUnwrap(captured.buffer.floatChannelData)
        XCTAssertFalse(captured.buffer === source)
        XCTAssertEqual(captured.buffer.frameLength, 4)
        XCTAssertEqual(capturedChannels[0][0], 1.25)
        XCTAssertEqual(capturedChannels[1][3], 2.5)
        XCTAssertEqual(bufferPool.availableCount, 0)

        // When: consumerが返却したslotへ次の音声を取り込む
        captured.release()
        sourceChannels[0][0] = 3.75
        tap.receive(source)
        let reusedValue = await iterator.next()
        let reused = try XCTUnwrap(reusedValue)
        continuation.finish()

        // Then: callbackごとに確保せず、同じ事前確保slotを再利用する
        XCTAssertTrue(reused === captured)
        XCTAssertEqual(reused.buffer.floatChannelData?[0][0], 3.75)
        reused.release()
        XCTAssertEqual(bufferPool.availableCount, 1)
    }

    func testPoolKeepsSpareSlotForBufferingNewestReplacement() async throws {
        // Given: 1件stream・1件consumer処理中・1件予備の3slotを持つtap
        let format = try XCTUnwrap(
            AVAudioFormat(
                commonFormat: .pcmFormatFloat32,
                sampleRate: 48_000,
                channels: 1,
                interleaved: false
            )
        )
        let source = try XCTUnwrap(
            AVAudioPCMBuffer(pcmFormat: format, frameCapacity: 1)
        )
        source.frameLength = 1
        let sourceData = try XCTUnwrap(source.floatChannelData)
        let (stream, continuation) = AsyncStream<CapturedAudioBuffer>.makeStream(
            bufferingPolicy: .bufferingNewest(1)
        )
        let pool = try XCTUnwrap(
            CapturedAudioBufferPool(
                format: format,
                frameCapacity: 1,
                capacity: 3
            )
        )
        let tap = AnalyzerAudioTap(continuation: continuation, bufferPool: pool)

        // When: 1件を処理中に、streamへ新旧2bufferを連続投入する
        sourceData[0][0] = 1
        tap.receive(source)
        var iterator = stream.makeAsyncIterator()
        let processingValue = await iterator.next()
        let processing = try XCTUnwrap(processingValue)
        sourceData[0][0] = 2
        tap.receive(source)
        sourceData[0][0] = 3
        tap.receive(source)
        let newestValue = await iterator.next()
        let newest = try XCTUnwrap(newestValue)
        continuation.finish()

        // Then: 待機中の最古slotを返却し、処理中でも最新音声を保持する
        XCTAssertEqual(processing.buffer.floatChannelData?[0][0], 1)
        XCTAssertEqual(newest.buffer.floatChannelData?[0][0], 3)
        XCTAssertEqual(pool.availableCount, 1)
        processing.release()
        newest.release()
        XCTAssertEqual(pool.availableCount, 3)
    }
}
