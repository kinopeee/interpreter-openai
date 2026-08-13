using System.Linq;
using RealtimeTranslator.Core.Audio;
using RealtimeTranslator.Core.Localization;
using RealtimeTranslator.Core.OpenAI;
using RealtimeTranslator.Core.Settings;
using Xunit;

namespace RealtimeTranslator.Core.Tests;

public sealed class AppSettingsCodecTests
{
    // Given: 既定から変更した設定
    // When: JSON へ書き出して読み戻す
    // Then: 同じ値が復元される
    [Fact]
    public void EncodeDecodeRoundTripsEveryField()
    {
        var settings = AppSettingsData.Default with
        {
            FontSize = 40,
            HasCustomOverlayOrigin = true,
            OverlayOriginX = 120.5,
            OverlayOriginY = 640.25,
            AcceptedConsentVersion = AppSettingsData.CurrentConsentVersion,
            TranscriptionPrompt = "会議の用語を優先",
            TranscriptionKeywordsText = "Devin\nWASAPI",
            NoiseReduction = RealtimeTranslationNoiseReduction.NearField,
            TranscriptionDelay = RealtimeTranscriptionDelay.High,
            RecordSubtitles = true,
            LanguagePair = LanguagePair.EnEs,
            UiLanguage = UiLanguagePreference.En,
        };

        var restored = AppSettingsCodec.Decode(AppSettingsCodec.Encode(settings));

        Assert.Equal(settings, restored);
        Assert.True(restored.HasAcceptedCurrentConsent);
        Assert.True(restored.RecordSubtitles);
        Assert.Equal(LanguagePair.EnEs, restored.LanguagePair);
        Assert.Equal(UiLanguagePreference.En, restored.UiLanguage);
    }

    // Given: 対応する全言語ペア
    // When: JSON へ書き出して読み戻す
    // Then: 各ペアが欠落なく復元される
    [Theory]
    [InlineData(LanguagePair.JaEn)]
    [InlineData(LanguagePair.JaEs)]
    [InlineData(LanguagePair.EnEs)]
    public void EncodeDecodeRoundTripsEveryLanguagePair(LanguagePair pair)
    {
        var settings = AppSettingsData.Default with { LanguagePair = pair };
        var restored = AppSettingsCodec.Decode(AppSettingsCodec.Encode(settings));
        Assert.Equal(pair, restored.LanguagePair);
    }

    // Given: 対応する全表示言語
    // When: JSON へ書き出して読み戻す
    // Then: system / ja / en が欠落なく復元される
    [Theory]
    [InlineData(UiLanguagePreference.System)]
    [InlineData(UiLanguagePreference.Ja)]
    [InlineData(UiLanguagePreference.En)]
    public void EncodeDecodeRoundTripsEveryUiLanguage(UiLanguagePreference uiLanguage)
    {
        var settings = AppSettingsData.Default with { UiLanguage = uiLanguage };
        var restored = AppSettingsCodec.Decode(AppSettingsCodec.Encode(settings));
        Assert.Equal(uiLanguage, restored.UiLanguage);
    }

    // Given: API キーを含めてはいけない設定 JSON
    // When: 書き出す
    // Then: キーらしき項目が現れない
    [Fact]
    public void EncodeNeverEmitsApiKeyFields()
    {
        var json = AppSettingsCodec.Encode(AppSettingsData.Default);

        Assert.DoesNotContain("apiKey", json, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sk-", json, System.StringComparison.Ordinal);
    }

    // Given: 壊れた JSON や未知の値
    // When: 読み込む
    // Then: 既定値へ倒れて起動を妨げない
    [Theory]
    [InlineData("{ not json")]
    [InlineData("[]")]
    [InlineData("{\"noiseReduction\":\"unknown\",\"transcriptionDelay\":\"unknown\"}")]
    public void DecodeFallsBackToDefaults(string json)
    {
        var settings = AppSettingsCodec.Decode(json);

        Assert.Equal(AppSettingsData.Default.NoiseReduction, settings.NoiseReduction);
        Assert.Equal(AppSettingsData.Default.TranscriptionDelay, settings.TranscriptionDelay);
        Assert.Equal(AppSettingsData.Default.FontSize, settings.FontSize);
    }

    // Given: recordSubtitles を含まない古い settings.json
    // When: 読み込む
    // Then: false になる
    [Fact]
    public void DecodeMissingRecordSubtitlesDefaultsToFalse()
    {
        var settings = AppSettingsCodec.Decode("{\"fontSize\":32}");
        Assert.False(settings.RecordSubtitles);
    }

    // Given: 言語ペアを含まない旧 settings.json
    // When: 読み込む
    // Then: 既定の ja-en を使う
    [Fact]
    public void DecodeMissingLanguagePairDefaultsToJaEn()
    {
        Assert.Equal(LanguagePair.JaEn, AppSettingsCodec.Decode("{\"fontSize\":32}").LanguagePair);
    }

    // Given: 表示言語を含まない旧 settings.json
    // When: 読み込む
    // Then: system を使う
    [Fact]
    public void DecodeMissingUiLanguageDefaultsToSystem()
    {
        Assert.Equal(
            UiLanguagePreference.System,
            AppSettingsCodec.Decode("{\"fontSize\":32}").UiLanguage);
    }

    // Given: 未知の表示言語を含む settings.json
    // When: 読み込む
    // Then: system へ倒して起動を妨げない
    [Fact]
    public void DecodeUnknownUiLanguageDefaultsToSystem()
    {
        Assert.Equal(
            UiLanguagePreference.System,
            AppSettingsCodec.Decode("{\"uiLanguage\":\"es\"}").UiLanguage);
    }

    // Given: 未知の言語ペアを含む settings.json
    // When: 読み込む
    // Then: 既定の ja-en へフォールバックする
    [Fact]
    public void DecodeUnknownLanguagePairDefaultsToJaEn()
    {
        Assert.Equal(
            LanguagePair.JaEn,
            AppSettingsCodec.Decode("{\"languagePair\":\"xx-yy\"}").LanguagePair);
    }

    // Given: 範囲外のフォントサイズ
    // When: 読み込む
    // Then: 18..48 にクランプされる
    [Fact]
    public void DecodeClampsFontSize()
    {
        Assert.Equal(AppSettingsData.MaximumFontSize, AppSettingsCodec.Decode("{\"fontSize\":900}").FontSize);
        Assert.Equal(AppSettingsData.MinimumFontSize, AppSettingsCodec.Decode("{\"fontSize\":1}").FontSize);
    }

    // Given: NaN / Infinity を含む設定レコード
    // When: JSON へ書き出す
    // Then: Utf8JsonWriter 例外にならず有限値へ正規化される
    [Fact]
    public void EncodeNormalizesNonFiniteNumbers()
    {
        var settings = AppSettingsData.Default with
        {
            FontSize = double.NaN,
            OverlayOriginX = double.PositiveInfinity,
            OverlayOriginY = double.NegativeInfinity,
        };

        var restored = AppSettingsCodec.Decode(AppSettingsCodec.Encode(settings));

        Assert.Equal(AppSettingsData.DefaultFontSize, restored.FontSize);
        Assert.Equal(0, restored.OverlayOriginX);
        Assert.Equal(0, restored.OverlayOriginY);
    }

    // Given: 同意バージョンが古い設定
    // When: 現在の同意状態を見る
    // Then: 未同意として扱う
    [Fact]
    public void OlderConsentVersionCountsAsNotAccepted() =>
        Assert.False((AppSettingsData.Default with { AcceptedConsentVersion = 0 }).HasAcceptedCurrentConsent);

    // Given: 保存済みのプロンプトとキーワード
    // When: セッション tuning を作る
    // Then: 送信用にサニタイズされた値になる
    [Fact]
    public void TuningUsesSanitizedHints()
    {
        var settings = AppSettingsData.Default with
        {
            TranscriptionPrompt = "prompt <tag>",
            TranscriptionKeywordsText = "Devin\n<script>\n",
        };

        var tuning = settings.Tuning();

        Assert.Equal(RealtimeSessionTuning.SanitizedPrompt("prompt <tag>"), tuning.TranscriptionPrompt);
        Assert.Equal(
            RealtimeSessionTuning.ParseKeywords("Devin\n<script>\n").ToList(),
            tuning.TranscriptionKeywords.ToList());
    }
}
