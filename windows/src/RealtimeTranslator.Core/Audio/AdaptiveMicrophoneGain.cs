using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;

namespace RealtimeTranslator.Core.Audio;

/// <summary>
/// マイク入力の RMS を追跡し、目標レベルへ近づける適応ゲイン (契約 v2)。
/// feeder タスクから直列に呼ぶ前提。100ms float フレームごとに <see cref="Process"/> を呼び、
/// フレーム内では適用ゲインをランプさせて不連続を防ぐ。クリップ limiter は
/// 当該フレームの適用ゲインだけを下げ、持続する <see cref="Gain"/> は変えない。
/// </summary>
public sealed class AdaptiveMicrophoneGain
{
    public const int FrameSamples = Pcm16FramePacketizer.SamplesPerFrame;
    public const float MinimumGain = 1.0f;
    public const float MaximumGain = 8.0f;

    /// <summary>shared/fixtures/v2/audio.json の defaultInitialGain と一致させる。</summary>
    public const float DefaultInitialGain = 4.0f;

    /// <summary>目標 RMS。</summary>
    public const float TargetRms = 0.1f;

    /// <summary>発話判定: rms が noiseFloor のこの倍以上なら発話とみなす。</summary>
    public const float SpeechRatio = 3.16f;

    /// <summary>発話判定: これ未満の rms は音量に関わらず非発話。</summary>
    public const float SpeechAbsoluteFloor = 0.003f;

    /// <summary>noiseFloor に比例して許容する増幅の天井。静かな環境での暴騰を防ぐ。</summary>
    public const float NoiseCeiling = 0.01f;

    /// <summary>noiseFloor の下限。デジタル無音 (rms=0) で noiseFloor が 0 に固定されないようにする。</summary>
    public const float NoiseFloorMinimum = 0.0001f;

    /// <summary>noiseFloor の推定に使う直近フレーム数。揃うまでフロアは未確定とし、noiseCap で下げない。</summary>
    public const int NoiseFloorWindowFrames = 30;

    /// <summary>フレームあたりのゲイン上昇上限。</summary>
    public const float GainRise = 1.12f;

    /// <summary>フレームあたりのゲイン下降上限。</summary>
    public const float GainFall = 0.8f;

    /// <summary>増幅後に許容するピークの天井。</summary>
    public const float ClipCeiling = 0.9f;

    /// <summary>適用ゲインを previous→current へ線形に遷移させる先頭サンプル数。</summary>
    public const int RampSamples = 120;

    private readonly List<float> _rmsHistory = [];

    public AdaptiveMicrophoneGain(float initialGain = DefaultInitialGain, bool isEnabled = true)
    {
        if (!float.IsFinite(initialGain))
        {
            throw new ArgumentOutOfRangeException(nameof(initialGain), "initialGain must be finite.");
        }

        Gain = Clamp(initialGain);
        IsEnabled = isEnabled;
        AppliedGain = isEnabled ? Gain : 1.0f;
    }

    public float Gain { get; private set; }

    /// <summary>直前フレームへ実際に掛けたゲイン。ランプの起点になる。</summary>
    public float AppliedGain { get; private set; }

    public bool IsEnabled { get; }

    /// <summary>1 フレーム分の統計を取り込み、このフレームへ適用するゲインを返す。</summary>
    public float Observe(float rms, float peak)
    {
        if (!IsEnabled)
        {
            // disabled 時は状態を一切変えず 1.0 素通し。
            return 1.0f;
        }

        if (!float.IsFinite(rms) || !float.IsFinite(peak))
        {
            // 非有限値で追跡状態を壊さない。
            return AppliedGain;
        }

        rms = MathF.Max(0f, rms);
        peak = MathF.Max(0f, peak);

        // 直近 30 フレームの floored RMS の最小値を noiseFloor とする。
        // 語間の無音が窓内にあれば発話中もフロアは低く保たれ、
        // 定常ノイズは窓が入れ替わる 3 秒以内にフロアへ反映される。
        var floored = MathF.Max(rms, NoiseFloorMinimum);
        _rmsHistory.Add(floored);
        if (_rmsHistory.Count > NoiseFloorWindowFrames)
        {
            _rmsHistory.RemoveAt(0);
        }

        var noiseFloor = _rmsHistory.Min();
        var confirmed = _rmsHistory.Count == NoiseFloorWindowFrames;
        var noiseCap = Clamp(NoiseCeiling / noiseFloor);
        var isSpeech = rms >= SpeechAbsoluteFloor && rms >= noiseFloor * SpeechRatio;

        if (isSpeech)
        {
            var desired = Clamp(TargetRms / rms);
            if (confirmed)
            {
                desired = MathF.Min(desired, noiseCap);
            }

            if (desired > Gain)
            {
                Gain = MathF.Min(desired, Gain * GainRise);
            }
            else if (desired < Gain)
            {
                Gain = MathF.Max(desired, Gain * GainFall);
            }
        }
        else if (confirmed && Gain > noiseCap)
        {
            // フロア確定後の持続ノイズでは noiseCap まで徐々に下げる。
            Gain = MathF.Max(noiseCap, Gain * GainFall);
        }

        // クリック等の瞬間ピークはこのフレームの適用ゲインだけを下げる。
        var applied = peak > 0f ? MathF.Min(Gain, ClipCeiling / peak) : Gain;
        AppliedGain = Clamp(applied);
        return AppliedGain;
    }

    /// <summary>非有限サンプルを除いた有限サンプルの rms / peak を返す。有限サンプルが 0 個なら (NaN, NaN)。</summary>
    public static (float Rms, float Peak) FrameStatistics(ReadOnlySpan<float> samples)
    {
        var sum = 0.0;
        var peak = 0f;
        var count = 0;
        foreach (var sample in samples)
        {
            if (!float.IsFinite(sample))
            {
                continue;
            }

            sum += (double)sample * sample;
            peak = MathF.Max(peak, MathF.Abs(sample));
            count += 1;
        }

        if (count == 0)
        {
            return (float.NaN, float.NaN);
        }

        return ((float)Math.Sqrt(sum / count), peak);
    }

    /// <summary>
    /// previousAppliedGain → appliedGain へ先頭 RampSamples を線形ランプしながら PCM16 LE へ変換する。
    /// peak > 0 のとき各サンプルのゲインを ClipCeiling/peak (下限 MinimumGain) 以下に抑え、ランプ先頭でもクリップしない。
    /// </summary>
    public static byte[] EncodePcm16(
        ReadOnlySpan<float> samples,
        float previousAppliedGain,
        float appliedGain,
        float peak
    )
    {
        var limit = peak > 0f ? MathF.Max(MinimumGain, ClipCeiling / peak) : float.PositiveInfinity;
        var data = new byte[samples.Length * 2];
        for (var index = 0; index < samples.Length; index += 1)
        {
            var ramp =
                index < RampSamples
                    ? previousAppliedGain + ((appliedGain - previousAppliedGain) * ((index + 1) / (float)RampSamples))
                    : appliedGain;
            var gain = MathF.Min(ramp, limit);
            BinaryPrimitives.WriteInt16LittleEndian(
                data.AsSpan(index * 2, 2),
                Pcm16LittleEndianEncoder.EncodeSample(samples[index], gain)
            );
        }

        return data;
    }

    /// <summary>統計 → observe → ランプ付き PCM16 化を 1 フレーム分行う。</summary>
    public byte[] Process(ReadOnlySpan<float> samples)
    {
        var (rms, peak) = FrameStatistics(samples);
        var previous = AppliedGain;
        var applied = Observe(rms, peak);
        return EncodePcm16(samples, previous, applied, peak);
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
