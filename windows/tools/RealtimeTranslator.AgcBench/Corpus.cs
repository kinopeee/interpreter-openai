using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using RealtimeTranslator.Core.Audio;

namespace RealtimeTranslator.AgcBench;

/// <summary>corpus.json の 1 クリップ。</summary>
internal sealed record CorpusClip
{
    public required string Id { get; init; }
    public required string File { get; init; }
    public string Language { get; init; } = "ja";
    public string Pair { get; init; } = "ja-en";
    public string Reference { get; init; } = "";
    public int SpeechOnsetMs { get; init; }
    public bool ExpectSilence { get; init; }

    public LanguagePair LanguagePair => LanguagePairExtensions.ParseLanguagePair(Pair);
}

/// <summary>corpus.json 全体。</summary>
internal sealed record Corpus
{
    public int SampleRate { get; init; } = WavFile.RequiredSampleRate;
    public List<CorpusClip> Clips { get; init; } = [];

    public static Corpus Load(string directory)
    {
        var path = Path.Combine(directory, "corpus.json");
        var corpus =
            JsonSerializer.Deserialize<Corpus>(File.ReadAllText(path), Json.Options)
            ?? throw new InvalidDataException($"{path}: corpus.json の解析に失敗しました。");
        return corpus;
    }

    public void Save(string directory)
    {
        var path = Path.Combine(directory, "corpus.json");
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json.Options) + "\n");
    }
}

/// <summary>scenarios.json (compose 入力)。</summary>
internal sealed record Scenarios
{
    public int SampleRate { get; init; } = WavFile.RequiredSampleRate;

    [JsonPropertyName("scenarios")]
    public List<Scenario> Items { get; init; } = [];
}

internal sealed record Scenario
{
    public required string Id { get; init; }
    public string? Base { get; init; }
    public string Language { get; init; } = "ja";
    public string Pair { get; init; } = "ja-en";
    public string Reference { get; init; } = "";
    public double GainDb { get; init; }
    public int LeadingSilenceMs { get; init; }
    public int TrailingSilenceMs { get; init; }
    public NoiseSpec? Noise { get; init; }
    public List<ClickSpec> Clicks { get; init; } = [];
    public int Seed { get; init; } = 1;
}

internal sealed record NoiseSpec
{
    public string Kind { get; init; } = "white";
    public double Rms { get; init; }
    public int? DipEveryMs { get; init; }
    public int DipMs { get; init; }
    public double DipRms { get; init; }
}

internal sealed record ClickSpec
{
    public int AtMs { get; init; }
    public double Peak { get; init; }
    public int DurationMs { get; init; }
}

internal static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
