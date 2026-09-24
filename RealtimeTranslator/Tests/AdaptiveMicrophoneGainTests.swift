import XCTest
@testable import RealtimeTranslator

final class AdaptiveMicrophoneGainTests: XCTestCase {
    func testQuietSpeechAfterNoiseRaisesGainGradually() {
        // Given: 雑音フレームを1つ観測した初期ゲイン1.0
        var agc = AdaptiveMicrophoneGain(initialGain: 1.0)
        _ = agc.observe(rms: 0.001, peak: 0.004)

        // When: 雑音より十分大きく、音節ごとに音量が変わる小声の発話フレームを複数回観測する
        var previous = agc.gain
        var raisedEveryFrame = true
        for index in 0..<10 {
            let rms: Float = index.isMultiple(of: 2) ? 0.01 : 0.004
            let next = agc.observe(rms: rms, peak: rms * 5)
            if next <= previous || next > previous * AdaptiveMicrophoneGain.gainRiseFactor + 0.0001 {
                raisedEveryFrame = false
            }
            previous = next
        }

        // Then: ゲインは1フレームあたり最大12%ずつ上がり、上限8.0を超えない
        XCTAssertTrue(raisedEveryFrame)
        XCTAssertGreaterThan(agc.gain, 1.0)
        XCTAssertLessThanOrEqual(agc.gain, AdaptiveMicrophoneGain.maximumGain)
    }

    func testSteadyLevelAfterNoiseStopsRaisingGainWithinModulationWindow() {
        // Given: 雑音フレームを1つ観測した初期ゲイン1.0
        var agc = AdaptiveMicrophoneGain(initialGain: 1.0)
        _ = agc.observe(rms: 0.001, peak: 0.003)

        // When: 音量が一定のフレーム（ファンなど）を続けて観測する
        for _ in 0..<20 {
            _ = agc.observe(rms: 0.006, peak: 0.018)
        }

        // Then: 変動窓に雑音フレームが残る4フレームだけ上がり、その後は止まる
        XCTAssertEqual(agc.gain, 1.5735, accuracy: 0.0005)
    }

    func testSteadyNoiseDoesNotInflateGain() {
        // Given: 初期ゲイン1.0
        var agc = AdaptiveMicrophoneGain(initialGain: 1.0)

        // When: 旧実装の無音フロア(0.005)を超える雑音だけを連続観測する
        for _ in 0..<100 {
            _ = agc.observe(rms: 0.006, peak: 0.02)
        }

        // Then: 発話と判定されずゲインは動かない
        XCTAssertEqual(agc.gain, 1.0)
    }

    func testClickLimitsOnlyItsOwnFrame() {
        // Given: 雑音と発話を観測した初期ゲイン4.0
        var agc = AdaptiveMicrophoneGain(initialGain: 4.0)
        _ = agc.observe(rms: 0.001, peak: 0.003)
        _ = agc.observe(rms: 0.02, peak: 0.1)
        let gainBeforeClick = agc.gain

        // When: 瞬間的に大きいクリック音のフレームを観測する
        let appliedOnClick = agc.observe(rms: 0.0005, peak: 0.9)

        // Then: そのフレームの適用ゲインだけ1.0へ抑え、持続ゲインは下げない
        XCTAssertEqual(appliedOnClick, 1.0)
        XCTAssertEqual(agc.gain, gainBeforeClick)
        XCTAssertEqual(agc.observe(rms: 0.0005, peak: 0.01), gainBeforeClick)
    }

    func testGainIsClampedToConfiguredRange() {
        // Given: 極端な初期値
        let high = AdaptiveMicrophoneGain(initialGain: 100)
        let low = AdaptiveMicrophoneGain(initialGain: 0.01)

        // When/Then: 生成時点でclampされる
        XCTAssertEqual(high.gain, AdaptiveMicrophoneGain.maximumGain)
        XCTAssertEqual(low.gain, AdaptiveMicrophoneGain.minimumGain)
        XCTAssertEqual(high.appliedGain, AdaptiveMicrophoneGain.maximumGain)
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

    func testNonFiniteLevelsDoNotCorruptState() {
        // Given: 同じ雑音フレームを観測した対象と基準
        var agc = AdaptiveMicrophoneGain(initialGain: 4.0)
        var baseline = AdaptiveMicrophoneGain(initialGain: 4.0)
        _ = agc.observe(rms: 0.001, peak: 0.003)
        _ = baseline.observe(rms: 0.001, peak: 0.003)

        // When: 対象だけ非有限値を挟んでから発話フレームを観測する
        XCTAssertEqual(agc.observe(rms: .nan, peak: 0.1), 4.0)
        XCTAssertEqual(agc.observe(rms: 0.02, peak: .infinity), 4.0)
        let after = agc.observe(rms: 0.02, peak: 0.1)
        let expected = baseline.observe(rms: 0.02, peak: 0.1)

        // Then: 非有限値は状態を変えず、基準と同じゲインになる
        XCTAssertEqual(after, expected)
        XCTAssertEqual(agc.gain, baseline.gain)
    }

    func testMeasureLevelIgnoresNonFiniteSamples() {
        // Given: 有限サンプルと NaN / ±Infinity が混在するフレーム、非有限だけのフレーム
        let mixed: [Float] = [.nan, 0.3, -0.4, .infinity]
        let nonFinite: [Float] = [.nan, -.infinity]

        // When: レベルを測る
        let level = mixed.withUnsafeBufferPointer { AdaptiveMicrophoneGain.measureLevel($0) }
        let empty = nonFinite.withUnsafeBufferPointer { AdaptiveMicrophoneGain.measureLevel($0) }

        // Then: 有限サンプルだけの RMS と絶対値ピーク、有限が無ければ 0
        XCTAssertEqual(level.rms, 0.3535534, accuracy: 0.0005)
        XCTAssertEqual(level.peak, 0.4)
        XCTAssertEqual(empty.rms, 0)
        XCTAssertEqual(empty.peak, 0)
    }

    func testProcessRampsFromPreviousAppliedGainAndLimitsPeaks() {
        // Given: 初期ゲイン4.0で雑音フレームを処理済み
        var agc = AdaptiveMicrophoneGain(initialGain: 4.0)
        let noise = [Float](repeating: 0.001, count: AdaptiveMicrophoneGain.frameSamples)
        let noisePCM = noise.withUnsafeBufferPointer { agc.process(frame: $0) }
        XCTAssertEqual(pcmValues(noisePCM).first, 131)

        // When: ピーク0.6の大きな音のフレームを処理する
        let loud = [Float](repeating: 0.6, count: AdaptiveMicrophoneGain.frameSamples)
        let values = pcmValues(loud.withUnsafeBufferPointer { agc.process(frame: $0) })

        // Then: 先頭サンプルからリミッタで約0.9に収まり、クリップしない
        XCTAssertEqual(values.count, AdaptiveMicrophoneGain.frameSamples)
        XCTAssertEqual(values.first, 29490)
        XCTAssertEqual(values.last, 29490)
        XCTAssertEqual(agc.appliedGain, 1.5, accuracy: 0.0005)
        XCTAssertEqual(agc.gain, 3.2, accuracy: 0.0005)
    }

    func testDisabledGainEncodesAtUnity() {
        // Given: 自動ゲインを無効にした適応ゲイン
        var agc = AdaptiveMicrophoneGain(initialGain: 4.0, isEnabled: false)
        let frame = [Float](repeating: 0.1, count: AdaptiveMicrophoneGain.frameSamples)

        // When: 雑音と発話相当のフレームを処理する
        _ = agc.observe(rms: 0.001, peak: 0.003)
        let values = pcmValues(frame.withUnsafeBufferPointer { agc.process(frame: $0) })

        // Then: ゲイン1.0のまま変換し、状態も動かない
        XCTAssertFalse(agc.isEnabled)
        XCTAssertEqual(values.first, 3277)
        XCTAssertEqual(values.last, 3277)
        XCTAssertEqual(agc.gain, AdaptiveMicrophoneGain.minimumGain)
        XCTAssertEqual(agc.appliedGain, AdaptiveMicrophoneGain.minimumGain)
    }

    private func pcmValues(_ data: Data) -> [Int16] {
        data.withUnsafeBytes { raw in Array(raw.bindMemory(to: Int16.self)) }
    }
}
