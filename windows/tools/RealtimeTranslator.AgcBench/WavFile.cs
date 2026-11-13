using System;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using RealtimeTranslator.Core.Audio;

namespace RealtimeTranslator.AgcBench;

/// <summary>24 kHz mono WAV の読み書き。PCM16 と IEEE float32 を読み、PCM16 で書く。</summary>
internal static class WavFile
{
    public const int RequiredSampleRate = 24000;

    private const string ConvertHint =
        "24 kHz mono の WAV が必要です。例: ffmpeg -i in.wav -ac 1 -ar 24000 -sample_fmt s16 out.wav";

    /// <summary>RIFF/WAVE (PCM16 または IEEE float32, mono, 24 kHz) を [-1,1] の float 列で返す。</summary>
    public static float[] Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 12
            || bytes[0] != 'R' || bytes[1] != 'I' || bytes[2] != 'F' || bytes[3] != 'F'
            || bytes[8] != 'W' || bytes[9] != 'A' || bytes[10] != 'V' || bytes[11] != 'E')
        {
            throw new InvalidDataException($"{path}: RIFF/WAVE ではありません。{ConvertHint}");
        }

        ushort? format = null;
        ushort? channels = null;
        uint? sampleRate = null;
        ushort? bitsPerSample = null;
        var dataOffset = -1;
        var dataLength = 0;

        var offset = 12;
        while (offset + 8 <= bytes.Length)
        {
            var id = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 4, 4));
            var body = offset + 8;
            if (body + size > bytes.Length)
            {
                break;
            }

            if (id == 0x20746D66 && size >= 16) // "fmt "
            {
                format = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(body, 2));
                channels = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(body + 2, 2));
                sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(body + 4, 4));
                bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(body + 14, 2));
            }
            else if (id == 0x61746164) // "data"
            {
                dataOffset = body;
                dataLength = size;
            }

            offset = body + size + (size % 2);
        }

        if (channels != 1 || sampleRate != RequiredSampleRate)
        {
            throw new InvalidDataException(
                $"{path}: 24 kHz mono の WAV が必要です (channels={Format(channels)}, rate={Format(sampleRate)})。{ConvertHint}"
            );
        }

        if (dataOffset < 0 || format is not (1 or 3))
        {
            throw new InvalidDataException($"{path}: PCM16 / float32 の data チャンクが見つかりません。{ConvertHint}");
        }

        if (format == 1)
        {
            if (bitsPerSample != 16)
            {
                throw new InvalidDataException($"{path}: PCM は 16 bit のみ対応します。{ConvertHint}");
            }

            var count = dataLength / 2;
            var samples = new float[count];
            for (var index = 0; index < count; index += 1)
            {
                var v = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(dataOffset + (index * 2), 2));
                samples[index] = Math.Max(-1f, v / (float)short.MaxValue);
            }

            return samples;
        }

        if (bitsPerSample != 32)
        {
            throw new InvalidDataException($"{path}: float WAV は 32 bit のみ対応します。{ConvertHint}");
        }

        var floatCount = dataLength / 4;
        var floatSamples = new float[floatCount];
        for (var index = 0; index < floatCount; index += 1)
        {
            floatSamples[index] = BinaryPrimitives.ReadSingleLittleEndian(
                bytes.AsSpan(dataOffset + (index * 4), 4)
            );
        }

        return floatSamples;
    }

    /// <summary>float サンプル列を PCM16 24 kHz mono WAV として書き出す。</summary>
    public static void Write(string path, ReadOnlySpan<float> samples)
    {
        var pcm = Pcm16LittleEndianEncoder.Encode(samples, 1f);
        WritePcm16(path, pcm);
    }

    /// <summary>エンコード済み PCM16 little-endian バイト列を 24 kHz mono WAV として書き出す。</summary>
    public static void WritePcm16(string path, byte[] pcm16LittleEndian)
    {
        var dataLength = pcm16LittleEndian.Length;
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        Span<byte> header = stackalloc byte[44];
        WriteAscii(header, 0, "RIFF");
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(4, 4), (uint)(36 + dataLength));
        WriteAscii(header, 8, "WAVE");
        WriteAscii(header, 12, "fmt ");
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(16, 4), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(20, 2), 1); // PCM
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(22, 2), 1); // mono
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(24, 4), RequiredSampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(28, 4), RequiredSampleRate * 2);
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(32, 2), 2); // block align
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(34, 2), 16); // bits
        WriteAscii(header, 36, "data");
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(40, 4), (uint)dataLength);
        stream.Write(header);
        stream.Write(pcm16LittleEndian);
    }

    private static string Format(ushort? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? "?";

    private static string Format(uint? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? "?";

    private static void WriteAscii(Span<byte> destination, int offset, string value)
    {
        for (var index = 0; index < value.Length; index += 1)
        {
            destination[offset + index] = (byte)value[index];
        }
    }
}
