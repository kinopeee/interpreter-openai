using System;
using System.Collections.Generic;

namespace RealtimeTranslator.Core.Audio;

/// <summary>
/// 変換後の float サンプル列を 2,400 サンプル (100ms) フレームへ分割する。
/// AGC がフレーム単位の統計を使うため、PCM16 化の前段に置く。feeder タスクから直列に呼ぶ。
/// </summary>
public sealed class Float32FrameAccumulator
{
    public static int FrameSamples => Pcm16FramePacketizer.SamplesPerFrame;

    private readonly List<float> _pending = new(Pcm16FramePacketizer.SamplesPerFrame * 2);

    public int PendingSampleCount => _pending.Count;

    public IReadOnlyList<float[]> Append(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty)
        {
            return Array.Empty<float[]>();
        }

        _pending.AddRange(samples);
        if (_pending.Count < Pcm16FramePacketizer.SamplesPerFrame)
        {
            return Array.Empty<float[]>();
        }

        var frames = new List<float[]>(_pending.Count / Pcm16FramePacketizer.SamplesPerFrame);
        var consumed = 0;
        while (_pending.Count - consumed >= Pcm16FramePacketizer.SamplesPerFrame)
        {
            var frame = new float[Pcm16FramePacketizer.SamplesPerFrame];
            _pending.CopyTo(consumed, frame, 0, Pcm16FramePacketizer.SamplesPerFrame);
            frames.Add(frame);
            consumed += Pcm16FramePacketizer.SamplesPerFrame;
        }

        _pending.RemoveRange(0, consumed);
        return frames;
    }

    /// <summary>正常停止時に端数を 0.0 padding して 2,400 サンプルの 1 フレームを返す。端数が無ければ null。</summary>
    public float[]? FlushWithSilencePadding()
    {
        if (_pending.Count == 0)
        {
            return null;
        }

        var frame = new float[Pcm16FramePacketizer.SamplesPerFrame];
        _pending.CopyTo(0, frame, 0, _pending.Count);
        _pending.Clear();
        return frame;
    }

    public void Reset() => _pending.Clear();
}
