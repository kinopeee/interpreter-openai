using System;
using System.Collections.Generic;
using System.Linq;
using RealtimeTranslator.Core.Audio;
using Xunit;

namespace RealtimeTranslator.Core.Tests;

public sealed class AudioFixtureTests
{
    public static TheoryData<string> PacketizerCases => SharedFixtures.CaseNames("audio", "packetizer");

    public static TheoryData<string> Float32Cases => SharedFixtures.CaseNames("audio", "float32ToPcm16");

    public static TheoryData<string> GainCases => GainCaseNames();

    // Given: shared fixture の音声フォーマット定義
    // When: packetizer の定数と照合する
    // Then: 24 kHz / 100 ms / 2,400 sample / 4,800 byte が一致する
    [Fact]
    public void FormatMatchesFixture()
    {
        // Given: shared audio fixture の format 定数
        var format = SharedFixtures.Load("audio")["format"]!.AsObject();

        // When/Then: packetizer 定数が一致する
        Assert.Equal(SharedFixtures.Number(format["sampleRate"]), Pcm16FramePacketizer.SampleRate);
        Assert.Equal(SharedFixtures.Number(format["bytesPerSample"]), Pcm16FramePacketizer.BytesPerSample);
        Assert.Equal(
            SharedFixtures.Number(format["frameDurationMilliseconds"]),
            Pcm16FramePacketizer.FrameDurationMilliseconds);
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
        var fixture = SharedFixtures.Case("audio", "packetizer", name);
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
        var fixture = SharedFixtures.Load("audio")["packetizerContinuity"]!.AsObject();
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
            emitted.Count / Pcm16FramePacketizer.BytesPerFrame);

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
        var fixture = SharedFixtures.Case("audio", "float32ToPcm16", name);

        // When/Then: 1 サンプルの符号化結果が一致する
        Assert.Equal(
            (short)SharedFixtures.Number(fixture["expected"]),
            Pcm16LittleEndianEncoder.EncodeSample(
                (float)SharedFixtures.Real(fixture["sample"]),
                (float)SharedFixtures.Real(fixture["gain"])));
    }

    // Given: shared fixture の適応ゲイン定数
    // When: C# 実装の定数と照合する
    // Then: 最小/最大ゲイン、目標ピーク、無音/クリップ閾値が一致する
    [Fact]
    public void GainConstantsMatchFixture()
    {
        // Given: gain 定数 fixture
        var constants = SharedFixtures.Load("audio")["gain"]!["constants"]!.AsObject();

        // When/Then: AdaptiveMicrophoneGain 定数が一致する
        Assert.Equal((float)SharedFixtures.Real(constants["minimumGain"]), AdaptiveMicrophoneGain.MinimumGain);
        Assert.Equal((float)SharedFixtures.Real(constants["maximumGain"]), AdaptiveMicrophoneGain.MaximumGain);
        Assert.Equal((float)SharedFixtures.Real(constants["targetPeak"]), AdaptiveMicrophoneGain.TargetPeak);
        Assert.Equal((float)SharedFixtures.Real(constants["silenceFloor"]), AdaptiveMicrophoneGain.SilenceFloor);
        Assert.Equal((float)SharedFixtures.Real(constants["clipThreshold"]), AdaptiveMicrophoneGain.ClipThreshold);
        Assert.Equal(
            (float)SharedFixtures.Real(constants["defaultInitialGain"]),
            AdaptiveMicrophoneGain.DefaultInitialGain);
    }

    // Given: fixture のピーク推移シナリオ
    // When: 順に適応ゲインを更新する
    // Then: 各ステップのゲイン値が期待値と一致する
    [Theory]
    [MemberData(nameof(GainCases))]
    public void GainMatchesFixture(string name)
    {
        // Given: gain ケースと許容誤差
        var gainFixture = SharedFixtures.Load("audio")["gain"]!.AsObject();
        var fixture = FindGainCase(gainFixture, name);
        var tolerance = SharedFixtures.Real(gainFixture["tolerance"]);

        var gain = new AdaptiveMicrophoneGain((float)SharedFixtures.Real(fixture["initialGain"]));
        var last = gain.Gain;

        // When: ピーク列または繰り返しピークを観測する
        if (fixture["repeatPeak"] is { } repeatPeak)
        {
            var repeatCount = SharedFixtures.Number(fixture["repeatCount"]);
            for (var index = 0; index < repeatCount; index += 1)
            {
                last = gain.ObservePeak((float)SharedFixtures.Real(repeatPeak));
            }
        }
        else
        {
            foreach (var peak in fixture["peaks"]!.AsArray())
            {
                last = gain.ObservePeak((float)SharedFixtures.Real(peak));
            }
        }

        // Then: 最終ゲインが期待値
        Assert.Equal(SharedFixtures.Real(fixture["expectedGain"]), last, tolerance);
        Assert.Equal(last, gain.Gain);
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

    // Given: 有限な初期ゲイン
    // When: 非有限ピークのあと有効ピークを観測する
    // Then: 状態は壊れず通常のクリップ減衰が動く
    [Fact]
    public void NonFinitePeaksDoNotCorruptGainState()
    {
        var gain = new AdaptiveMicrophoneGain(4.0f);

        Assert.Equal(4.0f, gain.ObservePeak(float.NaN));
        Assert.Equal(4.0f, gain.ObservePeak(float.PositiveInfinity));
        var recovered = gain.ObservePeak(0.3f);

        Assert.True(float.IsFinite(recovered));
        Assert.InRange(recovered, AdaptiveMicrophoneGain.MinimumGain, AdaptiveMicrophoneGain.MaximumGain);
        Assert.Equal(0.5f / 0.3f, recovered, 0.01f);
    }

    private static TheoryData<string> GainCaseNames()
    {
        var data = new TheoryData<string>();
        foreach (var item in SharedFixtures.Load("audio")["gain"]!["cases"]!.AsArray())
        {
            data.Add(SharedFixtures.Text(item?["name"]));
        }

        return data;
    }

    private static System.Text.Json.Nodes.JsonObject FindGainCase(
        System.Text.Json.Nodes.JsonObject gainFixture,
        string name)
    {
        foreach (var item in gainFixture["cases"]!.AsArray())
        {
            if (item is System.Text.Json.Nodes.JsonObject candidate
                && SharedFixtures.Text(candidate["name"]) == name)
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
