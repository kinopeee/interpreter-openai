using System;
using System.Linq;
using System.Text.Json.Nodes;
using RealtimeTranslator.Core.Audio;
using RealtimeTranslator.Core.Localization;
using RealtimeTranslator.Core.OpenAI;
using RealtimeTranslator.Core.Settings;
using Xunit;

namespace RealtimeTranslator.Core.Tests;

/// <summary><c>shared/fixtures/v1/settings.json</c> が永続設定契約の正本。</summary>
public sealed class SettingsFixtureTests
{
    public static TheoryData<string> DecodeCaseNames => SharedFixtures.CaseNames("settings", "decode");

    [Theory]
    [MemberData(nameof(DecodeCaseNames))]
    public void DecodeRestoresExpectedSettings(string name)
    {
        var fixture = SharedFixtures.Case("settings", "decode", name);

        // Given: fixture の保存値をそのまま settings.json の JSON として与える。
        var input = fixture["input"]!.AsObject().DeepClone().AsObject();
        foreach (var property in input.ToList())
        {
            input[property.Key] = ResolveStoredValue(property.Key, property.Value);
        }

        // When: decode し、ペア変更ケースはヒント更新へ進める。
        var decoded = AppSettingsCodec.Decode(input.ToJsonString());
        var effective = decoded;
        if (fixture["pairChange"] is { } change)
        {
            effective = AppSettingsHintRefresh.ApplyLanguagePair(
                decoded,
                LanguagePairExtensions.ParseLanguagePair(SharedFixtures.Text(change))
            );
        }

        // Then: expected の各フィールドが一致する。
        foreach (var property in fixture["expected"]!.AsObject())
        {
            AssertField(property.Key, property.Value, effective, name);
        }
    }

    [Fact]
    public void DefaultsMatchFixture()
    {
        // Given / When / Then: fixture の defaults と AppSettingsData.Default が一致する。
        var defaults = SharedFixtures.Load("settings")["defaults"]!.AsObject();
        foreach (var property in defaults)
        {
            AssertField(property.Key, property.Value, AppSettingsData.Default, "defaults");
        }
    }

    [Theory]
    [MemberData(nameof(DecodeCaseNames))]
    public void EncodeDecodeRoundTrips(string name)
    {
        var fixture = SharedFixtures.Case("settings", "decode", name);
        var input = fixture["input"]!.AsObject().DeepClone().AsObject();
        foreach (var property in input.ToList())
        {
            input[property.Key] = ResolveStoredValue(property.Key, property.Value);
        }

        // Given / When / Then: encode → decode が値を保つ。
        var decoded = AppSettingsCodec.Decode(input.ToJsonString());
        Assert.Equal(decoded, AppSettingsCodec.Decode(AppSettingsCodec.Encode(decoded)));
    }

    private static JsonNode? ResolveStoredValue(string key, JsonNode? value)
    {
        if (value is not JsonValue jsonValue || !jsonValue.TryGetValue<string>(out var text))
        {
            return value;
        }

        return ResolveHintText(key, text, LanguagePair.JaEn);
    }

    private static string ResolveHintText(string key, string text, LanguagePair resultPair)
    {
        const string prefix = "defaultForPair:";
        if (!text.StartsWith(prefix, StringComparison.Ordinal))
        {
            return text;
        }

        var pair = LanguagePairExtensions.ParseLanguagePair(text[prefix.Length..]);
        return key == "transcriptionPrompt"
            ? RealtimeSessionTuning.DefaultPromptForPair(pair)
            : RealtimeSessionTuning.KeywordsText(RealtimeSessionTuning.DefaultKeywordsForPair(pair));
    }

    private static void AssertField(string key, JsonNode? expected, AppSettingsData actual, string caseName)
    {
        switch (key)
        {
            case "fontSize":
                Assert.Equal(expected!.GetValue<double>(), actual.FontSize);
                break;
            case "hasCustomOverlayOrigin":
                Assert.Equal(SharedFixtures.Flag(expected), actual.HasCustomOverlayOrigin);
                break;
            case "overlayOriginX":
                Assert.Equal(expected!.GetValue<double>(), actual.OverlayOriginX);
                break;
            case "overlayOriginY":
                Assert.Equal(expected!.GetValue<double>(), actual.OverlayOriginY);
                break;
            case "acceptedConsentVersion":
                Assert.Equal(SharedFixtures.Number(expected), actual.AcceptedConsentVersion);
                break;
            case "transcriptionPrompt":
                Assert.Equal(
                    ResolveHintText(key, SharedFixtures.Text(expected), actual.LanguagePair),
                    actual.TranscriptionPrompt
                );
                break;
            case "transcriptionKeywordsText":
                Assert.Equal(
                    ResolveHintText(key, SharedFixtures.Text(expected), actual.LanguagePair),
                    actual.TranscriptionKeywordsText
                );
                break;
            case "noiseReduction":
                Assert.Equal(SharedFixtures.Text(expected), actual.NoiseReduction.ToWireValue());
                break;
            case "transcriptionDelay":
                Assert.Equal(SharedFixtures.Text(expected), actual.TranscriptionDelay.ToWireValue());
                break;
            case "recordSubtitles":
                Assert.Equal(SharedFixtures.Flag(expected), actual.RecordSubtitles);
                break;
            case "languagePair":
                Assert.Equal(SharedFixtures.Text(expected), actual.LanguagePair.ToWireValue());
                break;
            case "uiLanguage":
                Assert.Equal(SharedFixtures.Text(expected), actual.UiLanguage.ToWireValue());
                break;
            case "automaticGainEnabled":
                Assert.Equal(SharedFixtures.Flag(expected), actual.AutomaticGainEnabled);
                break;
            default:
                Assert.Fail($"{caseName}: unknown expected field {key}");
                break;
        }
    }
}
