import XCTest
@testable import RealtimeTranslator

final class AdaptiveMicrophoneGainTests: XCTestCase {
    func testQuietInputRaisesGainGradually() {
        // Given: 初期ゲイン4.0、目標ピーク0.5に対して小さな入力
        var agc = AdaptiveMicrophoneGain(initialGain: 4.0)
        let quietPeak: Float = 0.05

        // When: 同じ小音量を複数回観測する
        var previous = agc.gain
        var raised = false
        for _ in 0..<40 {
            let next = agc.observePeak(quietPeak)
            if next > previous {
                raised = true
            }
            previous = next
        }

        // Then: ゲインは漸増し、上限8.0を超えない
        XCTAssertTrue(raised)
        XCTAssertGreaterThan(agc.gain, 4.0)
        XCTAssertLessThanOrEqual(agc.gain, AdaptiveMicrophoneGain.maximumGain)
    }

    func testClippingInputLowersGainImmediately() {
        // Given: 初期ゲイン4.0で、増幅後にクリップするピーク
        var agc = AdaptiveMicrophoneGain(initialGain: 4.0)

        // When: 0.3 × 4.0 = 1.2 でクリップする入力を1回観測する
        let gain = agc.observePeak(0.3)

        // Then: 目標ピーク0.5 / 0.3 ≈ 1.67 付近へ即減衰する
        XCTAssertLessThan(gain, 4.0)
        XCTAssertEqual(gain, 0.5 / 0.3, accuracy: 0.01)
    }

    func testSilenceDoesNotInflateGain() {
        // Given: 初期ゲイン4.0
        var agc = AdaptiveMicrophoneGain(initialGain: 4.0)

        // When: 無音相当のピークを連続観測する
        for _ in 0..<50 {
            _ = agc.observePeak(0)
        }

        // Then: ゲインは動かない (暴騰しない)
        XCTAssertEqual(agc.gain, 4.0)
    }

    func testGainIsClampedToConfiguredRange() {
        // Given: 極端な初期値
        var high = AdaptiveMicrophoneGain(initialGain: 100)
        var low = AdaptiveMicrophoneGain(initialGain: 0.01)

        // When/Then: 生成時点でclampされる
        XCTAssertEqual(high.gain, AdaptiveMicrophoneGain.maximumGain)
        XCTAssertEqual(low.gain, AdaptiveMicrophoneGain.minimumGain)
    }

    func testNonFiniteInitialGainFallsBackToMinimum() {
        // Given: NaN / ±infinity の初期ゲイン
        let values: [Float] = [.nan, .infinity, -.infinity]

        // When/Then: clamp 経由で minimumGain に落ちる
        for value in values {
            XCTAssertEqual(
                AdaptiveMicrophoneGain(initialGain: value).gain,
                AdaptiveMicrophoneGain.minimumGain
            )
        }
    }

    func testNonFinitePeakDoesNotCorruptTrackedState() {
        // Given: 有限 seed を観測した対象と、同じ有限列だけの基準
        // followUp は seed より小さくし、新ピーク上書きで破損が隠れないようにする
        var agc = AdaptiveMicrophoneGain(initialGain: 4.0)
        var baseline = AdaptiveMicrophoneGain(initialGain: 4.0)
        let seedPeak: Float = 0.2
        let followUpPeak: Float = 0.1
        _ = agc.observePeak(seedPeak)
        _ = baseline.observePeak(seedPeak)

        // When: 非有限ピークを挟んだあと、seed より小さい有限ピークを観測する
        let gainBeforeNaN = agc.gain
        XCTAssertEqual(agc.observePeak(.nan), gainBeforeNaN)
        XCTAssertEqual(agc.gain, gainBeforeNaN)

        let gainBeforeInfinity = agc.gain
        XCTAssertEqual(agc.observePeak(.infinity), gainBeforeInfinity)
        XCTAssertEqual(agc.gain, gainBeforeInfinity)

        let after = agc.observePeak(followUpPeak)
        let expected = baseline.observePeak(followUpPeak)

        // Then: trackedPeak が壊れていなければ基準と同じゲインになる
        XCTAssertEqual(after, expected, accuracy: 0.0001)
        XCTAssertEqual(agc.gain, baseline.gain, accuracy: 0.0001)
    }

    func testAllNonFiniteSamplesKeepCurrentGain() {
        // Given: 有限 seed 後に非有限だけのバッファを渡す対象と、有限列だけの基準
        var agc = AdaptiveMicrophoneGain(initialGain: 4.0)
        var baseline = AdaptiveMicrophoneGain(initialGain: 4.0)
        let seedPeak: Float = 0.2
        let followUpPeak: Float = 0.1
        _ = agc.observePeak(seedPeak)
        _ = baseline.observePeak(seedPeak)
        var samples: [Float] = [.nan, .infinity, -.infinity]
        let gainBefore = agc.gain

        // When: 非有限バッファを observe し、続けて seed より小さい有限ピークを観測する
        let gain = samples.withUnsafeBufferPointer { buffer in
            agc.observe(floatSamples: buffer.baseAddress!, frameCount: buffer.count)
        }
        XCTAssertEqual(gain, gainBefore)
        XCTAssertEqual(agc.gain, gainBefore)

        let after = agc.observePeak(followUpPeak)
        let expected = baseline.observePeak(followUpPeak)

        // Then: 非有限ではゲインを動かさず、その後の挙動も基準と一致する
        XCTAssertEqual(after, expected, accuracy: 0.0001)
        XCTAssertEqual(agc.gain, baseline.gain, accuracy: 0.0001)
    }

    func testMixedFiniteAndNonFiniteSamplesUseFinitePeakOnly() {
        // Given: 有限サンプルと NaN が混在するバッファ
        var agc = AdaptiveMicrophoneGain(initialGain: 4.0)
        var samples: [Float] = [.nan, 0.3, .infinity]

        // When: observe する
        let gain = samples.withUnsafeBufferPointer { buffer in
            agc.observe(floatSamples: buffer.baseAddress!, frameCount: buffer.count)
        }

        // Then: 有限ピーク 0.3 だけを使い、クリップ減衰する
        XCTAssertEqual(gain, 0.5 / 0.3, accuracy: 0.01)
    }
}
