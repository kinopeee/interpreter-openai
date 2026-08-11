import XCTest
@testable import RealtimeTranslator

final class PCM16FramePacketizerTests: XCTestCase {
    func testExactFrameBoundary() {
        // Given: ちょうど100 ms分のPCM16
        var packetizer = PCM16FramePacketizer()
        let frame = Data(count: PCM16FramePacketizer.bytesPerFrame)

        // When: appendする
        let frames = packetizer.append(frame)

        // Then: 1 frameが返り、端数は残らない
        XCTAssertEqual(frames.count, 1)
        XCTAssertEqual(frames[0].count, PCM16FramePacketizer.bytesPerFrame)
        XCTAssertNil(packetizer.flushWithSilencePadding())
    }

    func testSplitAndCombineAcrossBuffers() {
        // Given: 半端な2バッファ
        var packetizer = PCM16FramePacketizer()
        let first = Data(count: 1000)
        let second = Data(count: PCM16FramePacketizer.bytesPerFrame - 1000 + 200)

        // When: 連続appendする
        XCTAssertTrue(packetizer.append(first).isEmpty)
        let frames = packetizer.append(second)

        // Then: 1 frameが完成し、200 byteが残る
        XCTAssertEqual(frames.count, 1)
        let padded = packetizer.flushWithSilencePadding()
        XCTAssertEqual(padded?.count, PCM16FramePacketizer.bytesPerFrame)
    }

    func testFlushPadsRemainderWithSilence() throws {
        // Given: 端数だけの入力
        var packetizer = PCM16FramePacketizer()
        _ = packetizer.append(Data([0x01, 0x02, 0x03, 0x04]))

        // When: flushする
        let frame = packetizer.flushWithSilencePadding()

        // Then: 100 ms分になり先頭4 byteを保持、残りは0
        let unwrapped = try XCTUnwrap(frame)
        XCTAssertEqual(unwrapped.count, PCM16FramePacketizer.bytesPerFrame)
        XCTAssertEqual(Array(unwrapped.prefix(4)), [0x01, 0x02, 0x03, 0x04])
        XCTAssertTrue(unwrapped.dropFirst(4).allSatisfy { $0 == 0 })
    }

    func testLittleEndianEncoderClipsFloatSamples() {
        // Given: 範囲外のfloatサンプル
        let samples: [Float] = [-2.0, -1.0, 0.0, 1.0, 2.0]

        // When: encodeする
        let data = samples.withUnsafeBufferPointer { buffer in
            PCM16LittleEndianEncoder.encode(
                floatSamples: buffer.baseAddress!,
                frameCount: buffer.count
            )
        }

        // Then: Int16範囲へclipされlittle-endian
        let values = data.withUnsafeBytes { raw -> [Int16] in
            Array(raw.bindMemory(to: Int16.self))
        }
        // -1.0 * Int16.max は -32767。Int16.min(-32768) には丸めない。
        XCTAssertEqual(values, [-32767, -32767, 0, Int16.max, Int16.max])
    }

    func testLittleEndianEncoderAppliesGainAndClips() {
        // Given: 小さい入力と4倍でclipする入力
        let samples: [Float] = [0.1, -0.1, 0.5, -0.5]

        // When: 4倍ゲインでencodeする
        let data = samples.withUnsafeBufferPointer { buffer in
            PCM16LittleEndianEncoder.encode(
                floatSamples: buffer.baseAddress!,
                frameCount: buffer.count,
                gain: 4
            )
        }

        // Then: 小さい入力は4倍、範囲外はPCM16上限へclipされる
        let values = data.withUnsafeBytes { raw -> [Int16] in
            Array(raw.bindMemory(to: Int16.self))
        }
        XCTAssertEqual(values, [13107, -13107, Int16.max, -32767])
    }

    func testLittleEndianEncoderMapsNaNToSilenceAndClipsInfinity() {
        // Given: AGC と同じく NaN / ±Infinity が混在する float バッファ
        let samples: [Float] = [.nan, 0.5, .infinity, -.infinity]

        // When: encode する（旧実装は Int16(Float.nan) で trap する）
        let data = samples.withUnsafeBufferPointer { buffer in
            PCM16LittleEndianEncoder.encode(
                floatSamples: buffer.baseAddress!,
                frameCount: buffer.count,
                gain: 1
            )
        }

        // Then: NaN は無音、有限と ±Infinity はクリップ済み PCM16
        let values = data.withUnsafeBytes { raw -> [Int16] in
            Array(raw.bindMemory(to: Int16.self))
        }
        XCTAssertEqual(values, [0, 16384, Int16.max, -32767])

        // Given/When: Infinity * 0 は IEEE で NaN になる
        let infinityTimesZero: [Float] = [.infinity]
        let silenced = infinityTimesZero.withUnsafeBufferPointer { buffer in
            PCM16LittleEndianEncoder.encode(
                floatSamples: buffer.baseAddress!,
                frameCount: buffer.count,
                gain: 0
            )
        }
        // Then: trap せず無音
        let silencedValues = silenced.withUnsafeBytes { raw -> [Int16] in
            Array(raw.bindMemory(to: Int16.self))
        }
        XCTAssertEqual(silencedValues, [0])
    }

    func testLittleEndianEncoderIgnoresNonFiniteGain() {
        // Given: 有限サンプルと非有限ゲイン
        let samples: [Float] = [0.5]

        // When: NaN ゲインで encode する
        let data = samples.withUnsafeBufferPointer { buffer in
            PCM16LittleEndianEncoder.encode(
                floatSamples: buffer.baseAddress!,
                frameCount: buffer.count,
                gain: .nan
            )
        }

        // Then: ゲイン 1.0 相当として符号化される
        let values = data.withUnsafeBytes { raw -> [Int16] in
            Array(raw.bindMemory(to: Int16.self))
        }
        XCTAssertEqual(values, [16384])
    }

    func testSilenceFrameHasExpectedSize() {
        // Given: 無音PCM
        var packetizer = PCM16FramePacketizer()
        let silence = Data(count: PCM16FramePacketizer.bytesPerFrame)

        // When: packet化する
        let frames = packetizer.append(silence)

        // Then: 全ゼロの1 frame
        XCTAssertEqual(frames.count, 1)
        XCTAssertTrue(frames[0].allSatisfy { $0 == 0 })
        XCTAssertEqual(PCM16FramePacketizer.frameDurationMilliseconds, 100)
    }
}
