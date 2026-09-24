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
                appliedGain: 3.0
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
    }
}
