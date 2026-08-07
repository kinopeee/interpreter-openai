using System;
using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RealtimeTranslator.Core.OpenAI;

namespace RealtimeTranslator.Core.Settings;

/// <summary>永続化する UI 設定。API キーは含めない (Credential Manager 側に置く)。</summary>
public sealed record AppSettingsData(
    double FontSize,
    bool HasCustomOverlayOrigin,
    double OverlayOriginX,
    double OverlayOriginY,
    int AcceptedConsentVersion,
    string TranscriptionPrompt,
    string TranscriptionKeywordsText,
    RealtimeTranslationNoiseReduction NoiseReduction,
    RealtimeTranscriptionDelay TranscriptionDelay,
    bool RecordSubtitles)
{
    /// <summary>同意文言を変えたら上げる。上げると再同意を求める。</summary>
    public const int CurrentConsentVersion = 1;

    public const double MinimumFontSize = 18;
    public const double MaximumFontSize = 48;
    public const double DefaultFontSize = 32;

    public static readonly AppSettingsData Default = new(
        DefaultFontSize,
        HasCustomOverlayOrigin: false,
        OverlayOriginX: 0,
        OverlayOriginY: 0,
        AcceptedConsentVersion: 0,
        RealtimeSessionTuning.DefaultPrompt,
        RealtimeSessionTuning.KeywordsText(RealtimeSessionTuning.DefaultKeywords),
        RealtimeTranslationNoiseReduction.FarField,
        RealtimeTranscriptionDelay.Low,
        RecordSubtitles: false);

    public bool HasAcceptedCurrentConsent => AcceptedConsentVersion >= CurrentConsentVersion;

    public RealtimeSessionTuning Tuning() => RealtimeSessionTuning.Make(
        NoiseReduction,
        TranscriptionDelay,
        TranscriptionPrompt,
        TranscriptionKeywordsText);
}

/// <summary>settings.json の読み書き。壊れた値は既定へ倒し、UI が起動できない状態を作らない。</summary>
public static class AppSettingsCodec
{
    public static string Encode(AppSettingsData settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            // Utf8JsonWriter は NaN/Infinity を拒否するため、書き出し前に正規化する。
            writer.WriteNumber("fontSize", ClampFontSize(settings.FontSize));
            writer.WriteBoolean("hasCustomOverlayOrigin", settings.HasCustomOverlayOrigin);
            writer.WriteNumber("overlayOriginX", FiniteOrZero(settings.OverlayOriginX));
            writer.WriteNumber("overlayOriginY", FiniteOrZero(settings.OverlayOriginY));
            writer.WriteNumber("acceptedConsentVersion", settings.AcceptedConsentVersion);
            writer.WriteString("transcriptionPrompt", settings.TranscriptionPrompt);
            writer.WriteString("transcriptionKeywordsText", settings.TranscriptionKeywordsText);
            writer.WriteString("noiseReduction", settings.NoiseReduction.ToWireValue());
            writer.WriteString("transcriptionDelay", settings.TranscriptionDelay.ToWireValue());
            writer.WriteBoolean("recordSubtitles", settings.RecordSubtitles);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static AppSettingsData Decode(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return AppSettingsData.Default;
        }

        if (root is not JsonObject dictionary)
        {
            return AppSettingsData.Default;
        }

        var defaults = AppSettingsData.Default;
        return new AppSettingsData(
            ClampFontSize(Number(dictionary, "fontSize") ?? defaults.FontSize),
            Boolean(dictionary, "hasCustomOverlayOrigin") ?? defaults.HasCustomOverlayOrigin,
            Number(dictionary, "overlayOriginX") ?? defaults.OverlayOriginX,
            Number(dictionary, "overlayOriginY") ?? defaults.OverlayOriginY,
            (int?)Number(dictionary, "acceptedConsentVersion") ?? defaults.AcceptedConsentVersion,
            Text(dictionary, "transcriptionPrompt") ?? defaults.TranscriptionPrompt,
            Text(dictionary, "transcriptionKeywordsText") ?? defaults.TranscriptionKeywordsText,
            NoiseReduction(dictionary) ?? defaults.NoiseReduction,
            TranscriptionDelay(dictionary) ?? defaults.TranscriptionDelay,
            Boolean(dictionary, "recordSubtitles") ?? false);
    }

    public static double ClampFontSize(double value) =>
        double.IsFinite(value)
            ? Math.Clamp(value, AppSettingsData.MinimumFontSize, AppSettingsData.MaximumFontSize)
            : AppSettingsData.DefaultFontSize;

    private static double FiniteOrZero(double value) => double.IsFinite(value) ? value : 0;

    private static double? Number(JsonObject dictionary, string name) =>
        dictionary[name] is JsonValue value && value.TryGetValue<double>(out var number) ? number : null;

    private static bool? Boolean(JsonObject dictionary, string name) =>
        dictionary[name] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;

    private static string? Text(JsonObject dictionary, string name) =>
        dictionary[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static RealtimeTranslationNoiseReduction? NoiseReduction(JsonObject dictionary)
    {
        var wireValue = Text(dictionary, "noiseReduction");
        if (wireValue is null)
        {
            return null;
        }

        try
        {
            return RealtimeTranslationWireValues.ParseNoiseReduction(wireValue);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static RealtimeTranscriptionDelay? TranscriptionDelay(JsonObject dictionary)
    {
        var wireValue = Text(dictionary, "transcriptionDelay");
        if (wireValue is null)
        {
            return null;
        }

        try
        {
            return RealtimeTranslationWireValues.ParseTranscriptionDelay(wireValue);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
