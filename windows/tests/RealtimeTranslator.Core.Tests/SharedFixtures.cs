using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;


namespace RealtimeTranslator.Core.Tests;

/// <summary>`shared/fixtures/v1` を読み込むヘルパ。fixture が唯一の正本。</summary>
public static class SharedFixtures
{
    public static JsonObject Load(string name, int version = 1)
    {
        var path = Path.Combine(DirectoryPath(version), name + ".json");
        var node = JsonNode.Parse(File.ReadAllText(path))
            ?? throw new InvalidOperationException($"{path} is empty");
        return node.AsObject();
    }

    public static JsonArray Section(string fixture, string section, int version = 1) =>
        Load(fixture, version)[section]?.AsArray()
        ?? throw new InvalidOperationException($"{fixture}.{section} is missing");

    /// <summary>xUnit の theory 名として fixture のケース名をそのまま使う。</summary>
    public static TheoryData<string> CaseNames(string fixture, string section, int version = 1)
    {
        var data = new TheoryData<string>();
        foreach (var item in Section(fixture, section, version))
        {
            data.Add(Text(item?["name"]));
        }

        return data;
    }

    public static JsonObject Case(
        string fixture,
        string section,
        string name,
        int version = 1)
    {
        foreach (var item in Section(fixture, section, version))
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
    private static string DirectoryPath(int version) =>
        FindDirectory("shared", "fixtures", $"v{version}");

    /// <summary><c>shared/locales/ui.json</c>。fixtures とは別ディレクトリ。</summary>
    public static string UiCatalogJson => File.ReadAllText(UiCatalogPath);

    public static string UiCatalogPath =>
        Path.Combine(FindDirectory("shared", "locales"), "ui.json");

    private static string FindDirectory(params string[] relativeSegments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var parts = new string[relativeSegments.Length + 1];
            parts[0] = directory.FullName;
            Array.Copy(relativeSegments, 0, parts, 1, relativeSegments.Length);
            var candidate = Path.Combine(parts);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            string.Join("/", relativeSegments) + " not found above " + AppContext.BaseDirectory);
    }
}
