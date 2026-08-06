using System;
using NAudio.Wave;
using RealtimeTranslator.Core.Audio;
using RealtimeTranslator.Platform.Audio;
using Xunit;

namespace RealtimeTranslator.Platform.Tests;

/// <summary>デバイス形式から 24kHz / mono / PCM16 / 100ms frame へ落とす契約。</summary>
public sealed class CapturedAudioFramePipelineTests
{
    // Given: 既に 24kHz mono の入力
    // When: 100ms 分を投入して読み出す
    // Then: 4,800 バイトちょうどの frame が 1 つ出る
    [Fact]
    public void ProducesExactlyOneFramePerHundredMilliseconds()
    {
        var pipeline = new CapturedAudioFramePipeline(new WaveFormat(Pcm16FramePacketizer.SampleRate, 16, 1));

        pipeline.Push(SineWave(Pcm16FramePacketizer.SamplesPerFrame, 1), Pcm16FramePacketizer.BytesPerFrame);
        var frames = pipeline.ReadFrames(Pcm16FramePacketizer.SamplesPerFrame);

        Assert.Single(frames);
        Assert.Equal(Pcm16FramePacketizer.BytesPerFrame, frames[0].Length);
    }

    // Given: 48kHz ステレオのデバイス入力
    // When: 200ms 分を投入して 200ms 分読み出す
    // Then: 24kHz mono に変換され 100ms frame が 2 つ出る
    [Fact]
    public void ResamplesAndDownmixesDeviceAudio()
    {
        var pipeline = new CapturedAudioFramePipeline(new WaveFormat(48_000, 16, 2));
        var samplesPerChannel = 48_000 / 5;

        pipeline.Push(SineWave(samplesPerChannel * 2, 2), samplesPerChannel * 2 * 2);
        var frames = pipeline.ReadFrames(Pcm16FramePacketizer.SamplesPerFrame * 2);

        Assert.Equal(2, frames.Count);
        Assert.All(frames, frame => Assert.Equal(Pcm16FramePacketizer.BytesPerFrame, frame.Length));
    }

    // Given: デバイスから何も届いていない状態
    // When: 100ms 分を読み出す
    // Then: 無音 frame を出し続ける (VAD のため無音も送る契約)
    [Fact]
    public void EmitsSilenceFramesWhenTheDeviceStarves()
    {
        var pipeline = new CapturedAudioFramePipeline(new WaveFormat(Pcm16FramePacketizer.SampleRate, 16, 1));

        var frames = pipeline.ReadFrames(Pcm16FramePacketizer.SamplesPerFrame);

        Assert.Single(frames);
        Assert.All(frames[0], value => Assert.Equal(0, value));
    }

    // Given: 48kHz ステレオでデバイス供給が無い状態
    // When: 100ms tick 相当を連続で読み出す
    // Then: リサンプラ短尺があっても毎回ちょうど 1 無音 frame が返り、欠番しない
    [Fact]
    public void EmitsSilenceFramesEveryTickWhenResampledDeviceStarves()
    {
        var pipeline = new CapturedAudioFramePipeline(new WaveFormat(48_000, 16, 2));

        for (var tick = 0; tick < 5; tick++)
        {
            var frames = pipeline.ReadFrames(Pcm16FramePacketizer.SamplesPerFrame);

            Assert.Single(frames);
            Assert.Equal(Pcm16FramePacketizer.BytesPerFrame, frames[0].Length);
            Assert.All(frames[0], value => Assert.Equal(0, value));
        }
    }

    // Given: macOS bufferingNewest(32) 相当の frame channel
    // When: 容量を超えて書き込む
    // Then: 古い frame が捨てられ、最新が残る
    [Fact]
    public void FrameChannelDropsOldestWhenTheConsumerLags()
    {
        var channel = WasapiAudioCaptureService.CreateFrameChannel();
        var capacity = WasapiAudioCaptureService.FrameChannelCapacity;

        for (var index = 0; index < capacity + 3; index++)
        {
            var frame = new byte[Pcm16FramePacketizer.BytesPerFrame];
            frame[0] = (byte)index;
            Assert.True(channel.Writer.TryWrite(frame));
        }

        Assert.True(channel.Reader.TryRead(out var oldestKept));
        Assert.Equal((byte)3, oldestKept.Span[0]);

        var remaining = 1;
        while (channel.Reader.TryRead(out var next))
        {
            remaining += 1;
            Assert.Equal((byte)(remaining + 2), next.Span[0]);
        }

        Assert.Equal(capacity, remaining);
    }

    // Given: 小音量の入力
    // When: 連続して観測する
    // Then: 適応ゲインが契約の範囲内に収まる
    [Fact]
    public void KeepsAdaptiveGainWithinContractBounds()
    {
        var pipeline = new CapturedAudioFramePipeline(new WaveFormat(Pcm16FramePacketizer.SampleRate, 16, 1));

        for (var iteration = 0; iteration < 20; iteration++)
        {
            pipeline.Push(
                SineWave(Pcm16FramePacketizer.SamplesPerFrame, 1, amplitude: 0.02),
                Pcm16FramePacketizer.BytesPerFrame);
            pipeline.ReadFrames(Pcm16FramePacketizer.SamplesPerFrame);
        }

        Assert.InRange(pipeline.CurrentGain, AdaptiveMicrophoneGain.MinimumGain, AdaptiveMicrophoneGain.MaximumGain);
    }

    private static byte[] SineWave(int totalSamples, int channels, double amplitude = 0.5)
    {
        var bytes = new byte[totalSamples * 2];
        for (var index = 0; index < totalSamples; index++)
        {
            var frameIndex = index / channels;
            var value = (short)(amplitude * short.MaxValue * Math.Sin(2 * Math.PI * 440 * frameIndex / 24_000.0));
            bytes[index * 2] = (byte)(value & 0xFF);
            bytes[(index * 2) + 1] = (byte)((value >> 8) & 0xFF);
        }

        return bytes;
    }
}
