using RealtimeTranslator.Core.OpenAI;

namespace RealtimeTranslator.Core.Tests;

/// <summary>翻訳セッション設定のテスト用ファクトリ。</summary>
public static class SessionConfigs
{
    public static RealtimeTranslationSessionConfig EnglishTargetWithoutSourceTranscription(
        RealtimeTranslationNoiseReduction? noiseReduction = RealtimeTranslationNoiseReduction.FarField) =>
        new(RealtimeTranslationOutputLanguage.English, null, noiseReduction);

    public static RealtimeTranslationSessionConfig JapaneseTargetWithoutSourceTranscription(
        RealtimeTranslationNoiseReduction? noiseReduction = RealtimeTranslationNoiseReduction.FarField) =>
        new(RealtimeTranslationOutputLanguage.Japanese, null, noiseReduction);
}
