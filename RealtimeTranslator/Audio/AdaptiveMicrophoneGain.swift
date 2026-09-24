import Foundation

/// 100 ms フレーム単位の適応マイクゲイン（shared/protocol/audio.md の v2 契約）。
///
/// 音量が変動する発話フレームの RMS だけで持続ゲインを動かし、雑音だけの区間では上げない。
/// クリップ防止のリミッタはそのフレームだけに効き、持続ゲインを変えない。
/// feederタスクから直列に呼ぶ前提。
struct AdaptiveMicrophoneGain: Sendable {
    static let frameSamples = PCM16FramePacketizer.samplesPerFrame
    static let minimumGain: Float = 1.0
    static let maximumGain: Float = 8.0
    /// shared/fixtures の defaultInitialGain と一致させる。
    static let defaultInitialGain: Float = 4.0
    /// 発話フレームの目標 RMS（約 -20 dBFS）。
    static let targetRms: Float = 0.1
    /// 雑音フロアからこの倍率（約 +10 dB）以上なら発話とみなす。
    static let speechRatio: Float = 3.16
    /// これ未満の RMS は発話にしない（約 -50 dBFS）。
    static let speechAbsoluteFloor: Float = 0.003
    /// これ未満の RMS はデジタル無音として雑音窓へ入れない。
    static let digitalSilenceRms: Float = 0.00001
    /// 雑音フロアを求める窓（直近 3 秒）。
    static let noiseWindowFrames = 30
    /// 録音開始から noiseWindowFrames フレームの間に仮定する雑音フロアの上限（約 -60 dBFS）。
    static let startupNoiseFloor: Float = 0.001
    /// 音量変動を見る窓（直近 500 ms）。
    static let modulationFrames = 5
    /// 変動窓の最大 RMS / 最小 RMS がこれ以上なら音量が変動している（約 6 dB）。
    static let modulationRatio: Float = 2.0
    static let gainRiseFactor: Float = 1.12
    static let gainFallFactor: Float = 0.8
    /// フレーム内リミッタの目標ピーク。
    static let clipCeiling: Float = 0.9
    /// 適用ゲインの切り替えを線形に移すサンプル数（5 ms）。
    static let rampSamples = 120

    let isEnabled: Bool
    /// 発話フレームだけで動く持続ゲイン。
    private(set) var gain: Float
    /// 直近フレームに適用したゲイン（リミッタ適用後）。
    private(set) var appliedGain: Float
    private var noiseWindow: [Float] = []
    private var noiseWindowNext = 0
    private var modulationWindow: [Float] = []
    private var modulationWindowNext = 0
    private var observedFrames = 0

    init(initialGain: Float = defaultInitialGain, isEnabled: Bool = true) {
        self.isEnabled = isEnabled
        gain = isEnabled ? Self.clamp(initialGain) : Self.minimumGain
        appliedGain = gain
        noiseWindow.reserveCapacity(Self.noiseWindowFrames)
        modulationWindow.reserveCapacity(Self.modulationFrames)
    }

    /// 有限なサンプルだけから RMS とピークを求める。有限なサンプルが無ければ 0。
    static func measureLevel(_ samples: UnsafeBufferPointer<Float>) -> (rms: Float, peak: Float) {
        var sumOfSquares = 0.0
        var count = 0
        var peak: Float = 0
        for sample in samples where sample.isFinite {
            sumOfSquares += Double(sample) * Double(sample)
            count += 1
            peak = max(peak, abs(sample))
        }
        guard count > 0 else { return (0, 0) }
        return (Float((sumOfSquares / Double(count)).squareRoot()), peak)
    }

    /// 1 フレームの RMS とピークを取り込み、このフレームに適用するゲインを返す。
    mutating func observe(rms: Float, peak: Float) -> Float {
        guard isEnabled else { return Self.minimumGain }
        // 非有限値で状態を壊さない。
        guard rms.isFinite, peak.isFinite else { return appliedGain }

        let rms = max(0, rms)
        let peak = max(0, peak)

        observedFrames = min(observedFrames + 1, Self.noiseWindowFrames + 1)
        if rms >= Self.digitalSilenceRms {
            Self.push(rms, into: &noiseWindow, next: &noiseWindowNext, capacity: Self.noiseWindowFrames)
            Self.push(
                rms,
                into: &modulationWindow,
                next: &modulationWindowNext,
                capacity: Self.modulationFrames
            )
        }

        if isModulated, rms >= Self.speechAbsoluteFloor, rms >= noiseFloor * Self.speechRatio {
            // 発話フレームだけ持続ゲインを動かす。音量が一定の雑音だけの区間では上げない。
            let desired = Self.clamp(Self.targetRms / rms)
            if desired > gain {
                gain = Self.clamp(min(desired, gain * Self.gainRiseFactor))
            } else if desired < gain {
                gain = Self.clamp(max(desired, gain * Self.gainFallFactor))
            }
        }

        // リミッタはこのフレームだけに効かせ、持続ゲインは変えない。
        let applied = peak > 0 ? min(gain, Self.clipCeiling / peak) : gain
        appliedGain = Self.clamp(applied)
        return appliedGain
    }

    /// ランプの開始ゲイン。リミッタで下げるフレームでは、先頭サンプルからピークを
    /// clipCeiling 以下に抑えるため、前フレームの適用ゲインより低くする。
    static func rampStartGain(previous: Float, peak: Float) -> Float {
        peak > 0 ? clamp(min(previous, clipCeiling / peak)) : previous
    }

    /// 1 フレームのレベルを取り込み、ランプ付きでゲインを掛けた PCM16 LE を返す。
    mutating func process(frame: UnsafeBufferPointer<Float>) -> Data {
        let previous = appliedGain
        let level = Self.measureLevel(frame)
        let applied = observe(rms: level.rms, peak: level.peak)
        return PCM16LittleEndianEncoder.encode(
            floatSamples: frame,
            fromGain: Self.rampStartGain(previous: previous, peak: level.peak),
            toGain: applied,
            rampSamples: Self.rampSamples
        )
    }

    private var noiseFloor: Float {
        let floor = noiseWindow.min() ?? .infinity
        // 録音開始直後は基準がないため、話し続けていても発話と判定できるよう上限を仮定する。
        return observedFrames <= Self.noiseWindowFrames ? min(floor, Self.startupNoiseFloor) : floor
    }

    private var isModulated: Bool {
        guard modulationWindow.count >= 2,
            let minimum = modulationWindow.min(),
            let maximum = modulationWindow.max()
        else { return false }
        return maximum >= minimum * Self.modulationRatio
    }

    private static func push(_ rms: Float, into window: inout [Float], next: inout Int, capacity: Int) {
        if window.count < capacity {
            window.append(rms)
        } else {
            window[next] = rms
        }
        next = (next + 1) % capacity
    }

    private static func clamp(_ value: Float) -> Float {
        guard value.isFinite else { return minimumGain }
        return min(maximumGain, max(minimumGain, value))
    }
}
