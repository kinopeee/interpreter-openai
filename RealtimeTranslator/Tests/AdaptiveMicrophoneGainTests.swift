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

    func testNonFinitePeakDoesNotCorruptTrackedState() {
        // Given: 初期ゲイン4.0
        var agc = AdaptiveMicrophoneGain(initialGain: 4.0)
        let before = agc.gain

        // When: NaN / infinity のピークを観測する
        let afterNan = agc.observePeak(.nan)
        let afterInfinity = agc.observePeak(.infinity)

        // Then: 追跡状態を壊さず現ゲインを維持する
        XCTAssertEqual(afterNan, before)
        XCTAssertEqual(afterInfinity, before)
        XCTAssertEqual(agc.gain, before)
    }

    func testAllNonFiniteSamplesKeepCurrentGain() {
        // Given: 非有限サンプルだけのバッファ
        var agc = AdaptiveMicrophoneGain(initialGain: 4.0)
        var samples: [Float] = [.nan, .infinity, -.infinity]

        // When: observe に渡す
        let gain = samples.withUnsafeBufferPointer { buffer in
            agc.observe(floatSamples: buffer.baseAddress!, frameCount: buffer.count)
        }

        // Then: 有限サンプルが無いのでゲインは動かない
        XCTAssertEqual(gain, 4.0)
        XCTAssertEqual(agc.gain, 4.0)
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
