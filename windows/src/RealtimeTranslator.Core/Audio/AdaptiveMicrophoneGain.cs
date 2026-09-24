using System;

namespace RealtimeTranslator.Core.Audio;

/// <summary>
/// 100 ms フレーム単位の適応マイクゲイン（shared/protocol/audio.md の v2 契約）。
/// 発話フレームの RMS だけで持続ゲインを動かし、雑音だけの区間では上げない。
/// クリップ防止のリミッタはそのフレームだけに効き、持続ゲインを変えない。
/// feeder から直列に呼ぶ前提。
/// </summary>
public sealed class AdaptiveMicrophoneGain
{
    public const int FrameSamples = Pcm16FramePacketizer.SamplesPerFrame;
    public const float MinimumGain = 1.0f;
    public const float MaximumGain = 8.0f;
    public const float DefaultInitialGain = 4.0f;

    /// <summary>発話フレームの目標 RMS（約 -20 dBFS）。</summary>
    public const float TargetRms = 0.1f;

    /// <summary>雑音フロアからこの倍率（約 +10 dB）以上なら発話とみなす。</summary>
    public const float SpeechRatio = 3.16f;

    /// <summary>これ未満の RMS は発話にしない（約 -50 dBFS）。</summary>
    public const float SpeechAbsoluteFloor = 0.003f;

    /// <summary>これ未満の RMS はデジタル無音として雑音窓へ入れない。</summary>
    public const float DigitalSilenceRms = 0.00001f;

    /// <summary>雑音フロアを求める窓（直近 3 秒）。</summary>
    public const int NoiseWindowFrames = 30;

    public const float GainRiseFactor = 1.12f;
    public const float GainFallFactor = 0.8f;

    /// <summary>フレーム内リミッタの目標ピーク。</summary>
    public const float ClipCeiling = 0.9f;

    /// <summary>適用ゲインの切り替えを線形に移すサンプル数（5 ms）。</summary>
    public const int RampSamples = 120;

    private readonly float[] _noiseWindow = new float[NoiseWindowFrames];
    private int _noiseWindowCount;
    private int _noiseWindowNext;

    public AdaptiveMicrophoneGain(float initialGain = DefaultInitialGain, bool isEnabled = true)
    {
        if (!float.IsFinite(initialGain))
        {
            throw new ArgumentOutOfRangeException(nameof(initialGain), "initialGain must be finite.");
        }

        IsEnabled = isEnabled;
        Gain = isEnabled ? Clamp(initialGain) : MinimumGain;
        AppliedGain = Gain;
    }

    public bool IsEnabled { get; }

    /// <summary>発話フレームだけで動く持続ゲイン。</summary>
    public float Gain { get; private set; }

    /// <summary>直近フレームに適用したゲイン（リミッタ適用後）。</summary>
    public float AppliedGain { get; private set; }

    /// <summary>有限なサンプルだけから RMS とピークを求める。有限なサンプルが無ければ 0。</summary>
    public static (float Rms, float Peak) MeasureLevel(ReadOnlySpan<float> samples)
    {
        var sumOfSquares = 0.0;
        var count = 0;
        var peak = 0f;
        foreach (var sample in samples)
        {
            if (!float.IsFinite(sample))
            {
                continue;
            }

            sumOfSquares += (double)sample * sample;
            count++;
            peak = MathF.Max(peak, MathF.Abs(sample));
        }

        return count == 0 ? (0f, 0f) : ((float)Math.Sqrt(sumOfSquares / count), peak);
    }

    /// <summary>1 フレームの RMS とピークを取り込み、このフレームに適用するゲインを返す。</summary>
    public float ObserveLevel(float rms, float peak)
    {
        if (!IsEnabled)
        {
            return MinimumGain;
        }

        if (!float.IsFinite(rms) || !float.IsFinite(peak))
        {
            // 非有限値で状態を壊さない。
            return AppliedGain;
        }

        rms = MathF.Max(0f, rms);
        peak = MathF.Max(0f, peak);

        if (rms >= DigitalSilenceRms)
        {
            PushNoiseWindow(rms);
        }

        if (_noiseWindowCount > 0 && rms >= SpeechAbsoluteFloor && rms >= NoiseFloor() * SpeechRatio)
        {
            // 発話フレームだけ持続ゲインを動かす。雑音だけの区間では上げない。
            var desired = Clamp(TargetRms / rms);
            if (desired > Gain)
            {
                Gain = Clamp(MathF.Min(desired, Gain * GainRiseFactor));
            }
            else if (desired < Gain)
            {
                Gain = Clamp(MathF.Max(desired, Gain * GainFallFactor));
            }
        }

        // リミッタはこのフレームだけに効かせ、持続ゲインは変えない。
        var applied = peak > 0f ? MathF.Min(Gain, ClipCeiling / peak) : Gain;
        AppliedGain = Clamp(applied);
        return AppliedGain;
    }

    /// <summary>1 フレームのレベルを取り込み、ランプ付きでゲインを掛けた PCM16 LE を返す。</summary>
    public byte[] ProcessFrame(ReadOnlySpan<float> frame)
    {
        var previous = AppliedGain;
        var (rms, peak) = MeasureLevel(frame);
        var applied = ObserveLevel(rms, peak);
        return Pcm16LittleEndianEncoder.EncodeWithRamp(frame, previous, applied, RampSamples);
    }

    private float NoiseFloor()
    {
        var floor = float.PositiveInfinity;
        for (var index = 0; index < _noiseWindowCount; index++)
        {
            floor = MathF.Min(floor, _noiseWindow[index]);
        }

        return floor;
    }

    private void PushNoiseWindow(float rms)
    {
        _noiseWindow[_noiseWindowNext] = rms;
        _noiseWindowNext = (_noiseWindowNext + 1) % NoiseWindowFrames;
        _noiseWindowCount = Math.Min(_noiseWindowCount + 1, NoiseWindowFrames);
    }

    private static float Clamp(float value)
    {
        if (!float.IsFinite(value))
        {
            return MinimumGain;
        }

        return MathF.Min(MaximumGain, MathF.Max(MinimumGain, value));
    }
}
