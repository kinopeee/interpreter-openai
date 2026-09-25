using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace RealtimeTranslator.AgcBench;

/// <summary>scenarios.json からコーパス WAV と corpus.json を決定的に生成する。</summary>
internal static class ComposeCommand
{
    public static int Run(CliOptions options)
    {
        var scenariosPath = options.Require("scenarios");
        var outDir = options.Require("out");
        var scenariosParent = Path.GetDirectoryName(scenariosPath);
        var scenariosDir = Path.GetFullPath(string.IsNullOrEmpty(scenariosParent) ? "." : scenariosParent);
        var scenarios =
            JsonSerializer.Deserialize<Scenarios>(File.ReadAllText(scenariosPath), Json.Options)
            ?? throw new InvalidDataException($"{scenariosPath}: scenarios.json の解析に失敗しました。");
        if (scenarios.SampleRate != WavFile.RequiredSampleRate)
        {
            throw new InvalidDataException(
                $"{scenariosPath}: sampleRate は {WavFile.RequiredSampleRate} のみ対応します。"
            );
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var duplicates = new List<string>();
        foreach (var scenario in scenarios.Items)
        {
            if (!seen.Add(scenario.Id) && !duplicates.Contains(scenario.Id))
            {
                duplicates.Add(scenario.Id);
            }
        }

        if (duplicates.Count > 0)
        {
            throw new InvalidDataException(
                $"{scenariosPath}: シナリオ id が重複しています: {string.Join(",", duplicates)}"
            );
        }

        Directory.CreateDirectory(outDir);
        var corpus = new Corpus();
        foreach (var scenario in scenarios.Items)
        {
            var (samples, corpusClip) = BuildClip(scenario, scenariosDir);
            var fileName = scenario.Id + ".wav";
            WavFile.Write(Path.Combine(outDir, fileName), samples);
            corpus.Clips.Add(corpusClip with { File = fileName });
            Console.WriteLine($"composed {scenario.Id}: {samples.Length} samples");
        }

        corpus.Save(outDir);
        return 0;
    }

    /// <summary>テストからも使えるよう 1 シナリオ分のサンプル列を返す。</summary>
    internal static (float[] Samples, CorpusClip Clip) BuildClip(Scenario scenario, string baseDir)
    {
        var leading = scenario.LeadingSilenceMs * WavFile.RequiredSampleRate / 1000;
        var trailing = scenario.TrailingSilenceMs * WavFile.RequiredSampleRate / 1000;

        float[] voice;
        if (scenario.Base is null)
        {
            voice = [];
        }
        else
        {
            voice = WavFile.Read(Path.Combine(baseDir, scenario.Base));
            var scale = (float)Math.Pow(10.0, scenario.GainDb / 20.0);
            for (var index = 0; index < voice.Length; index += 1)
            {
                voice[index] *= scale;
            }
        }

        var total = leading + voice.Length + trailing;
        if (total == 0)
        {
            throw new InvalidDataException($"{scenario.Id}: 出力が 0 サンプルです。");
        }

        var samples = new float[total];
        Array.Copy(voice, 0, samples, leading, voice.Length);

        if (scenario.Noise is { } noise)
        {
            AddNoise(samples, noise, scenario.Seed);
        }

        foreach (var click in scenario.Clicks)
        {
            AddClick(samples, click);
        }

        var clip = new CorpusClip
        {
            Id = scenario.Id,
            File = scenario.Id + ".wav",
            Language = scenario.Language,
            Pair = scenario.Pair,
            Reference = scenario.Reference,
            SpeechOnsetMs = scenario.LeadingSilenceMs,
            ExpectSilence = scenario.Base is null,
        };
        return (samples, clip);
    }

    private static void AddNoise(float[] samples, NoiseSpec noise, int seed)
    {
        if (!string.Equals(noise.Kind, "white", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"noise kind '{noise.Kind}' は未対応です (white のみ)。");
        }

        var random = new Random(seed);
        var raw = new float[samples.Length];
        for (var index = 0; index < raw.Length; index += 1)
        {
            raw[index] = (float)((random.NextDouble() * 2.0) - 1.0);
        }

        // dip 区間と通常区間で目標 RMS を変えてスケールする。
        var dipEvery = noise.DipEveryMs is { } every ? every * WavFile.RequiredSampleRate / 1000 : 0;
        var dipLength = noise.DipMs * WavFile.RequiredSampleRate / 1000;
        var normalRms = RmsOfSegments(raw, i => !IsDip(i, dipEvery, dipLength));
        var dipRawRms = RmsOfSegments(raw, i => IsDip(i, dipEvery, dipLength));
        for (var index = 0; index < samples.Length; index += 1)
        {
            if (IsDip(index, dipEvery, dipLength))
            {
                var scale = dipRawRms > 0 ? noise.DipRms / dipRawRms : 0.0;
                samples[index] += (float)(raw[index] * scale);
            }
            else
            {
                var scale = normalRms > 0 ? noise.Rms / normalRms : 0.0;
                samples[index] += (float)(raw[index] * scale);
            }
        }
    }

    private static bool IsDip(int index, int dipEvery, int dipLength) =>
        dipEvery > 0 && (index % dipEvery) < dipLength;

    private static double RmsOfSegments(float[] samples, Func<int, bool> include)
    {
        double sum = 0;
        var count = 0;
        for (var index = 0; index < samples.Length; index += 1)
        {
            if (!include(index))
            {
                continue;
            }

            sum += (double)samples[index] * samples[index];
            count += 1;
        }

        return count == 0 ? 0.0 : Math.Sqrt(sum / count);
    }

    private static void AddClick(float[] samples, ClickSpec click)
    {
        var start = click.AtMs * WavFile.RequiredSampleRate / 1000;
        var length = click.DurationMs * WavFile.RequiredSampleRate / 1000;
        var end = Math.Min(samples.Length, start + length);
        for (var index = start; index < end; index += 1)
        {
            samples[index] = Math.Clamp(samples[index] + (float)click.Peak, -1f, 1f);
        }
    }
}
