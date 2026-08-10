using System;
using System.Buffers.Binary;

namespace RealtimeTranslator.Core.Audio;

/// <summary>Float32 mono buffer を PCM16 little-endian へ変換する。</summary>
public static class Pcm16LittleEndianEncoder
{
    public static byte[] Encode(ReadOnlySpan<float> floatSamples, float gain = 1f)
    {
        var data = new byte[floatSamples.Length * 2];
        Encode(floatSamples, data, gain);
        return data;
    }

    public static void Encode(ReadOnlySpan<float> floatSamples, Span<byte> destination, float gain = 1f)
    {
        if (destination.Length < floatSamples.Length * 2)
        {
            throw new ArgumentException("destination is too small", nameof(destination));
        }

        for (var index = 0; index < floatSamples.Length; index++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(
                destination.Slice(index * 2, 2),
                EncodeSample(floatSamples[index], gain));
        }
    }

    /// <summary>
    /// クリップしてから Int16.MaxValue 倍し、0 から遠い側へ四捨五入する。
    /// NaN は無音 (0)。macOS の <c>Int16(Float.nan)</c> trap を避ける契約と揃える。
    /// ±Infinity は従来どおり ±1 へクリップする。
    /// </summary>
    public static short EncodeSample(float sample, float gain)
    {
        if (float.IsNaN(sample))
        {
            return 0;
        }

        var safeGain = float.IsFinite(gain) ? gain : 1f;
        var amplified = sample * safeGain;
        if (float.IsNaN(amplified))
        {
            // 例: Infinity * 0。trap/不正な cast を避けて無音にする。
            return 0;
        }

        var clipped = Math.Clamp(amplified, -1f, 1f);
        return (short)MathF.Round(clipped * short.MaxValue, MidpointRounding.AwayFromZero);
    }
}
