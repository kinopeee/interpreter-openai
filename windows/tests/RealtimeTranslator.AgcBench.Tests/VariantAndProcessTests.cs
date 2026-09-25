using System;
using System.IO;
using System.Text.Json;
using RealtimeTranslator.AgcBench;
using RealtimeTranslator.Core.Audio;
using Xunit;

namespace RealtimeTranslator.AgcBench.Tests;

public class VariantTests
{
    private static float[] Frame(float value)
    {
        var frame = new float[AdaptiveMicrophoneGain.FrameSamples];
        Array.Fill(frame, value);
        return frame;
    }

    // Given: off バリアント
    // When: フレームを処理する
    // Then: エンコーダ gain=1 と同一のバイト列で、Gain/AppliedGain は常に 1
    [Fact]
    public void OffVariantMatchesPlainEncoder()
    {
        var frame = Frame(0.25f);
        var variant = GainVariants.Create("off");

        Assert.Equal(Pcm16LittleEndianEncoder.Encode(frame, 1f), variant.ProcessFrame(frame));
        Assert.Equal(1f, variant.Gain);
        Assert.Equal(1f, variant.AppliedGain);
    }

    // Given: v2 バリアントと素の AdaptiveMicrophoneGain
    // When: 同じフレーム列を処理する
    // Then: 出力バイト列が一致する
    [Fact]
    public void V2VariantMatchesAdaptiveMicrophoneGain()
    {
        var variant = GainVariants.Create("v2");
        var agc = new AdaptiveMicrophoneGain();
        for (var index = 0; index < 5; index += 1)
        {
            var frame = Frame(0.01f * (index + 1));
            Assert.Equal(agc.Process(frame), variant.ProcessFrame(frame));
        }

        Assert.Equal(agc.Gain, variant.Gain);
        Assert.Equal(agc.AppliedGain, variant.AppliedGain);
    }

    // Given: クリップに達する大きなフレームのあと静かなフレームを続ける
    // When: v1 バリアントで処理する
    // Then: クリップでゲインが下がり、その後の静かな入力でゆっくり上がる
    [Fact]
    public void V1VariantAttacksClippingAndReleasesSlowly()
    {
        var variant = GainVariants.Create("v1");
        _ = variant.ProcessFrame(Frame(0.95f));
        var afterClip = variant.Gain;
        Assert.True(afterClip < 4f, $"gain should drop, got {afterClip}");

        var previous = afterClip;
        for (var index = 0; index < 20; index += 1)
        {
            _ = variant.ProcessFrame(Frame(0.02f));
            Assert.True(variant.Gain >= previous, "gain should not drop on quiet frames");
            previous = variant.Gain;
        }

        Assert.True(variant.Gain > afterClip, $"gain should recover, got {variant.Gain}");
    }
}

public class ProcessCommandTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"agcbench-{Guid.NewGuid():N}");

    public ProcessCommandTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }

    // Given: 2.5 フレーム分のクリップと speechOnsetMs=100
    // When: process を off バリアントで実行する
    // Then: 端数が zero-padding されて 3 フレーム、trace は 3 行、summary と trace が整合し、
    //       speechOutRms は onset 前のフレームを含まない
    [Fact]
    public void ProcessPadsPartialFrameAndSummarizes()
    {
        var corpusDir = Path.Combine(_dir, "corpus");
        var outDir = Path.Combine(_dir, "out");
        Directory.CreateDirectory(corpusDir);
        var samples = new float[2400 * 2 + 1200];
        Array.Fill(samples, 0.5f);
        Array.Fill(samples, 0f, 0, 2400); // 先頭フレームだけ無音
        WavFile.Write(Path.Combine(corpusDir, "clip.wav"), samples);
        new Corpus
        {
            Clips =
            [
                new CorpusClip
                {
                    Id = "clip",
                    File = "clip.wav",
                    SpeechOnsetMs = 100,
                },
            ],
        }.Save(corpusDir);

        var options = CliOptions.Parse(["--corpus", corpusDir, "--out", outDir, "--variants", "off"]);
        Assert.Equal(0, ProcessCommand.Run(options));

        var traceLines = File.ReadAllLines(Path.Combine(outDir, "clip.off.trace.csv"));
        Assert.Equal(4, traceLines.Length); // header + 3 行

        var processed = WavFile.Read(Path.Combine(outDir, "clip.off.wav"));
        Assert.Equal(2400 * 3, processed.Length);

        var summary = JsonSerializer.Deserialize<ProcessSummary>(
            File.ReadAllText(Path.Combine(outDir, "process-summary.json")),
            Json.Options
        );
        var entry = Assert.Single(summary!.Results);
        Assert.Equal(3, entry.Frames);
        Assert.Equal(1f, entry.MaxGain);
        Assert.Equal(1f, entry.MinGain);
        // onset 以降の 2 フレームのみ 0.5 の振幅 → speechOutRms ≈ 0.25 (半分が padding)
        Assert.True(entry.SpeechOutRms > 0.2 && entry.SpeechOutRms < 0.5, $"speechOutRms {entry.SpeechOutRms}");
        // corpus.json が処理ディレクトリへコピーされている
        Assert.Single(Corpus.Load(outDir).Clips);
    }
}
