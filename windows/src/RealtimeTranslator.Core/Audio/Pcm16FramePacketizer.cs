using System;
using System.Collections.Generic;

namespace RealtimeTranslator.Core.Audio;

/// <summary>24 kHz PCM16 mono little-endian を 100 ms 単位へ分割する。feeder タスクから直列に呼ぶ。</summary>
public sealed class Pcm16FramePacketizer
{
    public const int SampleRate = 24_000;
    public const int BytesPerSample = 2;
    public const int FrameDurationMilliseconds = 100;
    public const int SamplesPerFrame = SampleRate * FrameDurationMilliseconds / 1_000;
    public const int BytesPerFrame = SamplesPerFrame * BytesPerSample;

    private readonly List<byte> _pending = new(BytesPerFrame * 2);

    public int PendingByteCount => _pending.Count;

    public IReadOnlyList<byte[]> Append(ReadOnlySpan<byte> pcm16LittleEndian)
    {
        if (pcm16LittleEndian.IsEmpty)
        {
            return Array.Empty<byte[]>();
        }

        _pending.AddRange(pcm16LittleEndian);
        if (_pending.Count < BytesPerFrame)
        {
            return Array.Empty<byte[]>();
        }

        var frames = new List<byte[]>(_pending.Count / BytesPerFrame);
        var consumed = 0;
        while (_pending.Count - consumed >= BytesPerFrame)
        {
            var frame = new byte[BytesPerFrame];
            _pending.CopyTo(consumed, frame, 0, BytesPerFrame);
            frames.Add(frame);
            consumed += BytesPerFrame;
        }

        _pending.RemoveRange(0, consumed);
        return frames;
    }

    /// <summary>正常停止時に端数を無音 padding して最後の 1 frame を返す。端数が無ければ null。</summary>
    public byte[]? FlushWithSilencePadding()
    {
        if (_pending.Count == 0)
        {
            return null;
        }

        var frame = new byte[BytesPerFrame];
        _pending.CopyTo(0, frame, 0, Math.Min(_pending.Count, BytesPerFrame));
        _pending.Clear();
        return frame;
    }

    public void Reset() => _pending.Clear();
}
