using System;

namespace RealtimeTranslator.Core.Audio;

/// <summary>
/// PCM16 little-endian mono frame のピーク振幅（0.0〜1.0）。
/// 音声活動の閾値判定だけに使い、音声データ自体は保持・記録しない。
/// </summary>
public static class Pcm16AudioActivity
{
    public static double NormalizedPeakAmplitude(ReadOnlySpan<byte> pcm16LittleEndian)
    {
        var peak = 0;
        var sampleCount = pcm16LittleEndian.Length / 2;
        for (var index = 0; index < sampleCount; index++)
        {
            var value = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(
                pcm16LittleEndian.Slice(index * 2, 2)
            );
            var magnitude = value == short.MinValue ? 32768 : Math.Abs((int)value);
            peak = Math.Max(peak, magnitude);
        }

        return Math.Min(peak / 32767.0, 1.0);
    }
}
