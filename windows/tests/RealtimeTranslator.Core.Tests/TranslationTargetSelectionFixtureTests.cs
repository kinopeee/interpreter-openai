using RealtimeTranslator.Core.Audio;
using RealtimeTranslator.Core.OpenAI;
using RealtimeTranslator.Core.Realtime;
using Xunit;

namespace RealtimeTranslator.Core.Tests;

public sealed class TranslationTargetSelectionFixtureTests
{
    public static TheoryData<string> Cases => SharedFixtures.CaseNames("routing", "targetSelection");

    // Given: target は出力言語である targetSelection fixture
    // When: evidence を純粋な target 調停器へ順に渡す
    // Then: 各段階の出力 target と一致する
    [Theory]
    [MemberData(nameof(Cases))]
    public void SelectionMatchesFixture(string name)
    {
        // Given: pair と任意の初期 output target
        var fixture = SharedFixtures.Case("routing", "targetSelection", name);
        var pair = LanguagePairExtensions.ParseLanguagePair(SharedFixtures.Text(fixture["pair"]));
        var current = fixture["initialTarget"] is { } initial
            ? RealtimeTranslationWireValues.ParseOutputLanguage(SharedFixtures.Text(initial))
            : (RealtimeTranslationOutputLanguage?)null;
        var reverseCount = 0;

        foreach (var step in fixture["evidence"]!.AsArray())
        {
            var evidence = ParseEvidence(SharedFixtures.Text(step!["evidence"]));
            var selection = TranslationTargetSelector.Select(pair, current, reverseCount, evidence);
            current = selection.Target;
            reverseCount = selection.ReverseEvidenceCount;
            var expected = step["expectedTarget"] is { } target
                ? RealtimeTranslationWireValues.ParseOutputLanguage(SharedFixtures.Text(target))
                : (RealtimeTranslationOutputLanguage?)null;
            Assert.Equal(expected, current);
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
}
