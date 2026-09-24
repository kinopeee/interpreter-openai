using System;

namespace RealtimeTranslator.Core.Audio;

/// <summary>
/// 100 ms フレーム単位の適応マイクゲイン（shared/protocol/audio.md の v2 契約）。
/// 音量が変動する発話フレームの RMS だけで持続ゲインを動かし、雑音だけの区間では上げない。
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

    /// <summary>録音開始から <see cref="NoiseWindowFrames"/> フレームの間に仮定する雑音フロアの上限（約 -60 dBFS）。</summary>
    public const float StartupNoiseFloor = 0.001f;

    /// <summary>音量変動を見る窓（直近 500 ms）。</summary>
    public const int ModulationFrames = 5;

    /// <summary>変動窓の最大 RMS / 最小 RMS がこれ以上なら音量が変動している（約 6 dB）。</summary>
    public const float ModulationRatio = 2.0f;

    public const float GainRiseFactor = 1.12f;
    public const float GainFallFactor = 0.8f;

    /// <summary>フレーム内リミッタの目標ピーク。</summary>
    public const float ClipCeiling = 0.9f;

    /// <summary>適用ゲインの切り替えを線形に移すサンプル数（5 ms）。</summary>
    public const int RampSamples = 120;

    private readonly float[] _noiseWindow = new float[NoiseWindowFrames];
    private int _noiseWindowCount;
    private int _noiseWindowNext;
    private readonly float[] _modulationWindow = new float[ModulationFrames];
    private int _modulationWindowCount;
    private int _modulationWindowNext;
    private int _observedFrames;

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

        _observedFrames = Math.Min(_observedFrames + 1, NoiseWindowFrames + 1);
        if (rms >= DigitalSilenceRms)
        {
            Push(_noiseWindow, ref _noiseWindowCount, ref _noiseWindowNext, rms);
            Push(_modulationWindow, ref _modulationWindowCount, ref _modulationWindowNext, rms);
        }

        if (IsModulated() && rms >= SpeechAbsoluteFloor && rms >= NoiseFloor() * SpeechRatio)
        {
            // 発話フレームだけ持続ゲインを動かす。音量が一定の雑音だけの区間では上げない。
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

    /// <summary>
    /// ランプの開始ゲイン。リミッタで下げるフレームでは、先頭サンプルからピークを
    /// <see cref="ClipCeiling"/> 以下に抑えるため、前フレームの適用ゲインより低くする。
    /// </summary>
    public static float RampStartGain(float previousAppliedGain, float peak) =>
        peak > 0f ? Clamp(MathF.Min(previousAppliedGain, ClipCeiling / peak)) : previousAppliedGain;

    /// <summary>1 フレームのレベルを取り込み、ランプ付きでゲインを掛けた PCM16 LE を返す。</summary>
    public byte[] ProcessFrame(ReadOnlySpan<float> frame)
    {
        var previous = AppliedGain;
        var (rms, peak) = MeasureLevel(frame);
        var applied = ObserveLevel(rms, peak);
        var start = RampStartGain(previous, peak);
        return Pcm16LittleEndianEncoder.EncodeWithRamp(frame, start, applied, RampSamples);
    }

    private float NoiseFloor()
    {
        var floor = Min(_noiseWindow, _noiseWindowCount);
        // 録音開始直後は基準がないため、話し続けていても発話と判定できるよう上限を仮定する。
        return _observedFrames <= NoiseWindowFrames ? MathF.Min(floor, StartupNoiseFloor) : floor;
    }

    private bool IsModulated()
    {
        if (_modulationWindowCount < 2)
        {
            return false;
        }

        var maximum = 0f;
        for (var index = 0; index < _modulationWindowCount; index++)
        {
            maximum = MathF.Max(maximum, _modulationWindow[index]);
        }

        return maximum >= Min(_modulationWindow, _modulationWindowCount) * ModulationRatio;
    }

    private static float Min(float[] window, int count)
    {
        var minimum = float.PositiveInfinity;
        for (var index = 0; index < count; index++)
        {
            minimum = MathF.Min(minimum, window[index]);
        }

        return minimum;
    }

    private static void Push(float[] window, ref int count, ref int next, float rms)
    {
        window[next] = rms;
        next = (next + 1) % window.Length;
        count = Math.Min(count + 1, window.Length);
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
