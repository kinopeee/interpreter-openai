using System;
using System.Collections.Generic;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using RealtimeTranslator.Core.Audio;

namespace RealtimeTranslator.Platform.Audio;

/// <summary>
/// デバイス形式の PCM を 24 kHz / mono / PCM16 の 100 ms frame へ変換する。
/// 供給が追いつかない分は無音で埋める。Realtime 側の VAD は無音 frame を必要とするため、間引かない。
/// </summary>
public sealed class CapturedAudioFramePipeline
{
    private readonly BufferedWaveProvider _buffered;
    private readonly ISampleProvider _resampled;
    private readonly AdaptiveMicrophoneGain _gain;
    private readonly Pcm16FramePacketizer _packetizer = new();
    private readonly object _sync = new();

    private float[] _readBuffer = [];

    public CapturedAudioFramePipeline(WaveFormat sourceFormat, AdaptiveMicrophoneGain? gain = null)
    {
        ArgumentNullException.ThrowIfNull(sourceFormat);

        _gain = gain ?? new AdaptiveMicrophoneGain();
        _buffered = new BufferedWaveProvider(sourceFormat)
        {
            BufferDuration = TimeSpan.FromSeconds(2),
            DiscardOnBufferOverflow = true,
            // 供給遅れは無音として読ませ、frame の時間軸を途切れさせない。
            ReadFully = true,
        };

        var samples = new MonoDownmixSampleProvider(_buffered.ToSampleProvider());
        _resampled = samples.WaveFormat.SampleRate == Pcm16FramePacketizer.SampleRate
            ? samples
            : new WdlResamplingSampleProvider(samples, Pcm16FramePacketizer.SampleRate);
    }

    public float CurrentGain => _gain.Gain;

    public void Push(byte[] deviceBytes, int count)
    {
        ArgumentNullException.ThrowIfNull(deviceBytes);

        if (count > 0)
        {
            _buffered.AddSamples(deviceBytes, 0, count);
        }
    }

    /// <summary>24 kHz 換算で <paramref name="sampleCount"/> サンプル分だけ読み進め、揃った frame を返す。</summary>
    public IReadOnlyList<byte[]> ReadFrames(int sampleCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleCount);

        lock (_sync)
        {
            if (_readBuffer.Length < sampleCount)
            {
                _readBuffer = new float[sampleCount];
            }

            var read = _resampled.Read(_readBuffer, 0, sampleCount);
            if (read <= 0)
            {
                return [];
            }

            var samples = _readBuffer.AsSpan(0, read);
            var gain = _gain.Observe(samples);
            return _packetizer.Append(Pcm16LittleEndianEncoder.Encode(samples, gain));
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
}
