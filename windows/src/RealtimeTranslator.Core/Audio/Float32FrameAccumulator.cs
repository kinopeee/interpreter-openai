using System;
using System.Collections.Generic;

namespace RealtimeTranslator.Core.Audio;

/// <summary>
/// 24 kHz mono Float32 を 100 ms（2,400 samples）単位へ分割する。
/// 適応ゲインをフレーム単位で決めるため、PCM16 変換の前に使う。feeder から直列に呼ぶ。
/// </summary>
public sealed class Float32FrameAccumulator
{
    public const int SamplesPerFrame = Pcm16FramePacketizer.SamplesPerFrame;

    private readonly float[] _pending = new float[SamplesPerFrame];
    private int _pendingCount;

    public int PendingSampleCount => _pendingCount;

    public IReadOnlyList<float[]> Append(ReadOnlySpan<float> samples)
    {
        List<float[]>? frames = null;
        while (!samples.IsEmpty)
        {
            var take = Math.Min(SamplesPerFrame - _pendingCount, samples.Length);
            samples[..take].CopyTo(_pending.AsSpan(_pendingCount));
            _pendingCount += take;
            samples = samples[take..];

            if (_pendingCount == SamplesPerFrame)
            {
                frames ??= new List<float[]>(1);
                frames.Add((float[])_pending.Clone());
                _pendingCount = 0;
            }
        }

        return frames ?? (IReadOnlyList<float[]>)Array.Empty<float[]>();
    }

    /// <summary>正常停止時に端数を無音 padding して最後の 1 frame を返す。端数が無ければ null。</summary>
    public float[]? FlushWithSilencePadding()
    {
        if (_pendingCount == 0)
        {
            return null;
        }

        var frame = new float[SamplesPerFrame];
        _pending.AsSpan(0, _pendingCount).CopyTo(frame);
        _pendingCount = 0;
        return frame;
    }

    public void Reset() => _pendingCount = 0;
}
