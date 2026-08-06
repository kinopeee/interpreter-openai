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

        // When/Then: evidence と detect が一致する
        Assert.Equal(ParseEvidence(SharedFixtures.Text(fixture["evidence"])), SpokenLanguageDetector.Evidence(input));
        Assert.Equal(ParseLanguage(SharedFixtures.Text(fixture["detect"])), SpokenLanguageDetector.Detect(input));
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

        // When/Then: 末尾窓と全文 evidence が一致する
        Assert.Equal(
            ParseEvidence(SharedFixtures.Text(fixture["expected"])),
            SpokenLanguageDetector.RecentEvidence(input, SharedFixtures.Number(fixture["window"])));
        Assert.Equal(
            ParseEvidence(SharedFixtures.Text(fixture["fullEvidence"])),
            SpokenLanguageDetector.Evidence(input));
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
            var language = ParseLanguage(SharedFixtures.Text(fixture["language"]));
            var expected = SharedFixtures.OptionalText(fixture["translationTarget"]);

            // When/Then: TranslationTarget() が一致する
            Assert.Equal(
                expected is null ? null : RealtimeTranslationWireValues.ParseOutputLanguage(expected),
                language.TranslationTarget());
        }
    }

    private static SpokenLanguageEvidence ParseEvidence(string value) => value switch
    {
        "japanese" => SpokenLanguageEvidence.Japanese,
        "english" => SpokenLanguageEvidence.English,
        "ambiguousLatin" => SpokenLanguageEvidence.AmbiguousLatin,
        "none" => SpokenLanguageEvidence.None,
        _ => throw new Xunit.Sdk.XunitException("unhandled evidence " + value),
    };

    private static SpokenLanguage ParseLanguage(string value) => value switch
    {
        "japanese" => SpokenLanguage.Japanese,
        "english" => SpokenLanguage.English,
        "unknown" => SpokenLanguage.Unknown,
        _ => throw new Xunit.Sdk.XunitException("unhandled language " + value),
    };
}
