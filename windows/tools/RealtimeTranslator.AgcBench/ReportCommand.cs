using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace RealtimeTranslator.AgcBench;

/// <summary>process-summary.json と transcribe 結果 JSON から比較レポート markdown を生成する。</summary>
internal static class ReportCommand
{
    public static int Run(CliOptions options)
    {
        var processedDir = options.Require("processed");
        var outPath = options.Require("out");
        var resultsDir = options.Get("results");

        var corpus = Corpus.Load(processedDir);
        var summary =
            JsonSerializer.Deserialize<ProcessSummary>(
                File.ReadAllText(Path.Combine(processedDir, "process-summary.json")),
                Json.Options
            ) ?? new ProcessSummary();
        var results = resultsDir is null || !Directory.Exists(resultsDir)
            ? []
            : LoadResults(resultsDir);

        var markdown = Render(corpus, summary, results);
        File.WriteAllText(outPath, markdown);
        Console.WriteLine($"wrote {outPath}");
        return 0;
    }

    internal static Dictionary<(string Clip, string Variant), List<TranscribeResult>> LoadResults(
        string resultsDir
    )
    {
        var map = new Dictionary<(string, string), List<TranscribeResult>>();
        foreach (var path in Directory.EnumerateFiles(resultsDir, "*.json"))
        {
            var result = JsonSerializer.Deserialize<TranscribeResult>(File.ReadAllText(path), Json.Options);
            if (result is null)
            {
                continue;
            }

            if (!map.TryGetValue((result.Clip, result.Variant), out var list))
            {
                list = [];
                map[(result.Clip, result.Variant)] = list;
            }

            list.Add(result);
        }

        return map;
    }

    internal static string Render(
        Corpus corpus,
        ProcessSummary summary,
        Dictionary<(string Clip, string Variant), List<TranscribeResult>> results
    )
    {
        var variantSet = new SortedSet<string>(StringComparer.Ordinal);
        var runCount = 0;
        foreach (var entry in summary.Results)
        {
            variantSet.Add(entry.Variant);
        }

        foreach (var list in results.Values)
        {
            runCount += list.Count;
        }

        var builder = new StringBuilder();
        builder.AppendLine("# AGC バリアント比較レポート");
        builder.AppendLine();
        builder.AppendLine(
            CultureInfo.InvariantCulture,
            $"clips: {corpus.Clips.Count} / variants: {variantSet.Count} / transcribe runs: {runCount}"
        );

        foreach (var clip in corpus.Clips)
        {
            builder.AppendLine();
            builder.AppendLine(CultureInfo.InvariantCulture, $"## {clip.Id}");
            builder.AppendLine();
            builder.AppendLine(
                "| variant | maxGain | clippedSamples | speechOutRms | error rate mean (min–max) | firstDelta mean/min/max (ms, from onset) | falseSubtitleChars mean | runs with error |"
            );
            builder.AppendLine("|---|---|---|---|---|---|---|---|");
            foreach (var variant in variantSet)
            {
                var clipSummary = summary.Results.Find(
                    entry => entry.Clip == clip.Id && entry.Variant == variant
                );
                results.TryGetValue((clip.Id, variant), out var runs);
                runs ??= [];
                builder.AppendLine(RenderRow(clip, variant, clipSummary, runs));
            }
        }

        return builder.ToString();
    }

    private static string RenderRow(
        CorpusClip clip,
        string variant,
        ClipVariantSummary? summary,
        List<TranscribeResult> runs
    )
    {
        static string MissingOr(string? value) => value ?? "–";

        var maxGain = summary is { } s ? s.MaxGain.ToString("0.###", CultureInfo.InvariantCulture) : null;
        var clipped = summary is { } s2 ? s2.ClippedSamples.ToString(CultureInfo.InvariantCulture) : null;
        var speechRms = summary is { } s3
            ? s3.SpeechOutRms.ToString("0.#####", CultureInfo.InvariantCulture)
            : null;

        string? errorRate = null;
        string? firstDelta = null;
        string? falseChars = null;
        string? errorRuns = null;
        if (runs.Count > 0)
        {
            if (!clip.ExpectSilence)
            {
                var rates = new List<double>();
                foreach (var run in runs)
                {
                    rates.Add(TextMetrics.ErrorRate(clip.Language, clip.Reference, run.Transcript));
                }

                errorRate = FormatMeanMinMax(rates, "0.###");
            }

            var deltas = new List<double>();
            foreach (var run in runs)
            {
                if (run.FirstDeltaFromOnsetMs is { } delta)
                {
                    deltas.Add(delta);
                }
            }

            if (deltas.Count > 0)
            {
                firstDelta = FormatMeanMinMax(deltas, "0");
            }

            if (clip.ExpectSilence)
            {
                var chars = new List<double>();
                foreach (var run in runs)
                {
                    chars.Add(TextMetrics.FalseSubtitleChars(run.Transcript));
                }

                falseChars = chars.Count == 0 ? null : Mean(chars).ToString("0.#", CultureInfo.InvariantCulture);
            }

            var errorCount = 0;
            foreach (var run in runs)
            {
                if (run.Error is not null)
                {
                    errorCount += 1;
                }
            }

            errorRuns = string.Create(CultureInfo.InvariantCulture, $"{errorCount}/{runs.Count}");
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"| {variant} | {MissingOr(maxGain)} | {MissingOr(clipped)} | {MissingOr(speechRms)} | {MissingOr(errorRate)} | {MissingOr(firstDelta)} | {MissingOr(falseChars)} | {MissingOr(errorRuns)} |"
        );
    }

    private static double Mean(List<double> values)
    {
        double sum = 0;
        foreach (var value in values)
        {
            sum += value;
        }

        return sum / values.Count;
    }

    private static string FormatMeanMinMax(List<double> values, string format)
    {
        var mean = Mean(values);
        var min = double.MaxValue;
        var max = double.MinValue;
        foreach (var value in values)
        {
            min = Math.Min(min, value);
            max = Math.Max(max, value);
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{mean.ToString(format, CultureInfo.InvariantCulture)} ({min.ToString(format, CultureInfo.InvariantCulture)}–{max.ToString(format, CultureInfo.InvariantCulture)})"
        );
    }
}
