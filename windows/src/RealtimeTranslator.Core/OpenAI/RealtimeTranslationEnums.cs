using System;

namespace RealtimeTranslator.Core.OpenAI;

/// <summary>翻訳出力先の言語。1 接続につき 1 つ。</summary>
public enum RealtimeTranslationOutputLanguage
{
    English,
    Japanese,
}

/// <summary>入力音声のノイズ低減プロファイル。</summary>
public enum RealtimeTranslationNoiseReduction
{
    NearField,
    FarField,
}

/// <summary>gpt-live-transcribe の遅延/精度トレードオフ。</summary>
public enum RealtimeTranscriptionDelay
{
    Minimal,
    Low,
    Medium,
    High,
    XHigh,
}

/// <summary>enum とプロトコル上の文字列表現の相互変換。</summary>
public static class RealtimeTranslationWireValues
{
    public static string ToWireValue(this RealtimeTranslationOutputLanguage value) => value switch
    {
        RealtimeTranslationOutputLanguage.English => "en",
        RealtimeTranslationOutputLanguage.Japanese => "ja",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static string ToWireValue(this RealtimeTranslationNoiseReduction value) => value switch
    {
        RealtimeTranslationNoiseReduction.NearField => "near_field",
        RealtimeTranslationNoiseReduction.FarField => "far_field",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static string ToWireValue(this RealtimeTranscriptionDelay value) => value switch
    {
        RealtimeTranscriptionDelay.Minimal => "minimal",
        RealtimeTranscriptionDelay.Low => "low",
        RealtimeTranscriptionDelay.Medium => "medium",
        RealtimeTranscriptionDelay.High => "high",
        RealtimeTranscriptionDelay.XHigh => "xhigh",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static RealtimeTranslationOutputLanguage ParseOutputLanguage(string wireValue) => wireValue switch
    {
        "en" => RealtimeTranslationOutputLanguage.English,
        "ja" => RealtimeTranslationOutputLanguage.Japanese,
        _ => throw new ArgumentOutOfRangeException(nameof(wireValue), wireValue, null),
    };

    public static RealtimeTranslationNoiseReduction ParseNoiseReduction(string wireValue) => wireValue switch
    {
        "near_field" => RealtimeTranslationNoiseReduction.NearField,
        "far_field" => RealtimeTranslationNoiseReduction.FarField,
        _ => throw new ArgumentOutOfRangeException(nameof(wireValue), wireValue, null),
    };

    public static RealtimeTranscriptionDelay ParseTranscriptionDelay(string wireValue) => wireValue switch
    {
        "minimal" => RealtimeTranscriptionDelay.Minimal,
        "low" => RealtimeTranscriptionDelay.Low,
        "medium" => RealtimeTranscriptionDelay.Medium,
        "high" => RealtimeTranscriptionDelay.High,
        "xhigh" => RealtimeTranscriptionDelay.XHigh,
        _ => throw new ArgumentOutOfRangeException(nameof(wireValue), wireValue, null),
    };
}
