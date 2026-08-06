using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace RealtimeTranslator.Core.Tests;

/// <summary>`shared/fixtures/v1` を読み込むヘルパ。fixture が唯一の正本。</summary>
public static class SharedFixtures
{
    public static JsonObject Load(string name)
    {
        var path = Path.Combine(DirectoryPath.Value, name + ".json");
        var node = JsonNode.Parse(File.ReadAllText(path))
            ?? throw new InvalidOperationException($"{path} is empty");
        return node.AsObject();
    }

    public static JsonArray Section(string fixture, string section) =>
        Load(fixture)[section]?.AsArray()
        ?? throw new InvalidOperationException($"{fixture}.{section} is missing");

    /// <summary>xUnit の theory 名として fixture のケース名をそのまま使う。</summary>
    public static TheoryData<string> CaseNames(string fixture, string section)
    {
        var data = new TheoryData<string>();
        foreach (var item in Section(fixture, section))
        {
            data.Add(Text(item?["name"]));
        }

        return data;
    }

    public static JsonObject Case(string fixture, string section, string name)
    {
        foreach (var item in Section(fixture, section))
        {
            if (item is JsonObject candidate && Text(candidate["name"]) == name)
            {
                return candidate;
            }
        }

        throw new InvalidOperationException($"{fixture}.{section} has no case named {name}");
    }

    public static string Text(JsonNode? node) =>
        node?.GetValue<string>() ?? throw new InvalidOperationException("expected a string");

    public static string? OptionalText(JsonNode? node) => node?.GetValue<string>();

    public static int Number(JsonNode? node) =>
        node?.GetValue<int>() ?? throw new InvalidOperationException("expected a number");

    public static int? OptionalNumber(JsonNode? node) => node?.GetValue<int>();

    public static double Real(JsonNode? node) =>
        node?.GetValue<double>() ?? throw new InvalidOperationException("expected a number");

    public static bool Flag(JsonNode? node) =>
        node?.GetValue<bool>() ?? throw new InvalidOperationException("expected a bool");

    /// <summary>キー順を無視した JSON の意味比較。</summary>
    public static bool JsonEquals(JsonNode? left, JsonNode? right) =>
        JsonNode.DeepEquals(left, right);

    public static JsonNode ParseUtf8(byte[] utf8Json) =>
        JsonNode.Parse(utf8Json) ?? throw new InvalidOperationException("encoded payload is null");

    public static string Canonical(JsonNode? node) =>
        node?.ToJsonString(CanonicalOptions) ?? "null";

    private static readonly JsonSerializerOptions CanonicalOptions = new() { WriteIndented = false };

    /// <summary>ビルド出力から repo root を遡って探す。fixture をテスト出力へコピーしない。</summary>
    private static readonly Lazy<string> DirectoryPath = new(() =>
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "shared", "fixtures", "v1");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("shared/fixtures/v1 not found above " + AppContext.BaseDirectory);
    });
}
