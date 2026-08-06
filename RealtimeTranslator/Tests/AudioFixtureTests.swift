import XCTest
@testable import RealtimeTranslator

final class AudioFixtureTests: XCTestCase {
    // Given: shared fixture の音声フォーマット定義
    // When: packetizer の定数と照合する
    // Then: 24 kHz / 100 ms / 2,400 sample / 4,800 byte が一致する
    func testFormatMatchesFixture() throws {
        let format = try XCTUnwrap(
            try SharedFixtures.load("audio")["format"] as? [String: Any]
        )
        XCTAssertEqual(SharedFixtures.number(format["sampleRate"]), PCM16FramePacketizer.sampleRate)
        XCTAssertEqual(
            SharedFixtures.number(format["bytesPerSample"]),
            PCM16FramePacketizer.bytesPerSample
        )
        XCTAssertEqual(
            SharedFixtures.number(format["frameDurationMilliseconds"]),
            PCM16FramePacketizer.frameDurationMilliseconds
        )
        XCTAssertEqual(
            SharedFixtures.number(format["samplesPerFrame"]),
            PCM16FramePacketizer.samplesPerFrame
        )
        XCTAssertEqual(
            SharedFixtures.number(format["bytesPerFrame"]),
            PCM16FramePacketizer.bytesPerFrame
        )
    }

    // Given: fixture の PCM16 入力バイト列
    // When: packetizer へ流し込む
    // Then: 期待するフレーム分割と残バイトになる
    func testPacketizerMatchesFixture() throws {
        for name in try SharedFixtures.caseNames("audio", "packetizer") {
                        let fixture = try SharedFixtures.case("audio", "packetizer", name)
            var packetizer = PCM16FramePacketizer()
            let steps = try XCTUnwrap(fixture["steps"] as? [Any])
            for stepItem in steps {
                let step = try XCTUnwrap(stepItem as? [String: Any])
                if SharedFixtures.text(step["kind"]) == "reset" {
                    packetizer.reset()
                    continue
                }
                let frames = packetizer.append(ramp(SharedFixtures.number(step["byteCount"])))
                XCTAssertEqual(
                    SharedFixtures.number(step["expectedFrameCount"]),
                    frames.count
                )
                for frame in frames {
                    XCTAssertEqual(PCM16FramePacketizer.bytesPerFrame, frame.count)
                }
            }

            XCTAssertEqual(
                SharedFixtures.number(fixture["expectedPendingBytes"]),
                packetizer.pendingByteCount
            )

            let flush = try XCTUnwrap(fixture["flush"] as? [String: Any])
            let flushed = packetizer.flushWithSilencePadding()
            if let expectedFlushBytes = SharedFixtures.optionalNumber(flush["expectedFrameBytes"]) {
                let frame = try XCTUnwrap(flushed)
                XCTAssertEqual(expectedFlushBytes, frame.count)
                XCTAssertEqual(
                    SharedFixtures.number(flush["expectedTrailingZeroBytes"]),
                    trailingZeroCount(frame)
                )
                XCTAssertEqual(0, packetizer.pendingByteCount)
            } else {
                XCTAssertNil(flushed)
            }
        }
    }

    // Given: フレーム境界と無関係な長さで分割した連続入力
    // When: 順に packetizer へ流し込む
    // Then: 出力フレームを連結すると入力バイト列が欠落なく復元される
    func testPacketizerPreservesTheInputStream() throws {
        let fixture = try XCTUnwrap(
            try SharedFixtures.load("audio")["packetizerContinuity"] as? [String: Any]
        )
        var packetizer = PCM16FramePacketizer()
        var input = Data()
        var emitted = Data()

        let appendByteCounts = try XCTUnwrap(fixture["appendByteCounts"] as? [Any])
        for byteCountValue in appendByteCounts {
            let byteCount = SharedFixtures.number(byteCountValue)
            let chunk = ramp(byteCount, offset: input.count)
            input.append(chunk)
            for frame in packetizer.append(chunk) {
                emitted.append(frame)
            }
        }

        XCTAssertEqual(SharedFixtures.number(fixture["totalInputBytes"]), input.count)
        XCTAssertEqual(
            SharedFixtures.number(fixture["expectedEmittedFrameCount"]),
            emitted.count / PCM16FramePacketizer.bytesPerFrame
        )

        let flushed = try XCTUnwrap(packetizer.flushWithSilencePadding())
        XCTAssertEqual(SharedFixtures.number(fixture["expectedFlushFrameBytes"]), flushed.count)
        XCTAssertEqual(
            SharedFixtures.number(fixture["expectedTrailingZeroBytes"]),
            trailingZeroCount(flushed)
        )

        emitted.append(flushed)
        XCTAssertEqual(Data(emitted.prefix(input.count)), input)
        XCTAssertTrue(emitted.dropFirst(input.count).allSatisfy { $0 == 0 })
    }

    // Given: fixture の float32 サンプル
    // When: PCM16 へ変換する
    // Then: クリップと丸めを含めて期待値と一致する
    func testFloat32ToPcm16MatchesFixture() throws {
        for name in try SharedFixtures.caseNames("audio", "float32ToPcm16") {
                        let fixture = try SharedFixtures.case("audio", "float32ToPcm16", name)
            var sample = Float(SharedFixtures.real(fixture["sample"]))
            let encoded = PCM16LittleEndianEncoder.encode(
                floatSamples: &sample,
                frameCount: 1,
                gain: Float(SharedFixtures.real(fixture["gain"]))
            )
            let actual = encoded.withUnsafeBytes { buffer -> Int16 in
                buffer.load(as: Int16.self)
            }
            XCTAssertEqual(Int16(SharedFixtures.number(fixture["expected"])), actual)
        }
    }

    // Given: shared fixture の適応ゲイン定数
    // When: Swift 実装の定数と照合する
    // Then: 最小/最大ゲイン、目標ピーク、無音/クリップ閾値が一致する
    func testGainConstantsMatchFixture() throws {
        let constants = try XCTUnwrap(
            (try SharedFixtures.load("audio")["gain"] as? [String: Any])?["constants"] as? [String: Any]
        )
        XCTAssertEqual(
            Float(SharedFixtures.real(constants["minimumGain"])),
            AdaptiveMicrophoneGain.minimumGain
        )
        XCTAssertEqual(
            Float(SharedFixtures.real(constants["maximumGain"])),
            AdaptiveMicrophoneGain.maximumGain
        )
        XCTAssertEqual(
            Float(SharedFixtures.real(constants["targetPeak"])),
            AdaptiveMicrophoneGain.targetPeak
        )
        XCTAssertEqual(
            Float(SharedFixtures.real(constants["silenceFloor"])),
            AdaptiveMicrophoneGain.silenceFloor
        )
        XCTAssertEqual(
            Float(SharedFixtures.real(constants["clipThreshold"])),
            AdaptiveMicrophoneGain.clipThreshold
        )
        XCTAssertEqual(
            Float(SharedFixtures.real(constants["defaultInitialGain"])),
            AdaptiveMicrophoneGain.defaultInitialGain
        )
    }

    // Given: fixture のピーク推移シナリオ
    // When: 順に適応ゲインを更新する
    // Then: 各ステップのゲイン値が期待値と一致する
    func testGainMatchesFixture() throws {
        let gainFixture = try XCTUnwrap(
            try SharedFixtures.load("audio")["gain"] as? [String: Any]
        )
        let tolerance = SharedFixtures.real(gainFixture["tolerance"])
        let cases = try XCTUnwrap(gainFixture["cases"] as? [Any])
        for caseItem in cases {
            let fixture = try XCTUnwrap(caseItem as? [String: Any])
            let name = SharedFixtures.text(fixture["name"])
                        var gain = AdaptiveMicrophoneGain(
                initialGain: Float(SharedFixtures.real(fixture["initialGain"]))
            )
            var last = gain.gain
            if fixture["repeatPeak"] != nil {
                let repeatCount = SharedFixtures.number(fixture["repeatCount"])
                let peak = Float(SharedFixtures.real(fixture["repeatPeak"]))
                for _ in 0..<repeatCount {
                    last = gain.observePeak(peak)
                }
            } else {
                let peaks = try XCTUnwrap(fixture["peaks"] as? [Any])
                for peakValue in peaks {
                    last = gain.observePeak(Float(SharedFixtures.real(peakValue)))
                }
            }
            XCTAssertEqual(
                SharedFixtures.real(fixture["expectedGain"]),
                Double(last),
                accuracy: tolerance
            )
            XCTAssertEqual(last, gain.gain)
        }
    }

    // Given: 非有限の初期ゲイン
    // When: AdaptiveMicrophoneGain を生成する
    // Then: 最小ゲインへ正規化される（Swift 実装は throw せず clamp する）
    func testNonFiniteInitialGainIsClamped() {
        let nanGain = AdaptiveMicrophoneGain(initialGain: .nan)
        let infinityGain = AdaptiveMicrophoneGain(initialGain: .infinity)
        XCTAssertEqual(nanGain.gain, AdaptiveMicrophoneGain.minimumGain)
        XCTAssertEqual(infinityGain.gain, AdaptiveMicrophoneGain.minimumGain)
    }

    // Given: 有限な初期ゲイン
    // When: 非有限ピークのあと有効ピークを観測する
    // Then: 状態は壊れず通常のクリップ減衰が動く
    func testNonFinitePeaksDoNotCorruptGainState() {
        var gain = AdaptiveMicrophoneGain(initialGain: 4.0)
        XCTAssertEqual(gain.observePeak(.nan), 4.0)
        XCTAssertEqual(gain.observePeak(.infinity), 4.0)
        let recovered = gain.observePeak(0.3)
        XCTAssertTrue(recovered.isFinite)
        XCTAssertGreaterThanOrEqual(recovered, AdaptiveMicrophoneGain.minimumGain)
        XCTAssertLessThanOrEqual(recovered, AdaptiveMicrophoneGain.maximumGain)
        XCTAssertEqual(recovered, 0.5 / 0.3, accuracy: 0.01)
    }

    /// 0 padding と区別できるよう、非ゼロの繰り返しパターンを作る。
    private func ramp(_ byteCount: Int, offset: Int = 0) -> Data {
        var bytes = [UInt8](repeating: 0, count: byteCount)
        for index in 0..<byteCount {
            bytes[index] = UInt8(((offset + index) % 255) + 1)
        }
        return Data(bytes)
    }

    private func trailingZeroCount(_ frame: Data) -> Int {
        var count = 0
        for byte in frame.reversed() {
            guard byte == 0 else { break }
            count += 1
        }
        return count
    }
}
