import XCTest
@testable import RealtimeTranslator

final class AdaptiveMicrophoneGainTests: XCTestCase {
    // Given: 極端な初期値
    // When: AdaptiveMicrophoneGain を生成する
    // Then: 生成時点で [minimumGain, maximumGain] へ clamp される
    func testInitialGainIsClampedToConfiguredRange() {
        XCTAssertEqual(
            AdaptiveMicrophoneGain(initialGain: 100).gain,
            AdaptiveMicrophoneGain.maximumGain
        )
        XCTAssertEqual(
            AdaptiveMicrophoneGain(initialGain: 0.01).gain,
            AdaptiveMicrophoneGain.minimumGain
        )
    }

    // Given: NaN / ±infinity の初期ゲイン
    // When: AdaptiveMicrophoneGain を生成する
    // Then: clamp 経由で minimumGain に落ちる（Swift 実装は throw しない）
    func testNonFiniteInitialGainFallsBackToMinimum() {
        for value: Float in [.nan, .infinity, -.infinity] {
            XCTAssertEqual(
                AdaptiveMicrophoneGain(initialGain: value).gain,
                AdaptiveMicrophoneGain.minimumGain
            )
        }
    }

    // Given: enabled / disabled の初期ゲイン4.0
    // When: 生成直後の appliedGain を見る
    // Then: enabled は gain、disabled は 1.0 から始まる
    func testInitialAppliedGainFollowsIsEnabled() {
        let enabled = AdaptiveMicrophoneGain(initialGain: 4.0)
        let disabled = AdaptiveMicrophoneGain(initialGain: 4.0, isEnabled: false)
        XCTAssertEqual(enabled.appliedGain, 4.0)
        XCTAssertEqual(disabled.appliedGain, 1.0)
    }

    // Given: disabled の AGC
    // When: 発話相当の統計を観測する
    // Then: 1.0 を返し、gain も appliedGain も変わらない
    func testDisabledReturnsUnityAndKeepsState() {
        var agc = AdaptiveMicrophoneGain(initialGain: 4.0, isEnabled: false)

        XCTAssertEqual(agc.observe(rms: 0.3, peak: 1.0), 1.0)
        XCTAssertEqual(agc.gain, 4.0)
        XCTAssertEqual(agc.appliedGain, 1.0)
    }

    // Given: 発話を観測済みの AGC と同じ有限列だけの基準
    // When: 非有限の rms / peak を挟む
    // Then: 状態は変わらず、現在の appliedGain を返す
    func testNonFiniteStatisticsKeepStateUnchanged() {
        var agc = AdaptiveMicrophoneGain(initialGain: 4.0)
        _ = agc.observe(rms: 0.02, peak: 0.06)
        let gainBefore = agc.gain
        let appliedBefore = agc.appliedGain

        XCTAssertEqual(agc.observe(rms: .nan, peak: 0.06), appliedBefore)
        XCTAssertEqual(agc.observe(rms: 0.02, peak: .infinity), appliedBefore)
        XCTAssertEqual(agc.observe(rms: -.infinity, peak: .nan), appliedBefore)

        XCTAssertEqual(agc.gain, gainBefore)
        XCTAssertEqual(agc.appliedGain, appliedBefore)
    }

    // Given: ゲインが持続している状態
    // When: クリップする大きなピークを1フレームだけ観測する
    // Then: 適用ゲインだけが下がり、持続する gain は下げ続けない
    func testLimiterLowersOnlyTheAppliedGain() {
        var agc = AdaptiveMicrophoneGain(initialGain: 4.0)
        _ = agc.observe(rms: 0.001, peak: 0.003)

        let limited = agc.observe(rms: 0.3, peak: 1.0)

        XCTAssertEqual(limited, AdaptiveMicrophoneGain.minimumGain)
        XCTAssertLessThan(agc.appliedGain, agc.gain)
    }

    // Given: 無音フレーム
    // When: 連続して観測する
    // Then: ゲインは暴騰せず初期値を保つ
    func testSilenceDoesNotInflateGain() {
        var agc = AdaptiveMicrophoneGain(initialGain: 4.0)
        for _ in 0..<30 {
            _ = agc.observe(rms: 0.0, peak: 0.0)
        }
        XCTAssertEqual(agc.gain, 4.0)
    }

    // Given: 2,400 サンプルのフレーム
    // When: process する
    // Then: 4,800 バイトの PCM16 が返る
    func testProcessReturnsOnePcmFrame() {
        var agc = AdaptiveMicrophoneGain()
        var samples = [Float](repeating: 0.1, count: AdaptiveMicrophoneGain.frameSamples)

        let data = samples.withUnsafeBufferPointer { buffer in
            agc.process(floatSamples: buffer.baseAddress!, frameCount: buffer.count)
        }

        XCTAssertEqual(data.count, AdaptiveMicrophoneGain.frameSamples * 2)
    }

    // Given: previous→current のゲイン差があるランプ
    // When: encodePCM16 で変換する
    // Then: 先頭は previous 側、末尾は current 側のゲインが掛かる
    func testRampedEncodeInterpolatesAppliedGain() {
        var samples = [Float](repeating: 0.1, count: AdaptiveMicrophoneGain.frameSamples)
        let data = samples.withUnsafeBufferPointer { buffer in
            AdaptiveMicrophoneGain.encodePCM16(
                floatSamples: buffer.baseAddress!,
                frameCount: buffer.count,
                previousAppliedGain: 1.0,
                appliedGain: 3.0,
                peak: 0.1
            )
        }

        let pcm = data.withUnsafeBytes { rawBuffer in
            Array(rawBuffer.bindMemory(to: Int16.self))
        }
        // 先頭サンプルは 1.0 に近いゲイン、ランプ終了以降は 3.0 一定になる。
        XCTAssertEqual(pcm[0], Int16((0.1 * Float(1.0 + 2.0 / 120.0) * 32767).rounded()))
        XCTAssertEqual(pcm[AdaptiveMicrophoneGain.rampSamples], Int16((0.1 * 3.0 * 32767).rounded()))
        XCTAssertEqual(pcm[pcm.count - 1], pcm[AdaptiveMicrophoneGain.rampSamples])
        XCTAssertLessThan(pcm[0], pcm[AdaptiveMicrophoneGain.rampSamples])
    }

    // Given: 録音開始直後から発話が続く系列（未確定フロア）
    // When: 観測する
    // Then: フロア未確定の間は下げず、4 フレームのポーズのあとは発話として上がる
    func testSpeechFromStartKeepsGainUntilFirstPause() {
        var agc = AdaptiveMicrophoneGain(initialGain: 4.0)
        var trace: [Float] = []
        for (rms, peak) in [(Float(0.01), Float(0.03)), (0.02, 0.06), (0.01, 0.03)]
            + Array(repeating: (Float(0.02), Float(0.06)), count: 4)
            + Array(repeating: (Float(0.001), Float(0.003)), count: 4)
            + Array(repeating: (Float(0.02), Float(0.06)), count: 4)
        {
            _ = agc.observe(rms: rms, peak: peak)
            trace.append(agc.gain)
        }

        let expected: [Float] = Array(repeating: 4.0, count: 11) + [4.48, 5.0, 5.0, 5.0]
        XCTAssertEqual(trace.count, expected.count)
        for (index, (actual, wanted)) in zip(trace, expected).enumerated() {
            XCTAssertEqual(actual, wanted, accuracy: 0.0005, "index \(index)")
        }
    }

    // Given: フロア確定 (30フレーム) に満たない一定ノイズ
    // When: 29 フレーム、30 フレーム、31 フレームと観測する
    // Then: 未確定の間は gain を下げず、確定と同時に noiseCap へ下がる
    func testUnconfirmedNoiseFloorNeverLowersGain() {
        var agc = AdaptiveMicrophoneGain(initialGain: 4.0)
        for _ in 0..<29 {
            _ = agc.observe(rms: 0.004, peak: 0.012)
            XCTAssertEqual(agc.gain, 4.0)
        }
        _ = agc.observe(rms: 0.004, peak: 0.012)
        XCTAssertEqual(agc.gain, 3.2, accuracy: 0.0005)
        _ = agc.observe(rms: 0.004, peak: 0.012)
        XCTAssertEqual(agc.gain, 2.56, accuracy: 0.0005)
    }

    // Given: 発話中にクリック（大ピーク）が1フレーム混じる系列
    // When: 観測する
    // Then: クリックは appliedGain だけを下げ、直後の発話で gain が回復する
    func testGainRecoversAfterClickDuringSpeech() {
        var agc = AdaptiveMicrophoneGain(initialGain: 4.0)
        for _ in 0..<30 {
            _ = agc.observe(rms: 0.001, peak: 0.003)
        }
        for _ in 0..<3 {
            _ = agc.observe(rms: 0.02, peak: 0.06)
        }
        XCTAssertEqual(agc.gain, 5.0, accuracy: 0.0005)

        _ = agc.observe(rms: 0.06, peak: 0.9)
        XCTAssertEqual(agc.gain, 4.0, accuracy: 0.0005)
        XCTAssertEqual(agc.appliedGain, 1.0, accuracy: 0.0005)
        _ = agc.observe(rms: 0.02, peak: 0.06)
        XCTAssertEqual(agc.gain, 4.48, accuracy: 0.0005)
        _ = agc.observe(rms: 0.02, peak: 0.06)
        XCTAssertEqual(agc.gain, 5.0, accuracy: 0.0005)
    }

    // Given: ポーズを挟まない 0.01 / 0.02 交互の変動発話
    // When: フロア確定 (30 フレーム) を超えて観測する
    // Then: 途切れない発話はノイズと判定され、noiseCap まで gain が下がる
    func testAlternatingSpeechWithoutPauseIsTreatedAsNoise() {
        var agc = AdaptiveMicrophoneGain(initialGain: 4.0)
        var trace: [Float] = []
        for index in 0..<40 {
            let (rms, peak) = index % 2 == 0 ? (Float(0.01), Float(0.03)) : (Float(0.02), Float(0.06))
            _ = agc.observe(rms: rms, peak: peak)
            trace.append(agc.gain)
        }

        XCTAssertTrue(trace[0...28].allSatisfy { $0 == 4.0 })
        XCTAssertEqual(trace[29], 3.2, accuracy: 0.0005)
        XCTAssertEqual(trace[30], 2.56, accuracy: 0.0005)
        XCTAssertEqual(trace[35], 1.0, accuracy: 0.0005)
        XCTAssertTrue(trace[35...].allSatisfy { $0 == 1.0 })
    }

    // Given: 定常ノイズに 20 フレーム周期で 1 フレームの谷が混じる系列
    // When: 600 フレーム観測する
    // Then: 1 フレームの谷はフロアを下げず、gain は 4.0 を超えず noiseCap (2.5) で頭打ちになる
    func testPeriodicDipInSteadyNoiseDoesNotRaiseGain() {
        var agc = AdaptiveMicrophoneGain(initialGain: 4.0)
        var trace: [Float] = []
        for _ in 0..<30 {
            for _ in 0..<19 {
                _ = agc.observe(rms: 0.004, peak: 0.012)
                trace.append(agc.gain)
            }
            _ = agc.observe(rms: 0.001, peak: 0.003)
            trace.append(agc.gain)
        }

        XCTAssertTrue(trace.allSatisfy { $0 <= 4.0 })
        XCTAssertEqual(trace[29], 3.2, accuracy: 0.0005)
        XCTAssertEqual(trace[31], 2.5, accuracy: 0.0005)
        XCTAssertEqual(trace[599], 2.5, accuracy: 0.0005)
    }

    // Given: 無音で noiseFloor が下限に落ちた状態
    // When: 一定ノイズのフレームを続けて観測する
    // Then: 窓がノイズで埋まるまで上がり、確定後は noiseCap (2.5) まで下がる
    func testSteadyNoiseAfterSilenceFallsToNoiseCap() {
        var agc = AdaptiveMicrophoneGain(initialGain: 4.0)
        var trace: [(gain: Float, appliedGain: Float)] = []
        for _ in 0..<5 {
            let applied = agc.observe(rms: 0, peak: 0)
            trace.append((agc.gain, applied))
        }
        for _ in 0..<200 {
            let applied = agc.observe(rms: 0.004, peak: 0.012)
            trace.append((agc.gain, applied))
        }

        XCTAssertEqual(trace[12].gain, 8.0, accuracy: 0.0005)
        XCTAssertEqual(trace[33].gain, 4.096, accuracy: 0.0005)
        XCTAssertEqual(trace[34].gain, 3.2768, accuracy: 0.0005)
        XCTAssertEqual(trace[35].gain, 2.62144, accuracy: 0.0005)
        XCTAssertEqual(trace[40].gain, 2.5, accuracy: 0.0005)
        XCTAssertEqual(agc.gain, 2.5, accuracy: 0.0005)
        XCTAssertEqual(agc.appliedGain, 2.5, accuracy: 0.0005)
    }

    // Given: 前フレームで高い appliedGain だった状態からの大きな音のフレーム
    // When: process する
    // Then: ランプ先頭を含め全サンプルがフレーム peak 上限 (29490) 以内に収まる
    func testDescendingRampIsCappedByFramePeakLimit() {
        var agc = AdaptiveMicrophoneGain(initialGain: 4.0)
        var quiet = [Float](repeating: 0, count: AdaptiveMicrophoneGain.frameSamples)
        _ = quiet.withUnsafeBufferPointer { buffer in
            agc.process(floatSamples: buffer.baseAddress!, frameCount: buffer.count)
        }
        XCTAssertEqual(agc.appliedGain, 4.0)

        var loud = [Float](repeating: 0.8, count: AdaptiveMicrophoneGain.frameSamples)
        let data = loud.withUnsafeBufferPointer { buffer in
            agc.process(floatSamples: buffer.baseAddress!, frameCount: buffer.count)
        }

        let pcm = data.withUnsafeBytes { rawBuffer in
            Array(rawBuffer.bindMemory(to: Int16.self))
        }
        XCTAssertTrue(pcm.allSatisfy { abs(Int($0)) <= 29490 })
    }

    // Given: 全サンプル非有限のフレーム
    // When: process する
    // Then: 統計が非有限なら状態を変えない（NaN は無音、±Infinity は ±full scale へクリップされる）
    func testProcessAllNonFiniteKeepsStateUnchanged() {
        var agc = AdaptiveMicrophoneGain(initialGain: 4.0)
        var samples: [Float] = [.nan, .infinity, -.infinity]

        let data = samples.withUnsafeBufferPointer { buffer in
            agc.process(floatSamples: buffer.baseAddress!, frameCount: buffer.count)
        }

        XCTAssertEqual(agc.gain, 4.0)
        XCTAssertEqual(agc.appliedGain, 4.0)
        XCTAssertEqual(data.count, samples.count * 2)
        // NaN は無音 (0)、+Inf は +32767、-Inf は -32767 にクリップされる。
        let pcm = data.withUnsafeBytes { rawBuffer in
            Array(rawBuffer.bindMemory(to: Int16.self))
        }
        XCTAssertEqual(pcm, [0, Int16.max, -Int16.max])
    }
}
