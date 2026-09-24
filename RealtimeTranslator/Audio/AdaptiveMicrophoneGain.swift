import Foundation

/// マイク入力の RMS を追跡し、目標レベルへ近づける適応ゲイン (契約 v2)。
///
/// feederタスクから直列に呼ぶ前提。100ms floatフレームごとに `process` を呼び、
/// フレーム内では適用ゲインをランプさせて不連続を防ぐ。クリップ limiter は
/// 当該フレームの適用ゲインだけを下げ、持続する `gain` は変えない。
struct AdaptiveMicrophoneGain: Sendable {
    static let frameSamples = PCM16FramePacketizer.samplesPerFrame
    static let minimumGain: Float = 1.0
    static let maximumGain: Float = 8.0
    /// shared/fixtures/v2/audio.json の defaultInitialGain と一致させる。
    static let defaultInitialGain: Float = 4.0
    /// 目標 RMS。
    static let targetRms: Float = 0.1
    /// 発話判定: rms が noiseFloor のこの倍以上なら発話とみなす。
    static let speechRatio: Float = 3.16
    /// 発話判定: これ未満の rms は音量に関わらず非発話。
    static let speechAbsoluteFloor: Float = 0.003
    /// noiseFloor に比例して許容する増幅の天井。静かな環境での暴騰を防ぐ。
    static let noiseCeiling: Float = 0.01
    /// noiseFloor の下限。デジタル無音 (rms=0) で noiseFloor が 0 に固定されないようにする。
    static let noiseFloorMinimum: Float = 0.0001
    /// 非発話中に noiseFloor が上がるペース。
    static let noiseFloorRise: Float = 1.01
    /// フレームあたりのゲイン上昇上限。
    static let gainRise: Float = 1.12
    /// フレームあたりのゲイン下降上限。
    static let gainFall: Float = 0.8
    /// 増幅後に許容するピークの天井。
    static let clipCeiling: Float = 0.9
    /// 適用ゲインを previous→current へ線形に遷移させる先頭サンプル数。
    static let rampSamples = 120

    let isEnabled: Bool
    private(set) var gain: Float
    /// 直前フレームへ実際に掛けたゲイン。ランプの起点になる。
    private(set) var appliedGain: Float
    private var noiseFloor: Float?

    init(initialGain: Float = defaultInitialGain, isEnabled: Bool = true) {
        gain = Self.clamp(initialGain)
        self.isEnabled = isEnabled
        appliedGain = isEnabled ? gain : 1.0
    }

    /// 1フレーム分の統計を取り込み、このフレームへ適用するゲインを返す。
    mutating func observe(rms: Float, peak: Float) -> Float {
        // disabled 時は状態を一切変えず 1.0 素通し。
        guard isEnabled else { return 1.0 }
        // 非有限値で追跡状態を壊さない。
        guard rms.isFinite, peak.isFinite else { return appliedGain }

        let rms = max(0, rms)
        let peak = max(0, peak)

        let floored = max(rms, Self.noiseFloorMinimum)
        if let floor = noiseFloor, floored >= floor {
            noiseFloor = min(floor * Self.noiseFloorRise, floored)
        } else {
            noiseFloor = floored
        }

        let floor = noiseFloor ?? floored
        let noiseCap = Self.clamp(Self.noiseCeiling / floor)
        let isSpeech = rms >= Self.speechAbsoluteFloor && rms >= floor * Self.speechRatio

        if isSpeech {
            let desired = min(Self.clamp(Self.targetRms / rms), noiseCap)
            if desired > gain {
                gain = min(desired, gain * Self.gainRise)
            } else if desired < gain {
                gain = max(desired, gain * Self.gainFall)
            }
        } else if gain > noiseCap {
            // 持続するノイズ上昇では noiseCap まで徐々に下げる。
            gain = max(noiseCap, gain * Self.gainFall)
        }

        // クリック等の瞬間ピークはこのフレームの適用ゲインだけを下げる。
        let applied = peak > 0 ? min(gain, Self.clipCeiling / peak) : gain
        appliedGain = Self.clamp(applied)
        return appliedGain
    }

    /// 非有限サンプルを除いた有限サンプルの rms / peak を返す。
    /// 有限サンプルが 0 個なら (NaN, NaN)。
    static func frameStatistics(
        floatSamples: UnsafePointer<Float>,
        frameCount: Int
    ) -> (rms: Float, peak: Float) {
        var sum = 0.0
        var peak: Float = 0
        var count = 0
        for index in 0..<frameCount {
            let sample = floatSamples[index]
            guard sample.isFinite else { continue }
            sum += Double(sample) * Double(sample)
            peak = max(peak, abs(sample))
            count += 1
        }
        guard count > 0 else { return (.nan, .nan) }
        return (Float((sum / Double(count)).squareRoot()), peak)
    }

    /// previousAppliedGain → appliedGain へ先頭 rampSamples を線形ランプしながら PCM16 LE へ変換する。
    /// peak > 0 のとき各サンプルのゲインを clipCeiling/peak (下限 minimumGain) 以下に抑え、
    /// ランプ先頭でもクリップしない。
    static func encodePCM16(
        floatSamples: UnsafePointer<Float>,
        frameCount: Int,
        previousAppliedGain: Float,
        appliedGain: Float,
        peak: Float
    ) -> Data {
        let limit = peak > 0 ? max(Self.minimumGain, Self.clipCeiling / peak) : .infinity
        var data = Data(count: frameCount * 2)
        data.withUnsafeMutableBytes { rawBuffer in
            let output = rawBuffer.bindMemory(to: Int16.self)
            for index in 0..<frameCount {
                let ramp =
                    index < Self.rampSamples
                    ? previousAppliedGain
                        + (appliedGain - previousAppliedGain)
                        * (Float(index + 1) / Float(Self.rampSamples))
                    : appliedGain
                let gain = min(ramp, limit)
                output[index] = Self.encodeSample(floatSamples[index], gain: gain)
            }
        }
        return data
    }

    /// 統計 → observe → ランプ付き PCM16 化を1フレーム分行う。
    mutating func process(
        floatSamples: UnsafePointer<Float>,
        frameCount: Int
    ) -> Data {
        let statistics = Self.frameStatistics(floatSamples: floatSamples, frameCount: frameCount)
        let previous = appliedGain
        let applied = observe(rms: statistics.rms, peak: statistics.peak)
        return Self.encodePCM16(
            floatSamples: floatSamples,
            frameCount: frameCount,
            previousAppliedGain: previous,
            appliedGain: applied,
            peak: statistics.peak
        )
    }

    /// PCM16LittleEndianEncoder と同じスカラー変換規則をサンプル毎ゲインへ適用する。
    private static func encodeSample(_ sample: Float, gain: Float) -> Int16 {
        if sample.isNaN {
            return 0
        }
        let safeGain = gain.isFinite ? gain : 1
        let amplified = sample * safeGain
        if amplified.isNaN {
            // 例: Infinity * 0。trap を避けて無音にする。
            return 0
        }
        let clipped = max(-1.0 as Float, min(1.0 as Float, amplified))
        return Int16((clipped * Float(Int16.max)).rounded())
    }

    private static func clamp(_ value: Float) -> Float {
        guard value.isFinite else { return minimumGain }
        return min(maximumGain, max(minimumGain, value))
    }
}
