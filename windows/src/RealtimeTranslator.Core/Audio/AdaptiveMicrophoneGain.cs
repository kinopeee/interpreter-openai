using System;

namespace RealtimeTranslator.Core.Audio;

/// <summary>
/// マイク入力のピークを追跡し、目標レベルへ近づける適応ゲイン。
/// feeder タスクから直列に呼ぶ前提。クリップ時は即減衰、静かな入力はゆっくり増幅する。
/// </summary>
public sealed class AdaptiveMicrophoneGain
{
    public const float MinimumGain = 1.0f;
    public const float MaximumGain = 8.0f;

    /// <summary>目標ピーク (約 -6 dBFS)。</summary>
    public const float TargetPeak = 0.5f;

    /// <summary>これ未満のピークは無音扱いとし、ゲインを上げない。</summary>
    public const float SilenceFloor = 0.005f;

    /// <summary>クリップとみなす増幅後ピーク。</summary>
    public const float ClipThreshold = 0.95f;

    public const float DefaultInitialGain = 4.0f;

    private float _trackedPeak;

    public AdaptiveMicrophoneGain(float initialGain = DefaultInitialGain) => Gain = Clamp(initialGain);

    public float Gain { get; private set; }

    /// <summary>float サンプルからピークを取り込み、次バッファ用のゲインを返す。</summary>
    public float Observe(ReadOnlySpan<float> floatSamples)
    {
        if (floatSamples.IsEmpty)
        {
            return Gain;
        }

        var peak = 0f;
        foreach (var sample in floatSamples)
        {
            peak = MathF.Max(peak, MathF.Abs(sample));
        }

        return ObservePeak(peak);
    }

    public float ObservePeak(float peak)
    {
        var nonNegativePeak = MathF.Max(0f, peak);

        // 減衰付きピーク追跡 (新しいピークは即反映、減衰は緩やか)。
        _trackedPeak = nonNegativePeak >= _trackedPeak
            ? nonNegativePeak
            : (_trackedPeak * 0.9f) + (nonNegativePeak * 0.1f);

        if (_trackedPeak * Gain >= ClipThreshold && _trackedPeak > 0f)
        {
            // Fast attack: クリップを即座に解消する。
            Gain = Clamp(MathF.Min(Gain, TargetPeak / _trackedPeak));
            return Gain;
        }

        if (_trackedPeak < SilenceFloor)
        {
            // 無音ではゲインを動かさない (暴騰防止)。
            return Gain;
        }

        var clampedDesired = Clamp(TargetPeak / _trackedPeak);
        if (clampedDesired > Gain)
        {
            // Slow release: 1 ステップあたり最大 5% まで上げる。
            Gain = Clamp(MathF.Min(clampedDesired, Gain * 1.05f));
        }
        else if (clampedDesired < Gain)
        {
            Gain = Clamp(MathF.Max(clampedDesired, Gain * 0.85f));
        }

        return Gain;
    }

    private static float Clamp(float value) => MathF.Min(MaximumGain, MathF.Max(MinimumGain, value));
}
