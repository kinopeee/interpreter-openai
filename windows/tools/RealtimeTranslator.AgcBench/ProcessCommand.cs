using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using RealtimeTranslator.Core.Audio;

namespace RealtimeTranslator.AgcBench;

/// <summary>corpus の各クリップをバリアント別に処理し、WAV とゲイン trace、summary を出力する。</summary>
internal static class ProcessCommand
{
    public static int Run(CliOptions options)
    {
        var corpusDir = options.Require("corpus");
        var outDir = options.Require("out");
        var variantNames = options.GetList("variants");
        if (variantNames.Count == 0)
        {
            variantNames = GainVariants.AllNames;
        }

        var corpus = Corpus.Load(corpusDir);
        Directory.CreateDirectory(outDir);

        // outDir は process が占有するので、古いクリップ/バリアントの残滓を消す。
        foreach (var stale in Directory.EnumerateFiles(outDir, "*.wav"))
        {
            File.Delete(stale);
        }

        foreach (var stale in Directory.EnumerateFiles(outDir, "*.trace.csv"))
        {
            File.Delete(stale);
        }

        var summary = new ProcessSummary();
        foreach (var clip in corpus.Clips)
        {
            var samples = WavFile.Read(Path.Combine(corpusDir, clip.File));
            foreach (var name in variantNames)
            {
                var variant = GainVariants.Create(name);
                var result = RunClip(clip, samples, variant, outDir);
                summary.Results.Add(result);
                Console.WriteLine($"processed {clip.Id}.{variant.Name}: {result.Frames} frames");
            }
        }

        corpus.Save(outDir);
        File.WriteAllText(
            Path.Combine(outDir, "process-summary.json"),
            JsonSerializer.Serialize(summary, Json.Options) + "\n"
        );
        return 0;
    }

    internal static ClipVariantSummary RunClip(
        CorpusClip clip,
        float[] samples,
        IGainVariant variant,
        string outDir
    )
    {
        var accumulator = new Float32FrameAccumulator();
        var frames = new List<float[]>();
        frames.AddRange(accumulator.Append(samples));
        if (accumulator.FlushWithSilencePadding() is { } tail)
        {
            frames.Add(tail);
        }

        var wavPath = Path.Combine(outDir, $"{clip.Id}.{variant.Name}.wav");
        var tracePath = Path.Combine(outDir, $"{clip.Id}.{variant.Name}.trace.csv");
        using var wavStream = new MemoryStream();
        using var traceWriter = new StreamWriter(tracePath);
        traceWriter.WriteLine("frame,inRms,inPeak,gain,appliedGain,outPeak,clippedSamples");

        var maxGain = float.MinValue;
        var minGain = float.MaxValue;
        var framesAtMaxGain = 0;
        var totalClipped = 0;
        double speechSumSquares = 0;
        var speechSampleCount = 0;

        for (var frameIndex = 0; frameIndex < frames.Count; frameIndex += 1)
        {
            var frame = frames[frameIndex];
            var (inRms, inPeak) = AdaptiveMicrophoneGain.FrameStatistics(frame);
            var pcm = variant.ProcessFrame(frame);
            wavStream.Write(pcm);

            var outPeak = 0f;
            var clipped = 0;
            var frameStartMs = frameIndex * 100;
            var isSpeechRegion = frameStartMs >= clip.SpeechOnsetMs;
            for (var index = 0; index < pcm.Length; index += 2)
            {
                var value = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(index, 2));
                var magnitude = Math.Abs((int)value);
                outPeak = MathF.Max(outPeak, magnitude / (float)short.MaxValue);
                if (magnitude == short.MaxValue)
                {
                    clipped += 1;
                }

                if (isSpeechRegion)
                {
                    var normalized = value / (float)short.MaxValue;
                    speechSumSquares += (double)normalized * normalized;
                    speechSampleCount += 1;
                }
            }

            totalClipped += clipped;
            var gain = variant.Gain;
            maxGain = MathF.Max(maxGain, gain);
            minGain = MathF.Min(minGain, gain);
            if (gain >= AdaptiveMicrophoneGain.MaximumGain)
            {
                framesAtMaxGain += 1;
            }

            traceWriter.WriteLine(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{frameIndex},{inRms},{inPeak},{gain},{variant.AppliedGain},{outPeak},{clipped}"
                )
            );
        }

        WavFile.WritePcm16(wavPath, wavStream.ToArray());
        return new ClipVariantSummary
        {
            Clip = clip.Id,
            Variant = variant.Name,
            Frames = frames.Count,
            MaxGain = maxGain,
            MinGain = minGain,
            FramesAtMaxGain = framesAtMaxGain,
            ClippedSamples = totalClipped,
            SpeechOutRms = speechSampleCount == 0 ? 0.0 : Math.Sqrt(speechSumSquares / speechSampleCount),
        };
    }
}

internal sealed record ClipVariantSummary
{
    public required string Clip { get; init; }
    public required string Variant { get; init; }
    public int Frames { get; init; }
    public float MaxGain { get; init; }
    public float MinGain { get; init; }
    public int FramesAtMaxGain { get; init; }
    public int ClippedSamples { get; init; }
    public double SpeechOutRms { get; init; }
}

internal sealed record ProcessSummary
{
    public List<ClipVariantSummary> Results { get; init; } = [];
}
