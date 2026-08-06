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

    /// <summary>クリップしてから Int16.MaxValue 倍し、0 から遠い側へ四捨五入する。</summary>
    public static short EncodeSample(float sample, float gain)
    {
        var clipped = Math.Clamp(sample * gain, -1f, 1f);
        return (short)MathF.Round(clipped * short.MaxValue, MidpointRounding.AwayFromZero);
    }
}
