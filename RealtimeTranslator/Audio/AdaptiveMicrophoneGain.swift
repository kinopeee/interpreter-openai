import Foundation

/// マイク入力のピークを追跡し、目標レベルへ近づける適応ゲイン。
///
/// feederタスクから直列に呼ぶ前提。クリップ時は即減衰、静かな入力はゆっくり増幅する。
struct AdaptiveMicrophoneGain: Sendable {
    static let minimumGain: Float = 1.0
    static let maximumGain: Float = 8.0
    /// 目標ピーク (約 -6 dBFS)。
    static let targetPeak: Float = 0.5
    /// これ未満のピークは無音扱いとし、ゲインを上げない。
    static let silenceFloor: Float = 0.005
    /// クリップとみなす増幅後ピーク。
    static let clipThreshold: Float = 0.95

    private(set) var gain: Float
    private var trackedPeak: Float

    init(initialGain: Float = 4.0) {
        gain = Self.clamp(initialGain)
        trackedPeak = 0
    }

    /// floatサンプルからピークを取り込み、次バッファ用のゲインを返す。
    mutating func observe(floatSamples: UnsafePointer<Float>, frameCount: Int) -> Float {
        guard frameCount > 0 else { return gain }

        var peak: Float = 0
        for index in 0..<frameCount {
            peak = max(peak, abs(floatSamples[index]))
        }
        return observePeak(peak)
    }

    /// テスト用: 生ピークを直接渡してゲインを更新する。
    mutating func observePeak(_ peak: Float) -> Float {
        let nonNegativePeak = max(0, peak)
        // 減衰付きピーク追跡 (新しいピークは即反映、減衰は緩やか)。
        if nonNegativePeak >= trackedPeak {
            trackedPeak = nonNegativePeak
        } else {
            trackedPeak = trackedPeak * 0.9 + nonNegativePeak * 0.1
        }

        let amplifiedPeak = trackedPeak * gain
        if amplifiedPeak >= Self.clipThreshold, trackedPeak > 0 {
            // Fast attack: クリップを即座に解消する。
            let desired = Self.targetPeak / trackedPeak
            gain = Self.clamp(min(gain, desired))
            return gain
        }

        guard trackedPeak >= Self.silenceFloor else {
            // 無音ではゲインを動かさない (暴騰防止)。
            return gain
        }

        let desired = Self.targetPeak / trackedPeak
        let clampedDesired = Self.clamp(desired)
        if clampedDesired > gain {
            // Slow release: 1ステップあたり最大5%まで上げる。
            gain = Self.clamp(min(clampedDesired, gain * 1.05))
        } else if clampedDesired < gain {
            gain = Self.clamp(max(clampedDesired, gain * 0.85))
        }
        return gain
    }

    private static func clamp(_ value: Float) -> Float {
        min(maximumGain, max(minimumGain, value))
    }
}
