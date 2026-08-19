using System;
using System.Collections.Generic;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using RealtimeTranslator.Core.Audio;

namespace RealtimeTranslator.Platform.Audio;

/// <summary>
/// デバイス形式の PCM を 24 kHz / mono / PCM16 の 100 ms frame へ変換する。
/// 供給が追いつかない分は無音で埋める。Realtime 側の VAD は無音 frame を必要とするため、間引かない。
/// 端数の実音声に無音を混ぜて 100ms へ強制しない。遅延が溜まったら oldest を捨てて最新を残す。
/// </summary>
public sealed class CapturedAudioFramePipeline
{
    private const int MaxFramesPerTick = 32;

    private readonly BufferedWaveProvider _buffered;
    private readonly ISampleProvider _resampled;
    private readonly AdaptiveMicrophoneGain _gain;
    private readonly Pcm16FramePacketizer _packetizer = new();
    private readonly object _sync = new();

    private float[] _readBuffer = [];
    private byte[] _overflowDiscard = [];

    public CapturedAudioFramePipeline(WaveFormat sourceFormat, AdaptiveMicrophoneGain? gain = null)
    {
        ArgumentNullException.ThrowIfNull(sourceFormat);

        _gain = gain ?? new AdaptiveMicrophoneGain();
        _buffered = new BufferedWaveProvider(sourceFormat)
        {
            BufferDuration = TimeSpan.FromSeconds(2),
            DiscardOnBufferOverflow = true,
            // 不足分を 0 埋めすると、後から届く実音声と無音が二重に時間軸へ載る。
            ReadFully = false,
        };

        var samples = new MonoDownmixSampleProvider(_buffered.ToSampleProvider());
        _resampled = samples.WaveFormat.SampleRate == Pcm16FramePacketizer.SampleRate
            ? samples
            : new WdlResamplingSampleProvider(samples, Pcm16FramePacketizer.SampleRate);
    }

    public float CurrentGain => _gain.Gain;

    /// <summary>packetizer 端数または未変換のデバイスバイトが残っている。</summary>
    public bool HasUnsentAudio
    {
        get
        {
            lock (_sync)
            {
                return HasUnsentAudioLocked();
            }
        }
    }

    public void Push(byte[] deviceBytes, int count)
    {
        ArgumentNullException.ThrowIfNull(deviceBytes);

        if (count <= 0)
        {
            return;
        }

        lock (_sync)
        {
            var capacity = _buffered.BufferLength;
            if (capacity <= 0)
            {
                return;
            }

            if (count >= capacity)
            {
                // 1 チャンクがバッファ全体以上なら、最新の capacity バイトだけ残す。
                _buffered.ClearBuffer();
                _buffered.AddSamples(deviceBytes, count - capacity, capacity);
                return;
            }

            var overflow = _buffered.BufferedBytes + count - capacity;
            if (overflow > 0)
            {
                DiscardOldestBytesLocked(overflow);
            }

            _buffered.AddSamples(deviceBytes, 0, count);
        }
    }

    /// <summary>
    /// 24 kHz 換算で揃った 100ms frame だけを返す。端数は内部に残し、無音 padding しない。
    /// 完全な飢餓（端数もデバイスバイトも無し）のときは空。
    /// </summary>
    public IReadOnlyList<byte[]> ReadFrames(int sampleCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleCount);

        lock (_sync)
        {
            return ReadFramesLocked(sampleCount);
        }
    }

    /// <summary>
    /// 100ms pump tick。溜まっている complete frame をすべて返し、完全飢餓のときだけ無音 1 frame。
    /// packetizer 端数があるときは空を返し、実音声へ無音を混ぜない。
    /// </summary>
    public IReadOnlyList<byte[]> TakeTickFrames(int sampleCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleCount);

        lock (_sync)
        {
            List<byte[]>? frames = null;
            // 2 秒キャプチャバッファでも 20 frame。送信 channel 容量 32 を超えさせない。
            while ((frames?.Count ?? 0) < MaxFramesPerTick)
            {
                var batch = ReadFramesLocked(sampleCount);
                if (batch.Count == 0)
                {
                    break;
                }

                frames ??= new List<byte[]>(batch.Count);
                frames.AddRange(batch);
            }

            if (frames is { Count: > 0 })
            {
                return frames;
            }

            if (HasUnsentAudioLocked())
            {
                return Array.Empty<byte[]>();
            }

            return [new byte[Pcm16FramePacketizer.BytesPerFrame]];
        }
    }

    public void Reset()
    {
        lock (_sync)
        {
            _buffered.ClearBuffer();
            _packetizer.Reset();
        }
    }

    private IReadOnlyList<byte[]> ReadFramesLocked(int sampleCount)
    {
        if (_readBuffer.Length < sampleCount)
        {
            _readBuffer = new float[sampleCount];
        }

        var read = _resampled.Read(_readBuffer, 0, sampleCount);
        if (read <= 0)
        {
            return Array.Empty<byte[]>();
        }

        var samples = _readBuffer.AsSpan(0, read);
        var gain = _gain.Observe(samples);
        return _packetizer.Append(Pcm16LittleEndianEncoder.Encode(samples, gain));
    }

    private bool HasUnsentAudioLocked() =>
        _packetizer.PendingByteCount > 0 || _buffered.BufferedBytes > 0;

    private void DiscardOldestBytesLocked(int byteCount)
    {
        if (byteCount <= 0)
        {
            return;
        }

        if (_overflowDiscard.Length < byteCount)
        {
            _overflowDiscard = new byte[byteCount];
        }

        _ = _buffered.Read(_overflowDiscard, 0, byteCount);
    }
}
