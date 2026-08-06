using System;
using System.Globalization;
using System.Linq;
using RealtimeTranslator.Core.OpenAI;
using Xunit;

namespace RealtimeTranslator.Core.Tests;

public sealed class TuningFixtureTests
{
    public static TheoryData<string> ParseKeywordsCases => SharedFixtures.CaseNames("tuning", "parseKeywords");

    public static TheoryData<string> SanitizedPromptCases => SharedFixtures.CaseNames("tuning", "sanitizedPrompt");

    [Fact]
    public void LimitsMatchFixture()
    {
        var limits = SharedFixtures.Load("tuning")["limits"]!.AsObject();

        Assert.Equal(SharedFixtures.Number(limits["keywordLimit"]), RealtimeSessionTuning.KeywordLimit);
        Assert.Equal(
            SharedFixtures.Number(limits["promptCharacterLimit"]),
            RealtimeSessionTuning.PromptCharacterLimit);
        Assert.Equal(
            SharedFixtures.Text(limits["forbiddenKeywordCharacters"]),
            RealtimeSessionTuning.ForbiddenKeywordCharacters);
    }

    [Theory]
    [MemberData(nameof(ParseKeywordsCases))]
    public void ParseKeywordsMatchesFixture(string name)
    {
        var fixture = SharedFixtures.Case("tuning", "parseKeywords", name);
        var expected = fixture["expected"]!.AsArray().Select(SharedFixtures.Text).ToArray();

        Assert.Equal(expected, RealtimeSessionTuning.ParseKeywords(SharedFixtures.Text(fixture["input"])));
    }

    [Fact]
    public void ParseKeywordsStopsAtTheLimit()
    {
        var fixture = SharedFixtures.Load("tuning")["parseKeywordsLimit"]!.AsObject();
        var template = SharedFixtures.Text(fixture["lineTemplate"]);
        var lineCount = SharedFixtures.Number(fixture["lineCount"]);

        var input = string.Join(
            "\n",
            Enumerable.Range(0, lineCount).Select(index => template.Replace(
                "{index}",
                index.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal)));

        var keywords = RealtimeSessionTuning.ParseKeywords(input, SharedFixtures.Number(fixture["limit"]));

        Assert.Equal(SharedFixtures.Number(fixture["expectedCount"]), keywords.Length);
        Assert.Equal(SharedFixtures.Text(fixture["expectedFirst"]), keywords[0]);
        Assert.Equal(SharedFixtures.Text(fixture["expectedLast"]), keywords[^1]);
    }

    [Theory]
    [MemberData(nameof(SanitizedPromptCases))]
    public void SanitizedPromptMatchesFixture(string name)
    {
        var fixture = SharedFixtures.Case("tuning", "sanitizedPrompt", name);

        Assert.Equal(
            SharedFixtures.Text(fixture["expected"]),
            RealtimeSessionTuning.SanitizedPrompt(SharedFixtures.Text(fixture["input"])));
    }

    [Fact]
    public void SanitizedPromptTruncatesAtTheLimit()
    {
        var fixture = SharedFixtures.Load("tuning")["sanitizedPromptLimit"]!.AsObject();
        var input = new string(
            SharedFixtures.Text(fixture["repeatedCharacter"])[0],
            SharedFixtures.Number(fixture["inputLength"]));

        Assert.Equal(
            SharedFixtures.Number(fixture["expectedLength"]),
            RealtimeSessionTuning.SanitizedPrompt(input).Length);
    }
}
