using RealtimeTranslator.Core.Audio;
using RealtimeTranslator.Core.OpenAI;
using Xunit;

namespace RealtimeTranslator.Core.Tests;

public sealed class LanguageFixtureTests
{
    public static TheoryData<string> EvidenceCases => SharedFixtures.CaseNames("language", "evidence");

    public static TheoryData<string> RecentEvidenceCases => SharedFixtures.CaseNames("language", "recentEvidence");

    [Fact]
    public void WindowSizeMatchesFixture()
    {
        Assert.Equal(
            SharedFixtures.Number(SharedFixtures.Load("language")["recentEvidenceWindow"]),
            SpokenLanguageDetector.RecentEvidenceWindow);
    }

    [Theory]
    [MemberData(nameof(EvidenceCases))]
    public void EvidenceAndDetectMatchFixture(string name)
    {
        var fixture = SharedFixtures.Case("language", "evidence", name);
        var input = SharedFixtures.Text(fixture["input"]);

        Assert.Equal(ParseEvidence(SharedFixtures.Text(fixture["evidence"])), SpokenLanguageDetector.Evidence(input));
        Assert.Equal(ParseLanguage(SharedFixtures.Text(fixture["detect"])), SpokenLanguageDetector.Detect(input));
    }

    [Theory]
    [MemberData(nameof(RecentEvidenceCases))]
    public void RecentEvidenceMatchesFixture(string name)
    {
        var fixture = SharedFixtures.Case("language", "recentEvidence", name);
        var input = SharedFixtures.Text(fixture["input"]);

        Assert.Equal(
            ParseEvidence(SharedFixtures.Text(fixture["expected"])),
            SpokenLanguageDetector.RecentEvidence(input, SharedFixtures.Number(fixture["window"])));
        Assert.Equal(
            ParseEvidence(SharedFixtures.Text(fixture["fullEvidence"])),
            SpokenLanguageDetector.Evidence(input));
    }

    [Fact]
    public void TranslationTargetsMatchFixture()
    {
        foreach (var item in SharedFixtures.Section("language", "targets"))
        {
            var fixture = item!.AsObject();
            var language = ParseLanguage(SharedFixtures.Text(fixture["language"]));
            var expected = SharedFixtures.OptionalText(fixture["translationTarget"]);

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
