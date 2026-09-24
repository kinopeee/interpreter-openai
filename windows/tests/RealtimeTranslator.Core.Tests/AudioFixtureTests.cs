using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using RealtimeTranslator.Core.Audio;
using Xunit;

namespace RealtimeTranslator.Core.Tests;

public sealed class AudioFixtureTests
{
    public static TheoryData<string> PacketizerCases => SharedFixtures.CaseNames("audio", "packetizer", 2);

    public static TheoryData<string> Float32Cases => SharedFixtures.CaseNames("audio", "float32ToPcm16", 2);

    public static TheoryData<string> GainCases => GainCaseNames();

    // Given: shared fixture の音声フォーマット定義
    // When: packetizer の定数と照合する
    // Then: 24 kHz / 100 ms / 2,400 sample / 4,800 byte が一致する
    [Fact]
    public void FormatMatchesFixture()
    {
        // Given: shared audio fixture の format 定数
        var format = SharedFixtures.Load("audio", 2)["format"]!.AsObject();

        // When/Then: packetizer 定数が一致する
        Assert.Equal(SharedFixtures.Number(format["sampleRate"]), Pcm16FramePacketizer.SampleRate);
        Assert.Equal(SharedFixtures.Number(format["bytesPerSample"]), Pcm16FramePacketizer.BytesPerSample);
        Assert.Equal(
            SharedFixtures.Number(format["frameDurationMilliseconds"]),
            Pcm16FramePacketizer.FrameDurationMilliseconds
        );
        Assert.Equal(SharedFixtures.Number(format["samplesPerFrame"]), Pcm16FramePacketizer.SamplesPerFrame);
        Assert.Equal(SharedFixtures.Number(format["bytesPerFrame"]), Pcm16FramePacketizer.BytesPerFrame);
    }

    // Given: fixture の PCM16 入力バイト列
    // When: packetizer へ流し込む
    // Then: 期待するフレーム分割と残バイトになる
    [Theory]
    [MemberData(nameof(PacketizerCases))]
    public void PacketizerMatchesFixture(string name)
    {
        // Given: packetizer fixture ケース
        var fixture = SharedFixtures.Case("audio", "packetizer", name, 2);
        var packetizer = new Pcm16FramePacketizer();

        // When: append / reset を順に適用する
        foreach (var step in fixture["steps"]!.AsArray())
        {
            var typed = step!.AsObject();
            if (SharedFixtures.Text(typed["kind"]) == "reset")
            {
                packetizer.Reset();
                continue;
            }

            var frames = packetizer.Append(Ramp(SharedFixtures.Number(typed["byteCount"])));
            Assert.Equal(SharedFixtures.Number(typed["expectedFrameCount"]), frames.Count);
            Assert.All(frames, frame => Assert.Equal(Pcm16FramePacketizer.BytesPerFrame, frame.Length));
        }

        Assert.Equal(SharedFixtures.Number(fixture["expectedPendingBytes"]), packetizer.PendingByteCount);

        // Then: flush 結果と pending が期待どおり
        var flush = fixture["flush"]!.AsObject();
        var flushed = packetizer.FlushWithSilencePadding();
        var expectedFlushBytes = SharedFixtures.OptionalNumber(flush["expectedFrameBytes"]);
        if (expectedFlushBytes is null)
        {
            Assert.Null(flushed);
            return;
        }

        Assert.NotNull(flushed);
        Assert.Equal(expectedFlushBytes.Value, flushed.Length);
        Assert.Equal(SharedFixtures.Number(flush["expectedTrailingZeroBytes"]), TrailingZeroCount(flushed));
        Assert.Equal(0, packetizer.PendingByteCount);
    }

    /// <summary>フレーム境界でサンプルを落とさない・並べ替えないことを連結して確認する。</summary>
    // Given: フレーム境界と無関係な長さで分割した連続入力
    // When: 順に packetizer へ流し込む
    // Then: 出力フレームを連結すると入力バイト列が欠落なく復元される
    [Fact]
    public void PacketizerPreservesTheInputStream()
    {
        // Given: 連続 append 用の byte 列
        var fixture = SharedFixtures.Load("audio", 2)["packetizerContinuity"]!.AsObject();
        var packetizer = new Pcm16FramePacketizer();
        var input = new List<byte>();
        var emitted = new List<byte>();

        // When: 複数チャンクを append して flush する
        foreach (var byteCount in fixture["appendByteCounts"]!.AsArray())
        {
            var chunk = Ramp(SharedFixtures.Number(byteCount), input.Count);
            input.AddRange(chunk);
            foreach (var frame in packetizer.Append(chunk))
            {
                emitted.AddRange(frame);
            }
        }

        Assert.Equal(SharedFixtures.Number(fixture["totalInputBytes"]), input.Count);
        Assert.Equal(
            SharedFixtures.Number(fixture["expectedEmittedFrameCount"]),
            emitted.Count / Pcm16FramePacketizer.BytesPerFrame
        );

        var flushed = packetizer.FlushWithSilencePadding();
        Assert.NotNull(flushed);
        Assert.Equal(SharedFixtures.Number(fixture["expectedFlushFrameBytes"]), flushed.Length);
        Assert.Equal(SharedFixtures.Number(fixture["expectedTrailingZeroBytes"]), TrailingZeroCount(flushed));

        // Then: 入力ストリームが順序どおり保持され、不足分だけ 0 padding
        emitted.AddRange(flushed);
        Assert.Equal(input, emitted.Take(input.Count));
        Assert.All(emitted.Skip(input.Count), padding => Assert.Equal(0, padding));
    }

    // Given: fixture の float32 サンプル
    // When: PCM16 へ変換する
    // Then: クリップと丸めを含めて期待値と一致する
    [Theory]
    [MemberData(nameof(Float32Cases))]
    public void Float32ToPcm16MatchesFixture(string name)
    {
        // Given: float32→PCM16 fixture
        var fixture = SharedFixtures.Case("audio", "float32ToPcm16", name, 2);

        // When/Then: 1 サンプルの符号化結果が一致する
        Assert.Equal(
            (short)SharedFixtures.Number(fixture["expected"]),
            Pcm16LittleEndianEncoder.EncodeSample(
                (float)SharedFixtures.Real(fixture["sample"]),
                (float)SharedFixtures.Real(fixture["gain"])
            )
        );
    }

    // Given: fixture の float32 サンプル分割ケース
    // When: accumulator へ流し込む
    // Then: フレーム分割・pending・flush padding が期待値と一致する
    [Fact]
    public void Float32FramingMatchesFixture()
    {
        var framing = SharedFixtures.Load("audio", 2)["float32Framing"]!.AsObject();
        Assert.Equal(SharedFixtures.Number(framing["frameSamples"]), Float32FrameAccumulator.FrameSamples);

        foreach (var caseItem in framing["cases"]!.AsArray())
        {
            var fixture = caseItem!.AsObject();
            var name = SharedFixtures.Text(fixture["name"]);
            var accumulator = new Float32FrameAccumulator();
            var appendCounts = fixture["appendSampleCounts"]!.AsArray();
            var expectedCounts = fixture["expectedFrameCounts"]!.AsArray();
            Assert.Equal(appendCounts.Count, expectedCounts.Count);

            for (var index = 0; index < appendCounts.Count; index += 1)
            {
                var samples = new float[SharedFixtures.Number(appendCounts[index])];
                Array.Fill(samples, 0.5f);
                var frames = accumulator.Append(samples);
                Assert.Equal(SharedFixtures.Number(expectedCounts[index]), frames.Count);
                Assert.All(frames, frame => Assert.Equal(Float32FrameAccumulator.FrameSamples, frame.Length));
            }

            Assert.Equal(SharedFixtures.Number(fixture["expectedPendingSamples"]), accumulator.PendingSampleCount);

            var flushed = accumulator.FlushWithSilencePadding();
            if (SharedFixtures.OptionalNumber(fixture["flushPadsSilenceSamples"]) is not { } expectedPadding)
            {
                Assert.Null(flushed);
                continue;
            }

            Assert.NotNull(flushed);
            Assert.Equal(Float32FrameAccumulator.FrameSamples, flushed.Length);
            var trailingZeros = 0;
            for (var index = flushed.Length - 1; index >= 0 && flushed[index] == 0f; index -= 1)
            {
                trailingZeros += 1;
            }

            Assert.Equal(expectedPadding, trailingZeros);
        }
    }

    // Given: fixture のフレーム統計ケース
    // When: FrameStatistics を計算する
    // Then: rms / peak が期待値と一致する
    [Fact]
    public void FrameStatisticsMatchesFixture()
    {
        var statistics = SharedFixtures.Load("audio", 2)["frameStatistics"]!.AsObject();

        foreach (var caseItem in statistics["cases"]!.AsArray())
        {
            var fixture = caseItem!.AsObject();
            var name = SharedFixtures.Text(fixture["name"]);
            var samples = fixture["samples"]!.AsArray().Select(FixtureFloat).ToArray();

            var (rms, peak) = AdaptiveMicrophoneGain.FrameStatistics(samples);

            AssertFixtureFloatEqual(fixture["expectedRms"], rms, name);
            AssertFixtureFloatEqual(fixture["expectedPeak"], peak, name);
        }
    }

    // Given: shared fixture の適応ゲイン定数
    // When: C# 実装の定数と照合する
    // Then: 全定数が一致する
    [Fact]
    public void GainConstantsMatchFixture()
    {
        // Given: gain 定数 fixture
        var constants = SharedFixtures.Load("audio", 2)["gain"]!["constants"]!.AsObject();

        // When/Then: AdaptiveMicrophoneGain 定数が一致する
        Assert.Equal(SharedFixtures.Number(constants["frameSamples"]), AdaptiveMicrophoneGain.FrameSamples);
        Assert.Equal((float)SharedFixtures.Real(constants["minimumGain"]), AdaptiveMicrophoneGain.MinimumGain);
        Assert.Equal((float)SharedFixtures.Real(constants["maximumGain"]), AdaptiveMicrophoneGain.MaximumGain);
        Assert.Equal(
            (float)SharedFixtures.Real(constants["defaultInitialGain"]),
            AdaptiveMicrophoneGain.DefaultInitialGain
        );
        Assert.Equal((float)SharedFixtures.Real(constants["targetRms"]), AdaptiveMicrophoneGain.TargetRms);
        Assert.Equal((float)SharedFixtures.Real(constants["speechRatio"]), AdaptiveMicrophoneGain.SpeechRatio);
        Assert.Equal(
            (float)SharedFixtures.Real(constants["speechAbsoluteFloor"]),
            AdaptiveMicrophoneGain.SpeechAbsoluteFloor
        );
        Assert.Equal((float)SharedFixtures.Real(constants["noiseCeiling"]), AdaptiveMicrophoneGain.NoiseCeiling);
        Assert.Equal(
            (float)SharedFixtures.Real(constants["noiseFloorMinimum"]),
            AdaptiveMicrophoneGain.NoiseFloorMinimum
        );
        Assert.Equal(
            SharedFixtures.Number(constants["noiseFloorWindowFrames"]),
            AdaptiveMicrophoneGain.NoiseFloorWindowFrames
        );
        Assert.Equal((float)SharedFixtures.Real(constants["gainRise"]), AdaptiveMicrophoneGain.GainRise);
        Assert.Equal((float)SharedFixtures.Real(constants["gainFall"]), AdaptiveMicrophoneGain.GainFall);
        Assert.Equal((float)SharedFixtures.Real(constants["clipCeiling"]), AdaptiveMicrophoneGain.ClipCeiling);
        Assert.Equal(SharedFixtures.Number(constants["rampSamples"]), AdaptiveMicrophoneGain.RampSamples);
    }

    // Given: fixture のゲイン推移シナリオ
    // When: repeat 展開した各フレームを順に観測する
    // Then: 各フレームの gain / appliedGain が期待値と一致する
    [Theory]
    [MemberData(nameof(GainCases))]
    public void GainMatchesFixture(string name)
    {
        // Given: gain ケースと許容誤差
        var gainFixture = SharedFixtures.Load("audio", 2)["gain"]!.AsObject();
        var fixture = FindGainCase(gainFixture, name);
        var tolerance = SharedFixtures.Real(gainFixture["tolerance"]);
        var enabled = fixture["enabled"] is not { } enabledNode || SharedFixtures.Flag(enabledNode);
        var agc = new AdaptiveMicrophoneGain((float)SharedFixtures.Real(fixture["initialGain"]), isEnabled: enabled);

        // When: repeat 展開した各フレームを観測する
        var trace = new List<(float Gain, float AppliedGain)>();
        foreach (var frameItem in fixture["frames"]!.AsArray())
        {
            var frame = frameItem!.AsObject();
            var repeatCount = SharedFixtures.OptionalNumber(frame["repeat"]) ?? 1;
            for (var index = 0; index < repeatCount; index += 1)
            {
                var applied = agc.Observe(FixtureFloat(frame["rms"]), FixtureFloat(frame["peak"]));
                trace.Add((agc.Gain, applied));
            }
        }

        // Then: trace / 最終値が期待値と一致する
        if (fixture["expectedTrace"] is { } traceNode)
        {
            var expectedTrace = traceNode.AsArray();
            Assert.Equal(expectedTrace.Count, trace.Count);
            for (var index = 0; index < expectedTrace.Count; index += 1)
            {
                var expected = expectedTrace[index]!.AsObject();
                Assert.Equal(SharedFixtures.Real(expected["gain"]), trace[index].Gain, tolerance);
                Assert.Equal(SharedFixtures.Real(expected["appliedGain"]), trace[index].AppliedGain, tolerance);
            }
        }

        if (fixture["expectedCheckpoints"] is { } checkpointsNode)
        {
            foreach (var checkpointItem in checkpointsNode.AsArray())
            {
                var checkpoint = checkpointItem!.AsObject();
                var index = SharedFixtures.Number(checkpoint["index"]);
                Assert.Equal(SharedFixtures.Real(checkpoint["gain"]), trace[index].Gain, tolerance);
                Assert.Equal(SharedFixtures.Real(checkpoint["appliedGain"]), trace[index].AppliedGain, tolerance);
            }
        }

        if (fixture["expectedMaxGain"] is { } maxGainNode)
        {
            Assert.Equal(SharedFixtures.Real(maxGainNode), trace.Max(entry => entry.Gain), tolerance);
        }

        var expectedFinal = fixture["expectedFinal"]!.AsObject();
        Assert.Equal(SharedFixtures.Real(expectedFinal["gain"]), agc.Gain, tolerance);
        Assert.Equal(SharedFixtures.Real(expectedFinal["appliedGain"]), agc.AppliedGain, tolerance);
    }

    // Given: fixture のゲインランプケース
    // When: previous→applied ゲインでランプ付き PCM16 化する
    // Then: 各 index の Int16 が完全一致する
    [Fact]
    public void GainRampMatchesFixture()
    {
        var ramp = SharedFixtures.Load("audio", 2)["gain"]!["ramp"]!.AsObject();

        foreach (var caseItem in ramp["cases"]!.AsArray())
        {
            var fixture = caseItem!.AsObject();
            var name = SharedFixtures.Text(fixture["name"]);
            var samples = new float[AdaptiveMicrophoneGain.FrameSamples];
            Array.Fill(samples, (float)SharedFixtures.Real(fixture["sample"]));

            var encoded = AdaptiveMicrophoneGain.EncodePcm16(
                samples,
                (float)SharedFixtures.Real(fixture["previousAppliedGain"]),
                (float)SharedFixtures.Real(fixture["appliedGain"]),
                (float)SharedFixtures.Real(fixture["peak"])
            );

            foreach (var checkItem in fixture["checks"]!.AsArray())
            {
                var check = checkItem!.AsObject();
                var index = SharedFixtures.Number(check["index"]);
                Assert.Equal(
                    (short)SharedFixtures.Number(check["expectedPcm16"]),
                    BinaryPrimitives.ReadInt16LittleEndian(encoded.AsSpan(index * 2, 2))
                );
            }
        }
    }

    // Given: 録音開始直後から発話が続く系列（未確定フロア）
    // When: 観測する
    // Then: フロア未確定の間は下げず、小さなポーズのあとは発話として上がる
    [Fact]
    public void SpeechFromStartKeepsGainUntilFirstPause()
    {
        var agc = new AdaptiveMicrophoneGain(initialGain: 4.0f);
        var frames = new[] { (0.01f, 0.03f), (0.02f, 0.06f), (0.01f, 0.03f) }.Concat(
            Enumerable.Repeat((0.02f, 0.06f), 4)
        );
        foreach (var (rms, peak) in frames)
        {
            agc.Observe(rms, peak);
            Assert.Equal(4.0f, agc.Gain);
        }

        agc.Observe(0.001f, 0.003f);
        agc.Observe(0.02f, 0.06f);
        Assert.Equal(4.48f, agc.Gain, 0.0005);
        agc.Observe(0.02f, 0.06f);
        Assert.Equal(5.0f, agc.Gain, 0.0005);
    }

    // Given: フロア確定 (30フレーム) に満たない一定ノイズ
    // When: 29 フレーム、30 フレーム、31 フレームと観測する
    // Then: 未確定の間は gain を下げず、確定と同時に noiseCap へ下がる
    [Fact]
    public void UnconfirmedNoiseFloorNeverLowersGain()
    {
        var agc = new AdaptiveMicrophoneGain(initialGain: 4.0f);
        for (var index = 0; index < 29; index += 1)
        {
            agc.Observe(0.004f, 0.012f);
            Assert.Equal(4.0f, agc.Gain);
        }

        agc.Observe(0.004f, 0.012f);
        Assert.Equal(3.2f, agc.Gain, 0.0005);
        agc.Observe(0.004f, 0.012f);
        Assert.Equal(2.56f, agc.Gain, 0.0005);
    }

    // Given: 発話中にクリック（大ピーク）が1フレーム混じる系列
    // When: 観測する
    // Then: クリックは appliedGain だけを下げ、直後の発話で gain が回復する
    [Fact]
    public void GainRecoversAfterClickDuringSpeech()
    {
        var agc = new AdaptiveMicrophoneGain(initialGain: 4.0f);
        for (var index = 0; index < 30; index += 1)
        {
            agc.Observe(0.001f, 0.003f);
        }

        for (var index = 0; index < 3; index += 1)
        {
            agc.Observe(0.02f, 0.06f);
        }

        Assert.Equal(5.0f, agc.Gain, 0.0005);

        agc.Observe(0.06f, 0.9f);
        Assert.Equal(4.0f, agc.Gain, 0.0005);
        Assert.Equal(1.0f, agc.AppliedGain, 0.0005);
        agc.Observe(0.02f, 0.06f);
        Assert.Equal(4.48f, agc.Gain, 0.0005);
        agc.Observe(0.02f, 0.06f);
        Assert.Equal(5.0f, agc.Gain, 0.0005);
    }

    // Given: 無音で noiseFloor が下限に落ちた状態
    // When: 一定ノイズのフレームを続けて観測する
    // Then: 窓がノイズで埋まるまで上がり、確定後は noiseCap (2.5) まで下がる
    [Fact]
    public void SteadyNoiseAfterSilenceFallsToNoiseCap()
    {
        var agc = new AdaptiveMicrophoneGain(initialGain: 4.0f);
        var trace = new List<float>();
        for (var index = 0; index < 5; index += 1)
        {
            agc.Observe(0f, 0f);
            trace.Add(agc.Gain);
        }

        for (var index = 0; index < 200; index += 1)
        {
            agc.Observe(0.004f, 0.012f);
            trace.Add(agc.Gain);
        }

        Assert.Equal(8.0f, trace[12], 0.0005);
        Assert.Equal(8.0f, trace[33], 0.0005);
        Assert.Equal(2.5f, trace[40], 0.0005);
        Assert.Equal(2.5f, agc.Gain, 0.0005);
        Assert.Equal(2.5f, agc.AppliedGain, 0.0005);
    }

    // Given: 前フレームで高い appliedGain だった状態からの大きな音のフレーム
    // When: Process する
    // Then: ランプ先頭を含め全サンプルがフレーム peak 上限 (29490) 以内に収まる
    [Fact]
    public void DescendingRampIsCappedByFramePeakLimit()
    {
        var agc = new AdaptiveMicrophoneGain(initialGain: 4.0f);
        var quiet = new float[AdaptiveMicrophoneGain.FrameSamples];
        agc.Process(quiet);
        Assert.Equal(4.0f, agc.AppliedGain);

        var loud = new float[AdaptiveMicrophoneGain.FrameSamples];
        Array.Fill(loud, 0.8f);
        var encoded = agc.Process(loud);

        for (var index = 0; index < AdaptiveMicrophoneGain.FrameSamples; index += 1)
        {
            var sample = BinaryPrimitives.ReadInt16LittleEndian(encoded.AsSpan(index * 2, 2));
            Assert.InRange(Math.Abs((int)sample), 0, 29490);
        }
    }

    // Given: AGC と同じく NaN / ±Infinity が混在する float サンプル
    // When: PCM16 へ変換する
    // Then: NaN は無音、±Infinity は ±full scale へクリップされ、例外にしない
    [Fact]
    public void EncodeSampleMapsNaNToSilenceAndClipsInfinity()
    {
        Assert.Equal(0, Pcm16LittleEndianEncoder.EncodeSample(float.NaN, 1f));
        Assert.Equal(16384, Pcm16LittleEndianEncoder.EncodeSample(0.5f, 1f));
        Assert.Equal(short.MaxValue, Pcm16LittleEndianEncoder.EncodeSample(float.PositiveInfinity, 1f));
        Assert.Equal(-32767, Pcm16LittleEndianEncoder.EncodeSample(float.NegativeInfinity, 1f));
        Assert.Equal(16384, Pcm16LittleEndianEncoder.EncodeSample(0.5f, float.NaN));
        Assert.Equal(0, Pcm16LittleEndianEncoder.EncodeSample(float.PositiveInfinity, 0f));
    }

    // Given: 非有限の初期ゲイン
    // When: AdaptiveMicrophoneGain を生成する
    // Then: ArgumentOutOfRangeException になる
    [Fact]
    public void NonFiniteInitialGainIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AdaptiveMicrophoneGain(float.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AdaptiveMicrophoneGain(float.PositiveInfinity));
    }

    // Given: 発話を観測済みの AGC
    // When: 非有限の rms / peak を挟む
    // Then: 状態は変わらず、現在の appliedGain を返す
    [Fact]
    public void NonFiniteStatisticsKeepStateUnchanged()
    {
        var agc = new AdaptiveMicrophoneGain(4.0f);
        _ = agc.Observe(0.02f, 0.06f);
        var gainBefore = agc.Gain;
        var appliedBefore = agc.AppliedGain;

        Assert.Equal(appliedBefore, agc.Observe(float.NaN, 0.06f));
        Assert.Equal(appliedBefore, agc.Observe(0.02f, float.PositiveInfinity));
        Assert.Equal(appliedBefore, agc.Observe(float.NegativeInfinity, float.NaN));

        Assert.Equal(gainBefore, agc.Gain);
        Assert.Equal(appliedBefore, agc.AppliedGain);
    }

    // Given: disabled の AGC
    // When: 発話相当の統計を観測する
    // Then: 1.0 を返し、Gain も AppliedGain も変わらない
    [Fact]
    public void DisabledReturnsUnityAndKeepsState()
    {
        var agc = new AdaptiveMicrophoneGain(4.0f, isEnabled: false);

        Assert.Equal(4.0f, agc.Gain);
        Assert.Equal(1.0f, agc.AppliedGain);
        Assert.Equal(1.0f, agc.Observe(0.3f, 1.0f));
        Assert.Equal(4.0f, agc.Gain);
        Assert.Equal(1.0f, agc.AppliedGain);
    }

    // Given: 全サンプル非有限のフレームと 2,400 サンプルのフレーム
    // When: Process する
    // Then: 非有限は無音・状態不変、通常フレームは 4,800 バイトを返す
    [Fact]
    public void ProcessEncodesOneFrameAndKeepsStateOnNonFiniteInput()
    {
        var agc = new AdaptiveMicrophoneGain(4.0f);

        // NaN は無音 (0)、+Inf は +32767、-Inf は -32767 にクリップされる。
        var nonFinite = agc.Process([float.NaN, float.PositiveInfinity, float.NegativeInfinity]);
        Assert.Equal(3 * 2, nonFinite.Length);
        Assert.Equal(0, BinaryPrimitives.ReadInt16LittleEndian(nonFinite.AsSpan(0, 2)));
        Assert.Equal(short.MaxValue, BinaryPrimitives.ReadInt16LittleEndian(nonFinite.AsSpan(2, 2)));
        Assert.Equal(-short.MaxValue, BinaryPrimitives.ReadInt16LittleEndian(nonFinite.AsSpan(4, 2)));
        Assert.Equal(4.0f, agc.Gain);
        Assert.Equal(4.0f, agc.AppliedGain);

        var frame = agc.Process(new float[AdaptiveMicrophoneGain.FrameSamples]);
        Assert.Equal(Pcm16FramePacketizer.BytesPerFrame, frame.Length);
    }

    // Given: 複数 float サンプルとゲイン
    // When: 本番の Encode(buffer) 経路で PCM16 LE に変換する
    // Then: 各サンプルは EncodeSample と同じ値で little-endian に並び、不足バッファは拒否する
    [Fact]
    public void EncodeBufferMatchesEncodeSampleAndRejectsShortDestination()
    {
        float[] samples = [0.5f, -1f, float.NaN, 0f, float.PositiveInfinity];
        const float gain = 2f;

        var encoded = Pcm16LittleEndianEncoder.Encode(samples, gain);
        var destination = new byte[samples.Length * 2];
        Pcm16LittleEndianEncoder.Encode(samples, destination, gain);

        Assert.Equal(samples.Length * 2, encoded.Length);
        Assert.Equal(encoded, destination);
        for (var index = 0; index < samples.Length; index += 1)
        {
            Assert.Equal(
                Pcm16LittleEndianEncoder.EncodeSample(samples[index], gain),
                BinaryPrimitives.ReadInt16LittleEndian(encoded.AsSpan(index * 2, 2))
            );
        }

        var tooSmall = Assert.Throws<ArgumentException>(() =>
            Pcm16LittleEndianEncoder.Encode(samples, new byte[samples.Length], gain)
        );
        Assert.Equal("destination", tooSmall.ParamName);
    }

    private static TheoryData<string> GainCaseNames()
    {
        var data = new TheoryData<string>();
        foreach (var item in SharedFixtures.Load("audio", 2)["gain"]!["cases"]!.AsArray())
        {
            data.Add(SharedFixtures.Text(item?["name"]));
        }

        return data;
    }

    private static System.Text.Json.Nodes.JsonObject FindGainCase(
        System.Text.Json.Nodes.JsonObject gainFixture,
        string name
    )
    {
        foreach (var item in gainFixture["cases"]!.AsArray())
        {
            if (item is System.Text.Json.Nodes.JsonObject candidate && SharedFixtures.Text(candidate["name"]) == name)
            {
                return candidate;
            }
        }

        throw new Xunit.Sdk.XunitException("no gain case named " + name);
    }

    /// <summary>fixture の number / "nan" / "infinity" / "-infinity" 表記を float へ変換する。</summary>
    private static float FixtureFloat(JsonNode? node)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var literal))
        {
            return literal switch
            {
                "nan" => float.NaN,
                "infinity" => float.PositiveInfinity,
                "-infinity" => float.NegativeInfinity,
                _ => throw new InvalidOperationException("unknown fixture float literal: " + literal),
            };
        }

        return (float)SharedFixtures.Real(node);
    }

    /// <summary>期待値が "nan" 系なら isNaN、数値なら 1e-6 以内を照合する。</summary>
    private static void AssertFixtureFloatEqual(JsonNode? expected, float actual, string name)
    {
        if (expected is JsonValue value && value.TryGetValue<string>(out _))
        {
            Assert.True(float.IsNaN(actual), name);
            return;
        }

        Assert.Equal(SharedFixtures.Real(expected), actual, 1e-6);
    }

    /// <summary>0 padding と区別できるよう、非ゼロの繰り返しパターンを作る。</summary>
    private static byte[] Ramp(int byteCount, int offset = 0)
    {
        var bytes = new byte[byteCount];
        for (var index = 0; index < byteCount; index += 1)
        {
            bytes[index] = (byte)(((offset + index) % 255) + 1);
        }

        return bytes;
    }

    private static int TrailingZeroCount(ReadOnlySpan<byte> frame)
    {
        var count = 0;
        for (var index = frame.Length - 1; index >= 0 && frame[index] == 0; index -= 1)
        {
            count += 1;
        }

        return count;
    }
}
