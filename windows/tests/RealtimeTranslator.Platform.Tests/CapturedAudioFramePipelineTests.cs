using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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
    // When: 300ms 分を投入して tick 相当で吸い出す（リサンプラ遅延分の余裕）
    // Then: 24kHz mono の 100ms frame が 2 つ以上出る
    [Fact]
    public void ResamplesAndDownmixesDeviceAudio()
    {
        var pipeline = new CapturedAudioFramePipeline(new WaveFormat(48_000, 16, 2));
        var samplesPerChannel = 48_000 * 3 / 10;

        pipeline.Push(SineWave(samplesPerChannel * 2, 2), samplesPerChannel * 2 * 2);
        var frames = pipeline.TakeTickFrames(Pcm16FramePacketizer.SamplesPerFrame);

        Assert.True(frames.Count >= 2, "resampled 300ms should yield at least two 100ms frames");
        Assert.All(frames, frame => Assert.Equal(Pcm16FramePacketizer.BytesPerFrame, frame.Length));
    }

    // Given: 24kHz ステレオで左だけ / 右だけに正弦波
    // When: 100ms を読み出す
    // Then: どちらもモノラル frame が無音にならない（片チャンネル破棄の防止）
    [Fact]
    public void DownmixesStereoSoNeitherChannelIsDropped()
    {
        var format = new WaveFormat(Pcm16FramePacketizer.SampleRate, 16, 2);
        var leftOnly = new CapturedAudioFramePipeline(format, new AdaptiveMicrophoneGain(1f));
        leftOnly.Push(
            InterleavedSine(Pcm16FramePacketizer.SamplesPerFrame, [0.5, 0.0]),
            Pcm16FramePacketizer.SamplesPerFrame * 4);

        var rightOnly = new CapturedAudioFramePipeline(format, new AdaptiveMicrophoneGain(1f));
        rightOnly.Push(
            InterleavedSine(Pcm16FramePacketizer.SamplesPerFrame, [0.0, 0.5]),
            Pcm16FramePacketizer.SamplesPerFrame * 4);

        var leftFrames = leftOnly.ReadFrames(Pcm16FramePacketizer.SamplesPerFrame);
        var rightFrames = rightOnly.ReadFrames(Pcm16FramePacketizer.SamplesPerFrame);

        Assert.Single(leftFrames);
        Assert.Single(rightFrames);
        Assert.Contains(leftFrames[0], value => value != 0);
        Assert.Contains(rightFrames[0], value => value != 0);
    }

    // Given: デバイスから何も届いていない状態
    // When: 100ms tick 相当を読み出す
    // Then: 無音 frame を出し続ける (VAD のため無音も送る契約)
    [Fact]
    public void EmitsSilenceFramesWhenTheDeviceStarves()
    {
        var pipeline = new CapturedAudioFramePipeline(new WaveFormat(Pcm16FramePacketizer.SampleRate, 16, 1));

        var frames = pipeline.TakeTickFrames(Pcm16FramePacketizer.SamplesPerFrame);

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
            var frames = pipeline.TakeTickFrames(Pcm16FramePacketizer.SamplesPerFrame);

            Assert.Single(frames);
            Assert.Equal(Pcm16FramePacketizer.BytesPerFrame, frames[0].Length);
            Assert.All(frames[0], value => Assert.Equal(0, value));
        }
    }

    // Given: 50ms 分だけ実音声がある 24kHz mono
    // When: 100ms 相当を読む
    // Then: 無音 padding で 1 frame を作らず端数を保持する（時間軸の引き伸ばし防止）
    [Fact]
    public void DoesNotPadPartialAudioWithSilence()
    {
        var pipeline = new CapturedAudioFramePipeline(
            new WaveFormat(Pcm16FramePacketizer.SampleRate, 16, 1),
            new AdaptiveMicrophoneGain(1f));
        var halfBytes = Pcm16FramePacketizer.BytesPerFrame / 2;

        pipeline.Push(SineWave(Pcm16FramePacketizer.SamplesPerFrame / 2, 1), halfBytes);
        var first = pipeline.TakeTickFrames(Pcm16FramePacketizer.SamplesPerFrame);

        Assert.Empty(first);
        Assert.True(pipeline.HasUnsentAudio);

        pipeline.Push(SineWave(Pcm16FramePacketizer.SamplesPerFrame / 2, 1), halfBytes);
        var second = pipeline.TakeTickFrames(Pcm16FramePacketizer.SamplesPerFrame);

        Assert.Single(second);
        Assert.Equal(Pcm16FramePacketizer.BytesPerFrame, second[0].Length);
        Assert.Contains(second[0], value => value != 0);
        var trailing = second[0].AsSpan(Pcm16FramePacketizer.BytesPerFrame / 2);
        Assert.True(trailing.ToArray().Any(value => value != 0), "second half must stay real audio, not baked silence");
    }

    // Given: 50ms の端数だけあり、その後デバイス供給が止まる
    // When: tick を連続で読む
    // Then: 直後は padding せず、KeepAliveEmptyTicks 到達後は無音 frame を送り続ける
    [Fact]
    public void EmitsKeepAliveAfterPartialAudioStarves()
    {
        var pipeline = new CapturedAudioFramePipeline(
            new WaveFormat(Pcm16FramePacketizer.SampleRate, 16, 1),
            new AdaptiveMicrophoneGain(1f));
        var halfBytes = Pcm16FramePacketizer.BytesPerFrame / 2;

        pipeline.Push(SineWave(Pcm16FramePacketizer.SamplesPerFrame / 2, 1), halfBytes);

        var held = pipeline.TakeTickFrames(Pcm16FramePacketizer.SamplesPerFrame);
        Assert.Empty(held);
        Assert.True(pipeline.HasUnsentAudio);

        for (var tick = 1; tick < CapturedAudioFramePipeline.KeepAliveEmptyTicks - 1; tick++)
        {
            var waiting = pipeline.TakeTickFrames(Pcm16FramePacketizer.SamplesPerFrame);
            Assert.Empty(waiting);
        }

        var keepAlive = pipeline.TakeTickFrames(Pcm16FramePacketizer.SamplesPerFrame);
        Assert.Single(keepAlive);
        Assert.Equal(Pcm16FramePacketizer.BytesPerFrame, keepAlive[0].Length);
        Assert.All(keepAlive[0], value => Assert.Equal(0, value));
        Assert.True(pipeline.HasUnsentAudio);

        var next = pipeline.TakeTickFrames(Pcm16FramePacketizer.SamplesPerFrame);
        Assert.Single(next);
        Assert.All(next[0], value => Assert.Equal(0, value));

        pipeline.Push(SineWave(Pcm16FramePacketizer.SamplesPerFrame / 2, 1), halfBytes);
        var resumed = pipeline.TakeTickFrames(Pcm16FramePacketizer.SamplesPerFrame);
        Assert.Single(resumed);
        Assert.Contains(resumed[0], value => value != 0);
        var trailing = resumed[0].AsSpan(Pcm16FramePacketizer.BytesPerFrame / 2);
        Assert.True(trailing.ToArray().Any(value => value != 0), "held remainder must stay real audio after keep-alive");
    }

    // Given: 50ms の端数が保持されている
    // When: 停止相当で FlushRemainder する
    // Then: 端数が 1 frame として出る（macOS stream-end flush と同値）
    [Fact]
    public void FlushRemainderEmitsHeldPartialAudio()
    {
        var pipeline = new CapturedAudioFramePipeline(
            new WaveFormat(Pcm16FramePacketizer.SampleRate, 16, 1),
            new AdaptiveMicrophoneGain(1f));
        var halfBytes = Pcm16FramePacketizer.BytesPerFrame / 2;

        pipeline.Push(SineWave(Pcm16FramePacketizer.SamplesPerFrame / 2, 1), halfBytes);
        Assert.Empty(pipeline.TakeTickFrames(Pcm16FramePacketizer.SamplesPerFrame));

        var flushed = pipeline.FlushRemainder();

        Assert.Single(flushed);
        Assert.Equal(Pcm16FramePacketizer.BytesPerFrame, flushed[0].Length);
        Assert.Contains(flushed[0], value => value != 0);
        Assert.False(pipeline.HasUnsentAudio);
    }

    // Given: 48kHz mono でちょうど 100ms の実音声がある
    // When: 1 tick で読む
    // Then: WDL +4 ゲートで空→keep-alive 無音にせず、実音声 frame を出す
    [Fact]
    public void ExactHundredMillisecondsAtResampledRateEmitsAudioNotKeepAlive()
    {
        var pipeline = new CapturedAudioFramePipeline(
            new WaveFormat(48_000, 16, 1),
            new AdaptiveMicrophoneGain(1f));
        var samples = 48_000 / 10;
        var audio = SineWave(samples, 1);

        pipeline.Push(audio, audio.Length);
        var first = pipeline.TakeTickFrames(Pcm16FramePacketizer.SamplesPerFrame);

        Assert.True(first.Count >= 1, "exactly 100ms at 48kHz must produce a frame, not a gated empty tick");
        Assert.Contains(first[0], value => value != 0);
    }

    // Given: 48kHz mono で 1 sample だけ届いた
    // When: 100ms tick を読み、その後 100ms 分を足す
    // Then: 短い入力をリサンプラへ渡して捨てず、後続 frame の先頭に残る
    [Fact]
    public void DoesNotDropSubResamplerRemainderOnShortDeviceRead()
    {
        var pipeline = new CapturedAudioFramePipeline(
            new WaveFormat(48_000, 16, 1),
            new AdaptiveMicrophoneGain(1f));
        var loud = BitConverter.GetBytes((short)32_000);

        pipeline.Push(loud, loud.Length);
        var first = pipeline.TakeTickFrames(Pcm16FramePacketizer.SamplesPerFrame);

        Assert.Empty(first);
        Assert.True(pipeline.HasUnsentAudio);

        var rest = new byte[(48_000 / 10 * 2) + 16];
        pipeline.Push(rest, rest.Length);
        var second = pipeline.TakeTickFrames(Pcm16FramePacketizer.SamplesPerFrame);

        Assert.True(second.Count >= 1);
        Assert.True(
            BitConverter.ToInt16(second[0], 0) != 0,
            "first device sample must survive until a complete frame can be resampled");
    }

    // Given: 300ms 分の実音声がバッファに溜まっている
    // When: 1 tick で吸い出す
    // Then: 100ms を 1 枚ずつにせず 3 frame まとめて返し、遅延を残さない
    [Fact]
    public void TakeTickFramesDrainsBacklogInsteadOfPacingOneFrame()
    {
        var pipeline = new CapturedAudioFramePipeline(new WaveFormat(Pcm16FramePacketizer.SampleRate, 16, 1));
        pipeline.Push(
            SineWave(Pcm16FramePacketizer.SamplesPerFrame * 3, 1),
            Pcm16FramePacketizer.BytesPerFrame * 3);

        var frames = pipeline.TakeTickFrames(Pcm16FramePacketizer.SamplesPerFrame);

        Assert.Equal(3, frames.Count);
        Assert.All(frames, frame => Assert.Equal(Pcm16FramePacketizer.BytesPerFrame, frame.Length));
    }

    // Given: 2 秒バッファを超える入力（先頭 0.5 秒だけ非無音）
    // When: 溢れた分を Push する
    // Then: 古い非無音が捨てられ、読み出し先頭は無音（DropOldest。NAudio 既定の newest 切り捨ては使わない）
    [Fact]
    public void CaptureOverflowDropsOldestDeviceBytes()
    {
        var pipeline = new CapturedAudioFramePipeline(
            new WaveFormat(Pcm16FramePacketizer.SampleRate, 16, 1),
            new AdaptiveMicrophoneGain(1f));
        var loud = SineWave(Pcm16FramePacketizer.SamplesPerFrame, 1, amplitude: 0.5);
        var silence = new byte[Pcm16FramePacketizer.BytesPerFrame];

        for (var index = 0; index < 5; index++)
        {
            pipeline.Push(loud, loud.Length);
        }

        for (var index = 0; index < 20; index++)
        {
            pipeline.Push(silence, silence.Length);
        }

        var frames = pipeline.TakeTickFrames(Pcm16FramePacketizer.SamplesPerFrame);

        Assert.True(frames.Count >= 1);
        Assert.All(frames[0], value => Assert.Equal(0, value));
    }

    // Given: macOS bufferingNewest(32) 相当の frame channel
    // When: 容量を超えて書き込む
    // Then: 古い frame が捨てられ、最新が残る
    [Fact]
    public void FrameChannelDropsOldestWhenTheConsumerLags()
    {
        long droppedCount = 0;
        var channel = WasapiAudioCaptureService.CreateFrameChannel(
            _ => Interlocked.Increment(ref droppedCount));
        var capacity = WasapiAudioCaptureService.FrameChannelCapacity;

        for (var index = 0; index < capacity + 3; index++)
        {
            var frame = new CapturedAudioFrame(
                1,
                index,
                new byte[Pcm16FramePacketizer.BytesPerFrame],
                0,
                index);
            Assert.True(channel.Writer.TryWrite(frame));
        }

        Assert.True(channel.Reader.TryRead(out var oldestKept));
        Assert.Equal(3, oldestKept.Sequence);

        var remaining = 1;
        while (channel.Reader.TryRead(out var next))
        {
            remaining += 1;
            Assert.Equal(remaining + 2, next.Sequence);
        }

        Assert.Equal(capacity, remaining);
        Assert.Equal(3, Volatile.Read(ref droppedCount));
    }

    // Given: tracker が先頭 frame を観測済みの 32 枚 bounded channel
    // When: 読み手が遅れている間に 40 枚を追加する
    // Then: 最新32件を保持し、連番欠番から 8 枚 / 800ms の欠落を検知する
    [Fact]
    public void FrameChannelRetainsNewestFramesForLossTracking()
    {
        long droppedCount = 0;
        var channel = WasapiAudioCaptureService.CreateFrameChannel(
            _ => Interlocked.Increment(ref droppedCount));
        var tracker = new AudioLossTracker();

        Assert.True(channel.Writer.TryWrite(new CapturedAudioFrame(
            1,
            0,
            new byte[Pcm16FramePacketizer.BytesPerFrame],
            0,
            0)));
        Assert.True(channel.Reader.TryRead(out var firstFrame));
        tracker.Observe(
            firstFrame.Generation,
            firstFrame.Sequence,
            firstFrame.DiscardedMilliseconds,
            0,
            0);

        for (var sequence = 1; sequence <= 40; sequence++)
        {
            Assert.True(channel.Writer.TryWrite(new CapturedAudioFrame(
                1,
                sequence,
                new byte[Pcm16FramePacketizer.BytesPerFrame],
                0,
                sequence)));
        }

        var retained = new List<CapturedAudioFrame>();
        while (channel.Reader.TryRead(out var frame))
        {
            retained.Add(frame);
            tracker.Observe(
                frame.Generation,
                frame.Sequence,
                frame.DiscardedMilliseconds,
                0,
                frame.Sequence * Pcm16FramePacketizer.FrameDurationMilliseconds);
        }

        Assert.Equal(32, retained.Count);
        Assert.Equal(Enumerable.Range(9, 32), retained.Select(frame => (int)frame.Sequence));
        Assert.Equal(8, tracker.Metrics.DroppedFrames);
        Assert.Equal(800, tracker.Metrics.LostMilliseconds);
        Assert.Equal(8, Volatile.Read(ref droppedCount));
    }

    // Given: 2,500ms 分の24kHz mono入力を空のcapture bufferへ投入する
    // When: buffer容量を超える入力を受け取る
    // Then: 入力時間から算出した500msだけを破棄時間として記録する
    [Fact]
    public void RecordsDiscardedMillisecondsWhenCaptureBufferOverflows()
    {
        var pipeline = new CapturedAudioFramePipeline(
            new WaveFormat(Pcm16FramePacketizer.SampleRate, 16, 1));
        var inputMilliseconds = 2_500;
        var inputBytes = Pcm16FramePacketizer.SampleRate * 2 * inputMilliseconds / 1_000;

        pipeline.Push(new byte[inputBytes], inputBytes);

        Assert.Equal(500, pipeline.DiscardedMilliseconds);
    }

    // Given: 48kHz mono bufferを容量まで満たした状態
    // When: 512バイトのoverflowを3回発生させる
    // Then: 累積1536バイトを一度だけ換算した16msになる
    [Fact]
    public void RoundsCumulativeDiscardedBytesOnce()
    {
        var format = new WaveFormat(48_000, 16, 1);
        var pipeline = new CapturedAudioFramePipeline(format);
        var capacity = format.AverageBytesPerSecond * 2;

        pipeline.Push(new byte[capacity], capacity);
        for (var index = 0; index < 3; index++)
        {
            pipeline.Push(new byte[512], 512);
        }

        Assert.Equal(16, pipeline.DiscardedMilliseconds);
    }

    // Given: 先頭500msと後続2,000msで値が異なる単一チャンク
    // When: 容量以上の入力を一度に保持する
    // Then: 最新2,000msだけが残り、先頭 frame は後続側の値になる
    [Fact]
    public void LargePushRetainsNewestCaptureBufferWindow()
    {
        var pipeline = new CapturedAudioFramePipeline(
            new WaveFormat(Pcm16FramePacketizer.SampleRate, 16, 1),
            new AdaptiveMicrophoneGain(1f));
        var discardedMilliseconds = 500;
        var retainedMilliseconds = 2_000;
        var discarded = Pcm16Constant(discardedMilliseconds, 100);
        var retained = Pcm16Constant(retainedMilliseconds, 200);
        var input = new byte[discarded.Length + retained.Length];
        Buffer.BlockCopy(discarded, 0, input, 0, discarded.Length);
        Buffer.BlockCopy(retained, 0, input, discarded.Length, retained.Length);

        pipeline.Push(input, input.Length);
        var frames = pipeline.TakeTickFrames(Pcm16FramePacketizer.SamplesPerFrame);

        Assert.Equal(
            retainedMilliseconds / Pcm16FramePacketizer.FrameDurationMilliseconds,
            frames.Count);
        Assert.True(frames[0][0] > 100);
        Assert.Equal(discardedMilliseconds, pipeline.DiscardedMilliseconds);
    }

    // Given: 容量未満の入力だけが capture buffer に届いている
    // When: 入力を保持する
    // Then: 破棄時間は0msのままになる
    [Fact]
    public void DoesNotRecordDiscardBeforeCaptureBufferOverflows()
    {
        var pipeline = new CapturedAudioFramePipeline(
            new WaveFormat(Pcm16FramePacketizer.SampleRate, 16, 1));
        var inputMilliseconds = 1_500;
        var inputBytes = Pcm16FramePacketizer.SampleRate * 2 * inputMilliseconds / 1_000;

        pipeline.Push(new byte[inputBytes], inputBytes);

        Assert.Equal(0, pipeline.DiscardedMilliseconds);
    }

    // Given: 24kHz mono の capture buffer が overflow して破棄時間を持っている
    // When: pipeline を reset してから小さい入力を追加する
    // Then: 新しい世代の破棄時間は 0ms のままになる
    [Fact]
    public void ResetClearsDiscardedMilliseconds()
    {
        var format = new WaveFormat(Pcm16FramePacketizer.SampleRate, 16, 1);
        var pipeline = new CapturedAudioFramePipeline(format);
        var capacity = format.AverageBytesPerSecond * 2;
        var overflowBytes = format.AverageBytesPerSecond * 2_500 / 1_000;
        var smallPushBytes = format.AverageBytesPerSecond * 100 / 1_000;

        pipeline.Push(new byte[capacity], capacity);
        pipeline.Push(new byte[overflowBytes], overflowBytes);

        Assert.True(pipeline.DiscardedMilliseconds > 0);

        pipeline.Reset();
        Assert.Equal(0, pipeline.DiscardedMilliseconds);

        pipeline.Push(new byte[smallPushBytes], smallPushBytes);

        Assert.Equal(0, pipeline.DiscardedMilliseconds);
    }

    // Given: 24kHz mono の capture buffer を容量ちょうどまで満たしている
    // When: 小さい overflow と容量以上の入力を順に保持する
    // Then: 破棄時間は両方の入力分を累積する
    [Fact]
    public void AccumulatesDiscardAcrossSmallOverflowAndLargePush()
    {
        var format = new WaveFormat(Pcm16FramePacketizer.SampleRate, 16, 1);
        var pipeline = new CapturedAudioFramePipeline(format);
        var capacity = format.AverageBytesPerSecond * 2;
        var smallOverflowBytes = format.AverageBytesPerSecond * 100 / 1_000;
        var largePushBytes = format.AverageBytesPerSecond * 2_500 / 1_000;

        pipeline.Push(new byte[capacity], capacity);

        Assert.Equal(0, pipeline.DiscardedMilliseconds);

        pipeline.Push(new byte[smallOverflowBytes], smallOverflowBytes);
        pipeline.Push(new byte[largePushBytes], largePushBytes);

        Assert.Equal(2_600, pipeline.DiscardedMilliseconds);
    }

    // Given: 2,500ms を投入して 500ms が破棄された 24kHz mono pipeline
    // When: 1 回の tick で frame を読み出してから remainder を flush する
    // Then: 実音声は20 frame、flush は空で、破棄時間は500msのままになる
    [Fact]
    public void ReadingFramesDoesNotChangeDiscardedMilliseconds()
    {
        var format = new WaveFormat(Pcm16FramePacketizer.SampleRate, 16, 1);
        var pipeline = new CapturedAudioFramePipeline(format);
        var inputMilliseconds = 2_500;
        var inputBytes = format.AverageBytesPerSecond * inputMilliseconds / 1_000;

        pipeline.Push(new byte[inputBytes], inputBytes);

        Assert.Equal(500, pipeline.DiscardedMilliseconds);

        var frames = pipeline.TakeTickFrames(Pcm16FramePacketizer.SamplesPerFrame);
        var remainder = pipeline.FlushRemainder();

        Assert.Equal(20, frames.Count);
        Assert.Empty(remainder);
        Assert.Equal(500, pipeline.DiscardedMilliseconds);
    }

    // Given: 2,500ms を投入して500msが破棄された pipeline と loss tracker
    // When: 破棄時間を持つ frame を連続して観測し、さらに2,000msを破棄する
    // Then: 同じ破棄を二重計上せず、追加分だけを新しい loss event にする
    [Fact]
    public void TrackerCountsPipelineDiscardOnceAcrossFrames()
    {
        var format = new WaveFormat(Pcm16FramePacketizer.SampleRate, 16, 1);
        var pipeline = new CapturedAudioFramePipeline(format);
        var tracker = new AudioLossTracker();
        var inputMilliseconds = 2_500;
        var inputBytes = format.AverageBytesPerSecond * inputMilliseconds / 1_000;

        pipeline.Push(new byte[inputBytes], inputBytes);

        for (var sequence = 0; sequence < 3; sequence++)
        {
            var frame = new CapturedAudioFrame(
                1,
                sequence,
                new byte[Pcm16FramePacketizer.BytesPerFrame],
                checked((int)pipeline.DiscardedMilliseconds),
                sequence * 100);
            tracker.Observe(
                frame.Generation,
                frame.Sequence,
                frame.DiscardedMilliseconds,
                0,
                sequence * 100);
        }

        Assert.Equal(
            new AudioLossMetrics(DroppedFrames: 0, LostMilliseconds: 500, LossEvents: 1, MaxQueueWaitMilliseconds: 0),
            tracker.Metrics);

        var additionalInputMilliseconds = 2_000;
        var additionalInputBytes =
            format.AverageBytesPerSecond * additionalInputMilliseconds / 1_000;
        pipeline.Push(new byte[additionalInputBytes], additionalInputBytes);

        var finalFrame = new CapturedAudioFrame(
            1,
            3,
            new byte[Pcm16FramePacketizer.BytesPerFrame],
            checked((int)pipeline.DiscardedMilliseconds),
            300);
        tracker.Observe(
            finalFrame.Generation,
            finalFrame.Sequence,
            finalFrame.DiscardedMilliseconds,
            0,
            300);

        Assert.Equal(
            new AudioLossMetrics(DroppedFrames: 0, LostMilliseconds: 2_500, LossEvents: 2, MaxQueueWaitMilliseconds: 0),
            tracker.Metrics);
    }

    // Given: frame channel に drop callback を設定している
    // When: 容量を3つ超える frame を順番に書き込む
    // Then: 先頭3つが順番に破棄され、reader には4つ目以降が残る
    [Fact]
    public void DropCallbackReceivesEvictedFrameMetadataInOrder()
    {
        var dropped = new List<CapturedAudioFrame>();
        var channel = WasapiAudioCaptureService.CreateFrameChannel(dropped.Add);

        for (var sequence = 0; sequence < WasapiAudioCaptureService.FrameChannelCapacity + 3; sequence++)
        {
            Assert.True(channel.Writer.TryWrite(new CapturedAudioFrame(
                7,
                sequence,
                new byte[Pcm16FramePacketizer.BytesPerFrame],
                0,
                sequence)));
        }

        Assert.Equal(
            new long[] { 0, 1, 2 },
            dropped.ConvertAll(frame => frame.Sequence));
        Assert.All(dropped, frame => Assert.Equal(7, frame.Generation));

        var retained = new List<CapturedAudioFrame>();
        while (channel.Reader.TryRead(out var frame))
        {
            retained.Add(frame);
        }

        Assert.Equal(WasapiAudioCaptureService.FrameChannelCapacity, retained.Count);
        Assert.Equal(
            Enumerable.Range(3, WasapiAudioCaptureService.FrameChannelCapacity)
                .Select(sequence => (long)sequence),
            retained.ConvertAll(frame => frame.Sequence));
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
        var amplitudes = new double[channels];
        Array.Fill(amplitudes, amplitude);
        return InterleavedSine(totalSamples / channels, amplitudes);
    }

    private static byte[] InterleavedSine(int frames, double[] amplitudes)
    {
        var channels = amplitudes.Length;
        var bytes = new byte[frames * channels * 2];
        for (var frame = 0; frame < frames; frame++)
        {
            for (var channel = 0; channel < channels; channel++)
            {
                var value = (short)(amplitudes[channel] * short.MaxValue
                    * Math.Sin(2 * Math.PI * 440 * frame / 24_000.0));
                var index = ((frame * channels) + channel) * 2;
                bytes[index] = (byte)(value & 0xFF);
                bytes[index + 1] = (byte)((value >> 8) & 0xFF);
            }
        }

        return bytes;
    }

    private static byte[] Pcm16Constant(int milliseconds, short value)
    {
        var samples = Pcm16FramePacketizer.SampleRate * milliseconds / 1_000;
        var bytes = new byte[samples * sizeof(short)];
        for (var index = 0; index < samples; index++)
        {
            BitConverter.TryWriteBytes(bytes.AsSpan(index * sizeof(short)), value);
        }

        return bytes;
    }
}
