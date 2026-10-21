using System;
using System.Collections.Generic;
using RealtimeTranslator.Core.Audio;

namespace RealtimeTranslator.AgcBench;

/// <summary>バリアント一覧。新しい候補はここに 1 ファイル追加して登録する。</summary>
internal static class GainVariants
{
    private static readonly Dictionary<string, Func<IGainVariant>> Registry = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        ["off"] = () => new OffVariant(),
        ["v1"] = () => new LegacyPeakGainVariant(),
        ["v2"] = () => new AgcV2Variant(),
    };

    public static IReadOnlyList<string> AllNames => ["off", "v1", "v2"];

    public static IGainVariant Create(string name)
    {
        if (!Registry.TryGetValue(name, out var factory))
        {
            throw new CliUsageException(
                $"unknown variant '{name}' (有効値: {string.Join(",", AllNames)})"
            );
        }

        return factory();
    }
}

/// <summary>ゲインなし。エンコーダを gain 1 で通すだけ。</summary>
internal sealed class OffVariant : IGainVariant
{
    public string Name => "off";
    public float Gain => 1f;
    public float AppliedGain => 1f;

    public byte[] ProcessFrame(ReadOnlySpan<float> frame) =>
        Pcm16LittleEndianEncoder.Encode(frame, 1f);
}

/// <summary>v1 契約の再現 (比較専用)。legacy ピーク追跡ゲインを同一フレームへ即適用する。</summary>
internal sealed class LegacyPeakGainVariant : IGainVariant
{
    private readonly LegacyPeakGain _gain = new();

    public string Name => "v1";
    public float Gain => _gain.Gain;
    public float AppliedGain => _gain.Gain;

    /// <summary>
    /// v1 の適用順序は `CapturedAudioFramePipeline` と同じく
    /// 「同じバッファで Observe した gain をそのバッファへ即適用」。
    /// 実機はプラットフォーム依存のチャンク長だったが、bench では 100 ms フレームで再生する。
    /// </summary>
    public byte[] ProcessFrame(ReadOnlySpan<float> frame)
    {
        var gain = _gain.Observe(frame);
        return Pcm16LittleEndianEncoder.Encode(frame, gain);
    }
}

/// <summary>v2 (現行)。既定 initial gain・有効状態の AdaptiveMicrophoneGain.Process。</summary>
internal sealed class AgcV2Variant : IGainVariant
{
    private readonly AdaptiveMicrophoneGain _agc = new();

    public string Name => "v2";
    public float Gain => _agc.Gain;
    public float AppliedGain => _agc.AppliedGain;

    public byte[] ProcessFrame(ReadOnlySpan<float> frame) => _agc.Process(frame);
}
