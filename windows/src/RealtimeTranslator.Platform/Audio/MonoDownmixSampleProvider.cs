using System;
using NAudio.Wave;

namespace RealtimeTranslator.Platform.Audio;

/// <summary>多チャンネル入力を平均でモノラルへ落とす。マイクの片チャンネルだけを拾って無音化するのを避ける。</summary>
public sealed class MonoDownmixSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _sourceChannels;
    private float[] _buffer = [];

    public MonoDownmixSampleProvider(ISampleProvider source)
    {
        ArgumentNullException.ThrowIfNull(source);

        _source = source;
        _sourceChannels = source.WaveFormat.Channels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        if (_sourceChannels == 1)
        {
            return _source.Read(buffer, offset, count);
        }

        var required = count * _sourceChannels;
        if (_buffer.Length < required)
        {
            _buffer = new float[required];
        }

        var read = _source.Read(_buffer, 0, required);
        var written = 0;
        for (var index = 0; index + _sourceChannels <= read; index += _sourceChannels)
        {
            var sum = 0f;
            for (var channel = 0; channel < _sourceChannels; channel++)
            {
                sum += _buffer[index + channel];
            }

            buffer[offset + written] = sum / _sourceChannels;
            written += 1;
        }

        return written;
    }
}
