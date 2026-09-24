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

    // Given: fixture の float32 サンプル分割ケース
    // When: accumulator へ流し込む
    // Then: フレーム分割・pending・flush padding が期待値と一致する
    func testFloat32FramingMatchesFixture() throws {
        let framing = try XCTUnwrap(
            try SharedFixtures.load("audio", version: 2)["float32Framing"] as? [String: Any]
        )
        XCTAssertEqual(
            SharedFixtures.number(framing["frameSamples"]),
            Float32FrameAccumulator.frameSamples
        )
        let cases = try XCTUnwrap(framing["cases"] as? [Any])
        for caseItem in cases {
            let fixture = try XCTUnwrap(caseItem as? [String: Any])
            var accumulator = Float32FrameAccumulator()
            let appendCounts = try XCTUnwrap(fixture["appendSampleCounts"] as? [Any])
            let expectedCounts = try XCTUnwrap(fixture["expectedFrameCounts"] as? [Any])
            XCTAssertEqual(appendCounts.count, expectedCounts.count)

            for (appendValue, expectedValue) in zip(appendCounts, expectedCounts) {
                var samples = [Float](repeating: 0.5, count: SharedFixtures.number(appendValue))
                let frames = samples.withUnsafeBufferPointer { buffer in
                    accumulator.append(buffer)
                }
                XCTAssertEqual(
                    SharedFixtures.number(expectedValue),
                    frames.count,
                    SharedFixtures.text(fixture["name"])
                )
                for frame in frames {
                    XCTAssertEqual(Float32FrameAccumulator.frameSamples, frame.count)
                }
            }

            XCTAssertEqual(
                SharedFixtures.number(fixture["expectedPendingSamples"]),
                accumulator.pendingSampleCount,
                SharedFixtures.text(fixture["name"])
            )

            let flushed = accumulator.flushWithSilencePadding()
            if let expectedPadding = fixture["flushPadsSilenceSamples"], !(expectedPadding is NSNull) {
                let frame = try XCTUnwrap(flushed)
                XCTAssertEqual(Float32FrameAccumulator.frameSamples, frame.count)
                XCTAssertEqual(SharedFixtures.number(expectedPadding), trailingZeroCount(frame))
            } else {
                XCTAssertNil(flushed)
            }
        }
    }

    // Given: fixture のフレーム統計ケース
    // When: frameStatistics を計算する
    // Then: rms / peak が期待値と一致する
    func testFrameStatisticsMatchesFixture() throws {
        let statistics = try XCTUnwrap(
            try SharedFixtures.load("audio", version: 2)["frameStatistics"] as? [String: Any]
        )
        let cases = try XCTUnwrap(statistics["cases"] as? [Any])
        for caseItem in cases {
            let fixture = try XCTUnwrap(caseItem as? [String: Any])
            let name = SharedFixtures.text(fixture["name"])
            var samples = try XCTUnwrap(fixture["samples"] as? [Any]).map(fixtureFloat)
            let result = samples.withUnsafeBufferPointer { buffer in
                AdaptiveMicrophoneGain.frameStatistics(
                    floatSamples: buffer.baseAddress!,
                    frameCount: buffer.count
                )
            }
            assertFixtureFloatEqual(
                fixture["expectedRms"],
                result.rms,
                name: name
            )
            assertFixtureFloatEqual(
                fixture["expectedPeak"],
                result.peak,
                name: name
            )
        }
    }

    // Given: shared fixture の適応ゲイン定数
    // When: Swift 実装の定数と照合する
    // Then: 全定数が一致する
    func testGainConstantsMatchFixture() throws {
        let constants = try XCTUnwrap(
            (try SharedFixtures.load("audio", version: 2)["gain"] as? [String: Any])?["constants"]
                as? [String: Any]
        )
        XCTAssertEqual(
            SharedFixtures.number(constants["frameSamples"]),
            AdaptiveMicrophoneGain.frameSamples
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
            Float(SharedFixtures.real(constants["defaultInitialGain"])),
            AdaptiveMicrophoneGain.defaultInitialGain
        )
        XCTAssertEqual(
            Float(SharedFixtures.real(constants["targetRms"])),
            AdaptiveMicrophoneGain.targetRms
        )
        XCTAssertEqual(
            Float(SharedFixtures.real(constants["speechRatio"])),
            AdaptiveMicrophoneGain.speechRatio
        )
        XCTAssertEqual(
            Float(SharedFixtures.real(constants["speechAbsoluteFloor"])),
            AdaptiveMicrophoneGain.speechAbsoluteFloor
        )
        XCTAssertEqual(
            Float(SharedFixtures.real(constants["noiseCeiling"])),
            AdaptiveMicrophoneGain.noiseCeiling
        )
        XCTAssertEqual(
            Float(SharedFixtures.real(constants["noiseFloorMinimum"])),
            AdaptiveMicrophoneGain.noiseFloorMinimum
        )
        XCTAssertEqual(
            Float(SharedFixtures.real(constants["noiseFloorRise"])),
            AdaptiveMicrophoneGain.noiseFloorRise
        )
        XCTAssertEqual(
            Float(SharedFixtures.real(constants["gainRise"])),
            AdaptiveMicrophoneGain.gainRise
        )
        XCTAssertEqual(
            Float(SharedFixtures.real(constants["gainFall"])),
            AdaptiveMicrophoneGain.gainFall
        )
        XCTAssertEqual(
            Float(SharedFixtures.real(constants["clipCeiling"])),
            AdaptiveMicrophoneGain.clipCeiling
        )
        XCTAssertEqual(
            SharedFixtures.number(constants["rampSamples"]),
            AdaptiveMicrophoneGain.rampSamples
        )
    }

    // Given: fixture のゲイン推移シナリオ
    // When: repeat 展開した各フレームを順に観測する
    // Then: 各フレームの gain / appliedGain が期待値と一致する
    func testGainMatchesFixture() throws {
        let gainFixture = try XCTUnwrap(
            try SharedFixtures.load("audio", version: 2)["gain"] as? [String: Any]
        )
        let tolerance = SharedFixtures.real(gainFixture["tolerance"])
        let cases = try XCTUnwrap(gainFixture["cases"] as? [Any])
        for caseItem in cases {
            let fixture = try XCTUnwrap(caseItem as? [String: Any])
            let name = SharedFixtures.text(fixture["name"])
            let enabled = fixture["enabled"].map { SharedFixtures.flag($0) } ?? true
            var agc = AdaptiveMicrophoneGain(
                initialGain: Float(SharedFixtures.real(fixture["initialGain"])),
                isEnabled: enabled
            )

            let frames = try XCTUnwrap(fixture["frames"] as? [Any])
            var trace: [(gain: Float, appliedGain: Float)] = []
            for frameItem in frames {
                let frame = try XCTUnwrap(frameItem as? [String: Any])
                let repeatCount = SharedFixtures.optionalNumber(frame["repeat"]) ?? 1
                for _ in 0..<repeatCount {
                    let applied = agc.observe(
                        rms: fixtureFloat(frame["rms"]),
                        peak: fixtureFloat(frame["peak"])
                    )
                    trace.append((agc.gain, applied))
                }
            }

            if let expectedTrace = fixture["expectedTrace"] as? [Any] {
                XCTAssertEqual(expectedTrace.count, trace.count, name)
                for (index, expectedItem) in expectedTrace.enumerated() {
                    let expected = try XCTUnwrap(expectedItem as? [String: Any])
                    XCTAssertEqual(
                        SharedFixtures.real(expected["gain"]),
                        Double(trace[index].gain),
                        accuracy: tolerance,
                        "\(name) [\(index)] gain"
                    )
                    XCTAssertEqual(
                        SharedFixtures.real(expected["appliedGain"]),
                        Double(trace[index].appliedGain),
                        accuracy: tolerance,
                        "\(name) [\(index)] appliedGain"
                    )
                }
            }

            let expectedFinal = try XCTUnwrap(fixture["expectedFinal"] as? [String: Any])
            XCTAssertEqual(
                SharedFixtures.real(expectedFinal["gain"]),
                Double(agc.gain),
                accuracy: tolerance,
                name
            )
            XCTAssertEqual(
                SharedFixtures.real(expectedFinal["appliedGain"]),
                Double(agc.appliedGain),
                accuracy: tolerance,
                name
            )
        }
    }

    // Given: fixture のゲインランプケース
    // When: previous→applied ゲインでランプ付き PCM16 化する
    // Then: 各 index の Int16 が完全一致する
    func testGainRampMatchesFixture() throws {
        let gainFixture = try XCTUnwrap(
            try SharedFixtures.load("audio", version: 2)["gain"] as? [String: Any]
        )
        let ramp = try XCTUnwrap(gainFixture["ramp"] as? [String: Any])
        let cases = try XCTUnwrap(ramp["cases"] as? [Any])
        for caseItem in cases {
            let fixture = try XCTUnwrap(caseItem as? [String: Any])
            let name = SharedFixtures.text(fixture["name"])
            var samples = [Float](
                repeating: Float(SharedFixtures.real(fixture["sample"])),
                count: AdaptiveMicrophoneGain.frameSamples
            )
            let data = samples.withUnsafeBufferPointer { buffer in
                AdaptiveMicrophoneGain.encodePCM16(
                    floatSamples: buffer.baseAddress!,
                    frameCount: buffer.count,
                    previousAppliedGain: Float(SharedFixtures.real(fixture["previousAppliedGain"])),
                    appliedGain: Float(SharedFixtures.real(fixture["appliedGain"])),
                    peak: Float(SharedFixtures.real(fixture["peak"]))
                )
            }
            let pcm = data.withUnsafeBytes { rawBuffer in
                Array(rawBuffer.bindMemory(to: Int16.self))
            }

            for checkItem in try XCTUnwrap(fixture["checks"] as? [Any]) {
                let check = try XCTUnwrap(checkItem as? [String: Any])
                let index = SharedFixtures.number(check["index"])
                XCTAssertEqual(
                    Int16(SharedFixtures.number(check["expectedPcm16"])),
                    pcm[index],
                    "\(name) [\(index)]"
                )
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

    private func trailingZeroCount(_ frame: [Float]) -> Int {
        var count = 0
        for sample in frame.reversed() {
            guard sample == 0 else { break }
            count += 1
        }
        return count
    }

    /// fixture の number / "nan" / "infinity" / "-infinity" 表記を Float へ変換する。
    private func fixtureFloat(_ value: Any?) -> Float {
        if let text = value as? String {
            switch text {
            case "nan": return .nan
            case "infinity": return .infinity
            case "-infinity": return -.infinity
            default: fatalError("unknown fixture float literal: \(text)")
            }
        }
        return Float(SharedFixtures.real(value))
    }

    /// 期待値が "nan" 系なら isNaN、数値なら 1e-6 以内を照合する。
    private func assertFixtureFloatEqual(
        _ expected: Any?,
        _ actual: Float,
        name: String
    ) {
        if expected is String {
            XCTAssertTrue(actual.isNaN, name)
            return
        }
        XCTAssertEqual(SharedFixtures.real(expected), Double(actual), accuracy: 1e-6, name)
    }
}
