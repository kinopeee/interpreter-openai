using System;
using System.Linq;
using RealtimeTranslator.Core.Audio;
using RealtimeTranslator.Core.OpenAI;
using Xunit;

namespace RealtimeTranslator.Core.Tests;

public sealed class LanguageFixtureTests
{
    public static TheoryData<string> EvidenceCases => SharedFixtures.CaseNames("language", "evidence");

    public static TheoryData<string> RecentEvidenceCases => SharedFixtures.CaseNames("language", "recentEvidence");

    // Given: shared fixture の直近証拠ウィンドウ定数
    // When: 検出器の定数と照合する
    // Then: 非空白 scalar 数の上限が一致する
    [Fact]
    public void WindowSizeMatchesFixture()
    {
        // Given: recentEvidenceWindow fixture
        // When/Then: detector の窓サイズが一致する
        Assert.Equal(
            SharedFixtures.Number(SharedFixtures.Load("language")["recentEvidenceWindow"]),
            SpokenLanguageDetector.RecentEvidenceWindow);
    }

    // Given: en-es 判定の契約定数
    // When: detector の実装定数と照合する
    // Then: 語窓と排他語リストが一致する
    [Fact]
    public void EnEsConstantsMatchFixture()
    {
        var fixture = SharedFixtures.Load("language");
        Assert.Equal(SharedFixtures.Number(fixture["enEsWindow"]), SpokenLanguageDetector.EnEsWindow);
        Assert.Equal(
            fixture["exclusiveWords"]!["es"]!.AsArray().Select(SharedFixtures.Text),
            SpokenLanguageDetector.SpanishExclusiveWords);
        Assert.Equal(
            fixture["exclusiveWords"]!["en"]!.AsArray().Select(SharedFixtures.Text),
            SpokenLanguageDetector.EnglishExclusiveWords);
    }

    // Given: 長い英語列の後ろにスペイン語の逆疑問文がある
    // When: en-es の8語窓で recent evidence を求める
    // Then: 窓の句読点を保持して spanish を即時確定する
    [Fact]
    public void EnEsRecentEvidencePreservesInvertedPunctuation()
    {
        Assert.Equal(
            SpokenLanguageEvidence.Spanish,
            SpokenLanguageDetector.RecentEvidence(
                "the and is are this with for ¿Dónde estás?",
                LanguagePair.EnEs,
                SpokenLanguageDetector.EnEsWindow));
    }

    // Given: 8語窓の先頭語の直前に空白付きの逆疑問符がある
    // When: en-es の recent evidence を求める
    // Then: 空白を挟んでも ¿ を窓に残し spanish を即時確定する
    [Fact]
    public void EnEsRecentEvidencePreservesInvertedPunctuationSeparatedBySpace()
    {
        Assert.Equal(
            SpokenLanguageEvidence.Spanish,
            SpokenLanguageDetector.RecentEvidence(
                "aaa bbb ccc ¿ Hello there friend people world today extra more",
                LanguagePair.EnEs,
                SpokenLanguageDetector.EnEsWindow));
    }

    // Given: 8語窓より前に ¿ があり、その間にラテン語がある
    // When: en-es の recent evidence を求める
    // Then: 直前のラテン語で walk-back を止め、遠い ¿ だけでは spanish にしない
    [Fact]
    public void EnEsRecentEvidenceDoesNotWalkBackPastLatinToDistantInvertedPunct()
    {
        const string text = "¿ Dónde estás hello there friend people world today extra more";

        Assert.Equal(
            SpokenLanguageEvidence.AmbiguousLatin,
            SpokenLanguageDetector.RecentEvidence(
                text,
                LanguagePair.EnEs,
                SpokenLanguageDetector.EnEsWindow));
        Assert.Equal(SpokenLanguageEvidence.Spanish, SpokenLanguageDetector.Evidence(text, LanguagePair.EnEs));
        Assert.True(
            SpokenLanguageDetector.RecentWordWindowStart(text) > text.IndexOf('¿'));
    }

    // Given: 8語窓の先頭語の直前に TAB / 改行付きの逆疑問符がある
    // When: RecentWordWindowStart と RecentEvidence を求める
    // Then: 制御空白を跨いで ¿ が窓先頭に残り spanish になる
    [Theory]
    [InlineData("\t")]
    [InlineData("\n")]
    [InlineData("\r")]
    public void EnEsRecentWordWindowStartIncludesInvertedPunctuationAcrossControlWhitespace(
        string separator)
    {
        var text = $"aaa bbb ccc ¿{separator}Hello there friend people world today extra more";

        var start = SpokenLanguageDetector.RecentWordWindowStart(text);
        var window = text[start..];

        Assert.StartsWith("¿", window, StringComparison.Ordinal);
        Assert.Equal(
            SpokenLanguageEvidence.Spanish,
            SpokenLanguageDetector.RecentEvidence(
                text,
                LanguagePair.EnEs,
                SpokenLanguageDetector.EnEsWindow));
        Assert.Equal(
            SpokenLanguageEvidence.Spanish,
            SpokenLanguageDetector.Evidence(window, LanguagePair.EnEs));
    }

    // Given: 8語窓の先頭語の直前に ¿ と ¡ が空白区切りである
    // When: RecentWordWindowStart を求める
    // Then: 両方の逆句読点が窓に残り spanish になる
    [Fact]
    public void EnEsRecentWordWindowStartIncludesBothInvertedMarksSeparatedBySpace()
    {
        const string text = "aaa bbb ccc ¿ ¡ Hello there friend people world today extra more";

        var window = text[SpokenLanguageDetector.RecentWordWindowStart(text)..];

        Assert.StartsWith("¿", window, StringComparison.Ordinal);
        Assert.Contains("¡", window, StringComparison.Ordinal);
        Assert.Equal(
            SpokenLanguageEvidence.Spanish,
            SpokenLanguageDetector.RecentEvidence(
                text,
                LanguagePair.EnEs,
                SpokenLanguageDetector.EnEsWindow));
    }

    // Given: 非 BMP 文字を含む単語境界
    // When: en-es の recent evidence を評価する
    // Then: UTF-16 の下位サロゲートを Rune として誤読せず判定できる
    [Fact]
    public void RecentEvidenceHandlesNonBmpTextBeforeWord()
    {
        var evidence = SpokenLanguageDetector.RecentEvidence(
            "😀hola",
            LanguagePair.EnEs,
            window: 1);

        Assert.Equal(SpokenLanguageEvidence.AmbiguousLatin, evidence);
    }

    // Given: title-case の英語 exclusive words（tr-TR の ToLower だと I→ı で "is" が照合不能）
    // When: en-es 証拠を求める
    // Then: ToLowerInvariant（I→i）で exclusive word が照合でき english になる
    [Fact]
    public void EnEsExclusiveWordMatchIsCultureInvariant()
    {
        Assert.Equal("i", "I".ToLowerInvariant());
        Assert.Equal("is", "Is".ToLowerInvariant());
        Assert.Equal("this", "This".ToLowerInvariant());

        Assert.Equal(
            SpokenLanguageEvidence.English,
            SpokenLanguageDetector.Evidence("Is This With It", LanguagePair.EnEs));
    }

    // Given: ラテン語が 0 語の en-es 原文（句読点 / 空白 / 空文字）
    // When: Evidence を求める
    // Then: 現行実装は AmbiguousLatin（protocol の None とは差がある。selector 初期値はどちらも candidate null）
    [Theory]
    [InlineData("!!!")]
    [InlineData("…")]
    [InlineData("   ")]
    [InlineData("")]
    public void EnEsZeroLatinWordsIsAmbiguousLatin(string text)
    {
        Assert.Equal(
            SpokenLanguageEvidence.AmbiguousLatin,
            SpokenLanguageDetector.Evidence(text, LanguagePair.EnEs));
        Assert.Equal(
            SpokenLanguageEvidence.None,
            SpokenLanguageDetector.Evidence(text, LanguagePair.JaEn));
    }

    // Given: アクセント付きスペイン語の複数語
    // When: ja-es で証拠を求める
    // Then: ASCII のみの語分割に落ちず spanish になる
    [Fact]
    public void JaEsEvidenceTreatsAccentedLatinAsSpanishWords()
    {
        Assert.Equal(
            SpokenLanguageEvidence.Spanish,
            SpokenLanguageDetector.Evidence("está aquí", LanguagePair.JaEs));
    }

    // Given: fixture の日英混在・曖昧・不明テキスト
    // When: 言語証拠を集計し言語を判定する
    // Then: 期待する証拠と検出結果になる
    [Theory]
    [MemberData(nameof(EvidenceCases))]
    public void EvidenceAndDetectMatchFixture(string name)
    {
        // Given: 文字種判定 fixture
        var fixture = SharedFixtures.Case("language", "evidence", name);
        var input = SharedFixtures.Text(fixture["input"]);
        var pair = fixture["pair"] is { } pairNode
            ? LanguagePairExtensions.ParseLanguagePair(SharedFixtures.Text(pairNode))
            : LanguagePair.JaEn;

        // When/Then: evidence と detect が一致する
        Assert.Equal(ParseEvidence(SharedFixtures.Text(fixture["evidence"])), SpokenLanguageDetector.Evidence(input, pair));
        Assert.Equal(ParseLanguage(SharedFixtures.Text(fixture["detect"])), SpokenLanguageDetector.Detect(input, pair));
    }

    // Given: ウィンドウを超える長さのテキスト
    // When: 末尾から Unicode scalar 単位で直近証拠を切り出す
    // Then: fixture の期待証拠と全体証拠の両方に一致する
    [Theory]
    [MemberData(nameof(RecentEvidenceCases))]
    public void RecentEvidenceMatchesFixture(string name)
    {
        // Given: 末尾ウィンドウ判定 fixture
        var fixture = SharedFixtures.Case("language", "recentEvidence", name);
        var input = SharedFixtures.Text(fixture["input"]);
        var pair = fixture["pair"] is { } pairNode
            ? LanguagePairExtensions.ParseLanguagePair(SharedFixtures.Text(pairNode))
            : LanguagePair.JaEn;

        // When/Then: 末尾窓と全文 evidence が一致する
        Assert.Equal(
            ParseEvidence(SharedFixtures.Text(fixture["expected"])),
            SpokenLanguageDetector.RecentEvidence(
                input,
                pair,
                SharedFixtures.Number(fixture["window"])));
        Assert.Equal(
            ParseEvidence(SharedFixtures.Text(fixture["fullEvidence"])),
            SpokenLanguageDetector.Evidence(input, pair));
    }

    // Given: fixture の言語→翻訳先対応表
    // When: 各言語の翻訳先と counterpart を求める
    // Then: target / counterpart が双方向に一致する
    [Fact]
    public void TranslationTargetsAndCounterpartsMatchFixture()
    {
        // Given: language → translationTarget / counterpart 対応表
        foreach (var item in SharedFixtures.Section("language", "targets"))
        {
            var fixture = item!.AsObject();
            var pair = LanguagePairExtensions.ParseLanguagePair(SharedFixtures.Text(fixture["pair"]));
            var language = ParseLanguage(SharedFixtures.Text(fixture["language"]));
            var expectedTarget = SharedFixtures.OptionalText(fixture["translationTarget"]);
            var expectedCounterpart = ParseOptionalLanguage(SharedFixtures.Text(fixture["counterpart"]));

            // When/Then: TranslationTarget() と Counterpart(language) が一致する
            Assert.Equal(
                expectedTarget is null ? null : RealtimeTranslationWireValues.ParseOutputLanguage(expectedTarget),
                pair.TranslationTarget(language));
            Assert.Equal(expectedCounterpart, pair.Counterpart(language));

            // When/Then: Counterpart(target) は「その target を選ぶ話者言語」を返す
            if (expectedTarget is { } wire)
            {
                var target = RealtimeTranslationWireValues.ParseOutputLanguage(wire);
                Assert.Equal(language, pair.Counterpart(target));
            }
        }
    }

    private static SpokenLanguageEvidence ParseEvidence(string value) => value switch
    {
        "japanese" => SpokenLanguageEvidence.Japanese,
        "english" => SpokenLanguageEvidence.English,
        "spanish" => SpokenLanguageEvidence.Spanish,
        "ambiguousLatin" => SpokenLanguageEvidence.AmbiguousLatin,
        "none" => SpokenLanguageEvidence.None,
        _ => throw new Xunit.Sdk.XunitException("unhandled evidence " + value),
    };

    private static SpokenLanguage ParseLanguage(string value) => value switch
    {
        "japanese" => SpokenLanguage.Japanese,
        "english" => SpokenLanguage.English,
        "spanish" => SpokenLanguage.Spanish,
        "unknown" => SpokenLanguage.Unknown,
        _ => throw new Xunit.Sdk.XunitException("unhandled language " + value),
    };

    private static SpokenLanguage? ParseOptionalLanguage(string value) => value switch
    {
        "unknown" => null,
        _ => ParseLanguage(value),
    };
}
