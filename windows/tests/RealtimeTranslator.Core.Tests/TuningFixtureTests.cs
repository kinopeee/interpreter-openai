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

    // Given: shared fixture の tuning 上限値
    // When: C# 実装の定数と照合する
    // Then: keyword 上限・prompt 上限・禁止文字が一致する
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

    // Given: 1 行 1 語のキーワードテキスト
    // When: ParseKeywords で正規化する
    // Then: fixture の期待配列と一致する
    [Theory]
    [MemberData(nameof(ParseKeywordsCases))]
    public void ParseKeywordsMatchesFixture(string name)
    {
        var fixture = SharedFixtures.Case("tuning", "parseKeywords", name);
        var expected = fixture["expected"]!.AsArray().Select(SharedFixtures.Text).ToArray();

        Assert.Equal(expected, RealtimeSessionTuning.ParseKeywords(SharedFixtures.Text(fixture["input"])));
    }

    // Given: 上限を超える行数のキーワードテキスト
    // When: ParseKeywords で正規化する
    // Then: 上限件数で打ち切られ、先頭と末尾が入力順を保つ
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

    // Given: 非空キーワードと limit=0
    // When: ParseKeywords で正規化する
    // Then: 1 件も返さない
    [Fact]
    public void ParseKeywordsReturnsEmptyWhenLimitIsZero()
    {
        var keywords = RealtimeSessionTuning.ParseKeywords("hackathon\ndemo", limit: 0);

        Assert.Empty(keywords);
    }

    // Given: 改行や前後空白を含む prompt
    // When: SanitizedPrompt で正規化する
    // Then: fixture の期待文字列と一致する
    [Theory]
    [MemberData(nameof(SanitizedPromptCases))]
    public void SanitizedPromptMatchesFixture(string name)
    {
        var fixture = SharedFixtures.Case("tuning", "sanitizedPrompt", name);

        Assert.Equal(
            SharedFixtures.Text(fixture["expected"]),
            RealtimeSessionTuning.SanitizedPrompt(SharedFixtures.Text(fixture["input"])));
    }

    // Given: 上限を超える長さの ASCII prompt
    // When: SanitizedPrompt で正規化する
    // Then: fixture の期待長へ切り詰められる
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

    // Given: サロゲートペアで表される絵文字だけで上限を超える prompt
    // When: SanitizedPrompt で正規化する
    // Then: Swift の Character 数と同じ上限文字数で切り、lone surrogate を残さない
    [Fact]
    public void SanitizedPromptTruncatesByTextElementNotCodeUnit()
    {
        const string emoji = "\U0001F600";
        var limit = RealtimeSessionTuning.PromptCharacterLimit;

        var truncated = RealtimeSessionTuning.SanitizedPrompt(string.Concat(Enumerable.Repeat(emoji, limit + 100)));

        Assert.Equal(string.Concat(Enumerable.Repeat(emoji, limit)), truncated);
    }

    // Given: 結合文字を含む書記素クラスタで上限を超える prompt
    // When: SanitizedPrompt で正規化する
    // Then: 結合文字ごと切り、基底文字だけを残さない
    [Fact]
    public void SanitizedPromptTruncatesCombiningGraphemeClusters()
    {
        const string combining = "e\u0301";
        var limit = RealtimeSessionTuning.PromptCharacterLimit;
        var input = new string('a', limit - 1) + combining + combining;

        var truncated = RealtimeSessionTuning.SanitizedPrompt(input);

        Assert.Equal(new string('a', limit - 1) + combining, truncated);
    }
}
