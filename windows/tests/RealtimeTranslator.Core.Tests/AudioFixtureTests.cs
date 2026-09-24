using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using RealtimeTranslator.Core.Audio;
using Xunit;

namespace RealtimeTranslator.Core.Tests;

public sealed class AudioFixtureTests
{
    public static TheoryData<string> PacketizerCases => SharedFixtures.CaseNames("audio", "packetizer", version: 2);

    public static TheoryData<string> Float32Cases => SharedFixtures.CaseNames("audio", "float32ToPcm16", version: 2);

    public static TheoryData<string> GainCases => GainSectionNames("cases");

    public static TheoryData<string> GainLevelCases => GainSectionNames("level");

    public static TheoryData<string> GainRampCases => GainSectionNames("ramp");

    // Given: shared fixture の音声フォーマット定義
    // When: packetizer の定数と照合する
    // Then: 24 kHz / 100 ms / 2,400 sample / 4,800 byte が一致する
    [Fact]
    public void FormatMatchesFixture()
    {
        // Given: shared audio fixture の format 定数
        var format = SharedFixtures.Load("audio", version: 2)["format"]!.AsObject();

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
        var fixture = SharedFixtures.Case("audio", "packetizer", name, version: 2);
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
        var fixture = SharedFixtures.Load("audio", version: 2)["packetizerContinuity"]!.AsObject();
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
        var fixture = SharedFixtures.Case("audio", "float32ToPcm16", name, version: 2);

        // When/Then: 1 サンプルの符号化結果が一致する
        Assert.Equal(
            (short)SharedFixtures.Number(fixture["expected"]),
            Pcm16LittleEndianEncoder.EncodeSample(
                (float)SharedFixtures.Real(fixture["sample"]),
                (float)SharedFixtures.Real(fixture["gain"])
            )
        );
    }

    // Given: shared fixture v2 の適応ゲイン定数
    // When: C# 実装の定数と照合する
    // Then: フレーム長・ゲイン範囲・発話判定・雑音窓・上昇/下降率・リミッタ・ランプが一致する
    [Fact]
    public void GainConstantsMatchFixture()
    {
        // Given: gain 定数 fixture
        var constants = SharedFixtures.Load("audio", version: 2)["gain"]!["constants"]!.AsObject();

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
        Assert.Equal(
            (float)SharedFixtures.Real(constants["digitalSilenceRms"]),
            AdaptiveMicrophoneGain.DigitalSilenceRms
        );
        Assert.Equal(SharedFixtures.Number(constants["noiseWindowFrames"]), AdaptiveMicrophoneGain.NoiseWindowFrames);
        Assert.Equal((float)SharedFixtures.Real(constants["gainRiseFactor"]), AdaptiveMicrophoneGain.GainRiseFactor);
        Assert.Equal((float)SharedFixtures.Real(constants["gainFallFactor"]), AdaptiveMicrophoneGain.GainFallFactor);
        Assert.Equal((float)SharedFixtures.Real(constants["clipCeiling"]), AdaptiveMicrophoneGain.ClipCeiling);
        Assert.Equal(SharedFixtures.Number(constants["rampSamples"]), AdaptiveMicrophoneGain.RampSamples);
    }

    // Given: fixture のフレーム列（RMS とピーク、繰り返し回数、有効/無効）
    // When: 順に適応ゲインへ取り込む
    // Then: 最後の持続ゲインと適用ゲインが期待値と一致する
    [Theory]
    [MemberData(nameof(GainCases))]
    public void GainMatchesFixture(string name)
    {
        // Given: gain ケースと許容誤差
        var gainFixture = SharedFixtures.Load("audio", version: 2)["gain"]!.AsObject();
        var fixture = FindByName(gainFixture["cases"]!.AsArray(), name);
        var tolerance = SharedFixtures.Real(gainFixture["tolerance"]);
        var isEnabled = fixture["enabled"] is null || SharedFixtures.Flag(fixture["enabled"]);
        var gain = new AdaptiveMicrophoneGain((float)SharedFixtures.Real(fixture["initialGain"]), isEnabled);
        var last = gain.AppliedGain;

        // When: フレーム列を観測する
        foreach (var frame in fixture["frames"]!.AsArray())
        {
            var repeat = SharedFixtures.OptionalNumber(frame?["repeat"]) ?? 1;
            for (var index = 0; index < repeat; index += 1)
            {
                last = gain.ObserveLevel(
                    (float)SharedFixtures.Real(frame?["rms"]),
                    (float)SharedFixtures.Real(frame?["peak"])
                );
            }
        }

        // Then: 持続ゲインと適用ゲインが期待値
        var expected = fixture["expected"]!.AsObject();
        Assert.Equal(SharedFixtures.Real(expected["gain"]), gain.Gain, tolerance);
        Assert.Equal(SharedFixtures.Real(expected["appliedGain"]), last, tolerance);
        Assert.Equal(isEnabled ? gain.AppliedGain : AdaptiveMicrophoneGain.MinimumGain, last);
    }

    // Given: fixture のサンプル列
    // When: フレームの RMS とピークを求める
    // Then: 期待値と一致する
    [Theory]
    [MemberData(nameof(GainLevelCases))]
    public void GainLevelMatchesFixture(string name)
    {
        // Given: level ケース
        var gainFixture = SharedFixtures.Load("audio", version: 2)["gain"]!.AsObject();
        var fixture = FindByName(gainFixture["level"]!.AsArray(), name);
        var tolerance = SharedFixtures.Real(gainFixture["tolerance"]);
        var samples = fixture["samples"]!.AsArray().Select(value => (float)SharedFixtures.Real(value)).ToArray();

        // When: レベルを測る
        var (rms, peak) = AdaptiveMicrophoneGain.MeasureLevel(samples);

        // Then: RMS とピークが期待値
        Assert.Equal(SharedFixtures.Real(fixture["expectedRms"]), rms, tolerance);
        Assert.Equal(SharedFixtures.Real(fixture["expectedPeak"]), peak, tolerance);
    }

    // Given: 前フレームと今回の適用ゲイン、一定値のサンプルで満たした 100 ms frame
    // When: ランプ付きで PCM16 へ変換する
    // Then: 指定インデックスの値が期待値と一致する
    [Theory]
    [MemberData(nameof(GainRampCases))]
    public void GainRampMatchesFixture(string name)
    {
        // Given: ramp ケース
        var gainFixture = SharedFixtures.Load("audio", version: 2)["gain"]!.AsObject();
        var fixture = FindByName(gainFixture["ramp"]!.AsArray(), name);
        var frame = new float[AdaptiveMicrophoneGain.FrameSamples];
        Array.Fill(frame, (float)SharedFixtures.Real(fixture["sample"]));

        // When: ランプ付きで変換する
        var encoded = Pcm16LittleEndianEncoder.EncodeWithRamp(
            frame,
            (float)SharedFixtures.Real(fixture["previousAppliedGain"]),
            (float)SharedFixtures.Real(fixture["appliedGain"]),
            AdaptiveMicrophoneGain.RampSamples
        );

        // Then: 各インデックスの PCM16 が期待値
        var indices = fixture["indices"]!.AsArray();
        var expected = fixture["expected"]!.AsArray();
        Assert.Equal(indices.Count, expected.Count);
        for (var position = 0; position < indices.Count; position += 1)
        {
            var index = SharedFixtures.Number(indices[position]);
            Assert.Equal(
                (short)SharedFixtures.Number(expected[position]),
                BinaryPrimitives.ReadInt16LittleEndian(encoded.AsSpan(index * 2, 2))
            );
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

    // Given: 雑音フレームを 1 つ観測した適応ゲイン
    // When: 非有限の RMS / ピークのあとに発話フレームを観測する
    // Then: 非有限値は直前の適用ゲインを返して状態を変えず、非有限値を見ていない場合と同じ結果になる
    [Fact]
    public void NonFiniteLevelsDoNotCorruptGainState()
    {
        // Given: 同じ雑音フレームを観測した 2 つの適応ゲイン
        var withNonFinite = new AdaptiveMicrophoneGain(4.0f);
        var reference = new AdaptiveMicrophoneGain(4.0f);
        withNonFinite.ObserveLevel(0.001f, 0.003f);
        reference.ObserveLevel(0.001f, 0.003f);

        // When: 片方だけ非有限値を挟んでから発話フレームを観測する
        Assert.Equal(4.0f, withNonFinite.ObserveLevel(float.NaN, 0.1f));
        Assert.Equal(4.0f, withNonFinite.ObserveLevel(0.02f, float.PositiveInfinity));
        var recovered = withNonFinite.ObserveLevel(0.02f, 0.1f);
        var expected = reference.ObserveLevel(0.02f, 0.1f);

        // Then: 状態は壊れていない
        Assert.Equal(expected, recovered);
        Assert.Equal(reference.Gain, withNonFinite.Gain);
    }

    // Given: NaN / ±Infinity と負のサンプルが混ざった frame
    // When: レベルを測る
    // Then: 有限なサンプルだけから RMS と絶対値ピークを求め、有限なサンプルが無ければ 0
    [Fact]
    public void MeasureLevelIgnoresNonFiniteSamples()
    {
        var (rms, peak) = AdaptiveMicrophoneGain.MeasureLevel([float.NaN, 0.3f, -0.4f, float.PositiveInfinity]);
        var (emptyRms, emptyPeak) = AdaptiveMicrophoneGain.MeasureLevel([float.NaN, float.NegativeInfinity]);

        Assert.Equal(0.3535534f, rms, 0.0005f);
        Assert.Equal(0.4f, peak);
        Assert.Equal(0f, emptyRms);
        Assert.Equal(0f, emptyPeak);
    }

    // Given: 初期ゲイン 4 で雑音 frame を処理した適応ゲイン
    // When: 大きな音（ピーク 0.6）の frame を処理する
    // Then: 先頭は前の適用ゲインからランプし、ランプ後はリミッタで 0.9 付近に収まり、持続ゲインは 20% 減に留まる
    [Fact]
    public void ProcessFrameRampsFromPreviousAppliedGainAndLimitsPeaks()
    {
        // Given: 雑音 frame を処理済み
        var gain = new AdaptiveMicrophoneGain(4.0f);
        var noise = new float[AdaptiveMicrophoneGain.FrameSamples];
        Array.Fill(noise, 0.001f);
        var noisePcm = gain.ProcessFrame(noise);
        Assert.Equal(131, BinaryPrimitives.ReadInt16LittleEndian(noisePcm.AsSpan(0, 2)));

        // When: 大きな音の frame を処理する
        var loud = new float[AdaptiveMicrophoneGain.FrameSamples];
        Array.Fill(loud, 0.6f);
        var loudPcm = gain.ProcessFrame(loud);

        // Then: ランプ先頭はクリップ、ランプ後はリミッタ後の値
        Assert.Equal(AdaptiveMicrophoneGain.FrameSamples * 2, loudPcm.Length);
        Assert.Equal(short.MaxValue, BinaryPrimitives.ReadInt16LittleEndian(loudPcm.AsSpan(0, 2)));
        var lastIndex = AdaptiveMicrophoneGain.FrameSamples - 1;
        Assert.Equal(29490, BinaryPrimitives.ReadInt16LittleEndian(loudPcm.AsSpan(lastIndex * 2, 2)));
        Assert.Equal(1.5f, gain.AppliedGain, 0.0005f);
        Assert.Equal(3.2f, gain.Gain, 0.0005f);
    }

    // Given: 自動ゲインを無効にした適応ゲイン
    // When: 発話相当の frame を処理する
    // Then: ゲイン 1.0 のまま変換し、状態も動かない
    [Fact]
    public void DisabledGainEncodesAtUnity()
    {
        var gain = new AdaptiveMicrophoneGain(isEnabled: false);
        var frame = new float[AdaptiveMicrophoneGain.FrameSamples];
        Array.Fill(frame, 0.1f);

        var encoded = gain.ProcessFrame(frame);

        Assert.False(gain.IsEnabled);
        Assert.Equal(3277, BinaryPrimitives.ReadInt16LittleEndian(encoded.AsSpan(0, 2)));
        Assert.Equal(3277, BinaryPrimitives.ReadInt16LittleEndian(encoded.AsSpan(encoded.Length - 2, 2)));
        Assert.Equal(AdaptiveMicrophoneGain.MinimumGain, gain.Gain);
        Assert.Equal(AdaptiveMicrophoneGain.MinimumGain, gain.AppliedGain);
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

    private static TheoryData<string> GainSectionNames(string section)
    {
        var data = new TheoryData<string>();
        foreach (var item in SharedFixtures.Load("audio", version: 2)["gain"]![section]!.AsArray())
        {
            data.Add(SharedFixtures.Text(item?["name"]));
        }

        return data;
    }

    private static System.Text.Json.Nodes.JsonObject FindByName(System.Text.Json.Nodes.JsonArray items, string name)
    {
        foreach (var item in items)
        {
            if (item is System.Text.Json.Nodes.JsonObject candidate && SharedFixtures.Text(candidate["name"]) == name)
            {
                return candidate;
            }
        }

        throw new Xunit.Sdk.XunitException("no gain case named " + name);
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
