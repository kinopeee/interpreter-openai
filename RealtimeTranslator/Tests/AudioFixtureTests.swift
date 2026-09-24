import XCTest
@testable import RealtimeTranslator

final class AudioFixtureTests: XCTestCase {
    // Given: shared fixture の音声フォーマット定義
    // When: packetizer の定数と照合する
    // Then: 24 kHz / 100 ms / 2,400 sample / 4,800 byte が一致する
    func testFormatMatchesFixture() throws {
        let format = try XCTUnwrap(
            try SharedFixtures.load("audio", version: 2)["format"] as? [String: Any]
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
        for name in try SharedFixtures.caseNames("audio", "packetizer", version: 2) {
            let fixture = try SharedFixtures.case("audio", "packetizer", name, version: 2)
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
            try SharedFixtures.load("audio", version: 2)["packetizerContinuity"] as? [String: Any]
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
        for name in try SharedFixtures.caseNames("audio", "float32ToPcm16", version: 2) {
            let fixture = try SharedFixtures.case("audio", "float32ToPcm16", name, version: 2)
            var sample = Float(SharedFixtures.real(fixture["sample"]))
            let encoded = PCM16LittleEndianEncoder.encode(
                floatSamples: &sample,
                frameCount: 1,
                gain: Float(SharedFixtures.real(fixture["gain"]))
            )
            let actual = encoded.withUnsafeBytes { buffer -> Int16 in
                Int16(littleEndian: buffer.loadUnaligned(as: Int16.self))
            }
            XCTAssertEqual(Int16(SharedFixtures.number(fixture["expected"])), actual)
        }
    }

    // Given: shared fixture v2 の適応ゲイン定数
    // When: Swift 実装の定数と照合する
    // Then: フレーム長・ゲイン範囲・発話判定・雑音窓・上昇/下降率・リミッタ・ランプが一致する
    func testGainConstantsMatchFixture() throws {
        let constants = try XCTUnwrap(
            (try SharedFixtures.load("audio", version: 2)["gain"] as? [String: Any])?["constants"]
                as? [String: Any]
        )
        XCTAssertEqual(SharedFixtures.number(constants["frameSamples"]), AdaptiveMicrophoneGain.frameSamples)
        let floatConstants: [(String, Float)] = [
            ("minimumGain", AdaptiveMicrophoneGain.minimumGain),
            ("maximumGain", AdaptiveMicrophoneGain.maximumGain),
            ("defaultInitialGain", AdaptiveMicrophoneGain.defaultInitialGain),
            ("targetRms", AdaptiveMicrophoneGain.targetRms),
            ("speechRatio", AdaptiveMicrophoneGain.speechRatio),
            ("speechAbsoluteFloor", AdaptiveMicrophoneGain.speechAbsoluteFloor),
            ("digitalSilenceRms", AdaptiveMicrophoneGain.digitalSilenceRms),
            ("gainRiseFactor", AdaptiveMicrophoneGain.gainRiseFactor),
            ("gainFallFactor", AdaptiveMicrophoneGain.gainFallFactor),
            ("clipCeiling", AdaptiveMicrophoneGain.clipCeiling),
        ]
        for (key, value) in floatConstants {
            XCTAssertEqual(Float(SharedFixtures.real(constants[key])), value, key)
        }
        XCTAssertEqual(
            SharedFixtures.number(constants["noiseWindowFrames"]),
            AdaptiveMicrophoneGain.noiseWindowFrames
        )
        XCTAssertEqual(SharedFixtures.number(constants["rampSamples"]), AdaptiveMicrophoneGain.rampSamples)
    }

    // Given: fixture のフレーム列（RMS とピーク、繰り返し回数、有効/無効）
    // When: 順に適応ゲインへ取り込む
    // Then: 最後の持続ゲインと適用ゲインが期待値と一致する
    func testGainMatchesFixture() throws {
        let gainFixture = try XCTUnwrap(
            try SharedFixtures.load("audio", version: 2)["gain"] as? [String: Any]
        )
        let tolerance = SharedFixtures.real(gainFixture["tolerance"])
        let cases = try XCTUnwrap(gainFixture["cases"] as? [Any])
        for caseItem in cases {
            let fixture = try XCTUnwrap(caseItem as? [String: Any])
            let name = SharedFixtures.text(fixture["name"])
            let isEnabled = fixture["enabled"] == nil || SharedFixtures.flag(fixture["enabled"])
            var gain = AdaptiveMicrophoneGain(
                initialGain: Float(SharedFixtures.real(fixture["initialGain"])),
                isEnabled: isEnabled
            )
            var last = gain.appliedGain
            let frames = try XCTUnwrap(fixture["frames"] as? [Any])
            for frameItem in frames {
                let frame = try XCTUnwrap(frameItem as? [String: Any])
                let repeatCount = SharedFixtures.optionalNumber(frame["repeat"]) ?? 1
                for _ in 0..<repeatCount {
                    last = gain.observe(
                        rms: Float(SharedFixtures.real(frame["rms"])),
                        peak: Float(SharedFixtures.real(frame["peak"]))
                    )
                }
            }
            let expected = try XCTUnwrap(fixture["expected"] as? [String: Any])
            XCTAssertEqual(
                SharedFixtures.real(expected["gain"]),
                Double(gain.gain),
                accuracy: tolerance,
                name
            )
            XCTAssertEqual(
                SharedFixtures.real(expected["appliedGain"]),
                Double(last),
                accuracy: tolerance,
                name
            )
            XCTAssertEqual(isEnabled ? gain.appliedGain : AdaptiveMicrophoneGain.minimumGain, last, name)
        }
    }

    // Given: fixture のサンプル列
    // When: フレームの RMS とピークを求める
    // Then: 期待値と一致する
    func testGainLevelMatchesFixture() throws {
        let gainFixture = try XCTUnwrap(
            try SharedFixtures.load("audio", version: 2)["gain"] as? [String: Any]
        )
        let tolerance = SharedFixtures.real(gainFixture["tolerance"])
        let cases = try XCTUnwrap(gainFixture["level"] as? [Any])
        for caseItem in cases {
            let fixture = try XCTUnwrap(caseItem as? [String: Any])
            let name = SharedFixtures.text(fixture["name"])
            let samples = try XCTUnwrap(fixture["samples"] as? [Any]).map {
                Float(SharedFixtures.real($0))
            }
            let level = samples.withUnsafeBufferPointer { AdaptiveMicrophoneGain.measureLevel($0) }
            XCTAssertEqual(
                SharedFixtures.real(fixture["expectedRms"]),
                Double(level.rms),
                accuracy: tolerance,
                name
            )
            XCTAssertEqual(
                SharedFixtures.real(fixture["expectedPeak"]),
                Double(level.peak),
                accuracy: tolerance,
                name
            )
        }
    }

    // Given: 前フレームと今回の適用ゲイン、一定値のサンプルで満たした 100 ms frame
    // When: ランプ付きで PCM16 へ変換する
    // Then: 指定インデックスの値が期待値と一致する
    func testGainRampMatchesFixture() throws {
        let gainFixture = try XCTUnwrap(
            try SharedFixtures.load("audio", version: 2)["gain"] as? [String: Any]
        )
        let cases = try XCTUnwrap(gainFixture["ramp"] as? [Any])
        for caseItem in cases {
            let fixture = try XCTUnwrap(caseItem as? [String: Any])
            let name = SharedFixtures.text(fixture["name"])
            let frame = [Float](
                repeating: Float(SharedFixtures.real(fixture["sample"])),
                count: AdaptiveMicrophoneGain.frameSamples
            )
            let encoded = frame.withUnsafeBufferPointer { buffer in
                PCM16LittleEndianEncoder.encode(
                    floatSamples: buffer,
                    fromGain: Float(SharedFixtures.real(fixture["previousAppliedGain"])),
                    toGain: Float(SharedFixtures.real(fixture["appliedGain"])),
                    rampSamples: AdaptiveMicrophoneGain.rampSamples
                )
            }
            let values = encoded.withUnsafeBytes { raw in Array(raw.bindMemory(to: Int16.self)) }
            let indices = try XCTUnwrap(fixture["indices"] as? [Any]).map { SharedFixtures.number($0) }
            let expected = try XCTUnwrap(fixture["expected"] as? [Any]).map { SharedFixtures.number($0) }
            XCTAssertEqual(indices.count, expected.count, name)
            for (index, value) in zip(indices, expected) {
                XCTAssertEqual(Int(values[index]), value, "\(name) index \(index)")
            }
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
