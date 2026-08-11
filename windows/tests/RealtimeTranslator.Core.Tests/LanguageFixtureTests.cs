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
    // When: 各言語の翻訳先を求める
    // Then: 日本語は英語へ、英語は日本語へ向かう
    [Fact]
    public void TranslationTargetsMatchFixture()
    {
        // Given: language → translationTarget 対応表
        foreach (var item in SharedFixtures.Section("language", "targets"))
        {
            var fixture = item!.AsObject();
            var pair = LanguagePairExtensions.ParseLanguagePair(SharedFixtures.Text(fixture["pair"]));
            var language = ParseLanguage(SharedFixtures.Text(fixture["language"]));
            var expected = SharedFixtures.OptionalText(fixture["translationTarget"]);

            // When/Then: TranslationTarget() が一致する
            Assert.Equal(
                expected is null ? null : RealtimeTranslationWireValues.ParseOutputLanguage(expected),
                pair.TranslationTarget(language));
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
}
