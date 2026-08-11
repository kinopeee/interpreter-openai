using System;
using System.Text;
using System.Text.Json.Nodes;
using RealtimeTranslator.Core.OpenAI;
using RealtimeTranslator.Core.Realtime;
using RealtimeTranslator.Core.Subtitles;
using Xunit;

namespace RealtimeTranslator.Core.Tests;

public sealed class SubtitleFixtureTests
{
    private static readonly DateTimeOffset Origin = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static TheoryData<string> ClipCases => SharedFixtures.CaseNames("subtitle", "clip");

    public static TheoryData<string> AssemblerCases => AssemblerCaseNames();

    // Given: shared fixture の字幕文字数上限
    // When: clipper の定数と照合する
    // Then: 日本語 60 / 英語 120 / 省略記号が一致する
    [Fact]
    public void LimitsMatchFixture()
    {
        // Given: subtitle clip limits fixture
        var limits = SharedFixtures.Load("subtitle")["limits"]!.AsObject();

        // When/Then: clipper 定数が一致する
        Assert.Equal(SharedFixtures.Number(limits["japaneseCharacterLimit"]), SubtitleTailClipper.JapaneseCharacterLimit);
        Assert.Equal(SharedFixtures.Number(limits["englishCharacterLimit"]), SubtitleTailClipper.EnglishCharacterLimit);
        Assert.Equal(SharedFixtures.Text(limits["ellipsis"]), SubtitleTailClipper.Ellipsis);
    }

    // Given: fixture の長文・短文・空白のみの字幕候補
    // When: 末尾優先でクリップする
    // Then: 期待する表示文字列になる
    [Theory]
    [MemberData(nameof(ClipCases))]
    public void ClipMatchesFixture(string name)
    {
        // Given: clip fixture
        var fixture = SharedFixtures.Case("subtitle", "clip", name);

        // When/Then: 末尾クリップ結果が一致する
        Assert.Equal(
            Expand(fixture["expected"]!),
            SubtitleTailClipper.Clip(Expand(fixture["input"]!)));
    }

    // Given: shared fixture の無採取 finalize 間隔
    // When: assembler の定数と照合する
    // Then: 8 秒の idle finalize 間隔が一致する
    [Fact]
    public void IdleIntervalMatchesFixture()
    {
        // Given: assembler idle 設定
        var assembler = SharedFixtures.Load("subtitle")["assembler"]!.AsObject();

        // When/Then: IdleFinalizeInterval が一致する
        Assert.Equal(
            TimeSpan.FromSeconds(SharedFixtures.Number(assembler["idleFinalizeSeconds"])),
            RealtimeSubtitleAssembler.IdleFinalizeInterval);
    }

    // Given: fixture の原文・翻訳 delta シナリオ（epoch / 重複 ID / lane 期待値を含む）
    // When: assembler へ順に投入し時間を進める
    // Then: finalize タイミングと字幕内容が期待どおりになる
    [Theory]
    [MemberData(nameof(AssemblerCases))]
    public void AssemblerMatchesFixture(string name)
    {
        // Given: assembler シナリオと初期 epoch/lane
        var fixture = FindAssemblerCase(name);
        var epoch = SharedFixtures.Number(fixture["epoch"]);

        var assembler = new RealtimeSubtitleAssembler();
        assembler.Reset(epoch);
        assembler.ExpectLane(
            SharedFixtures.OptionalText(fixture["expectLane"]) is { } lane
                ? RealtimeTranslationWireValues.ParseOutputLanguage(lane)
                : null);

        // When: tick / delta を順に適用する
        RealtimeSubtitleUpdate? last = null;
        foreach (var item in fixture["steps"]!.AsArray())
        {
            var step = item!.AsObject();
            var now = Origin.AddSeconds(SharedFixtures.Real(step["at"]));
            var kind = SharedFixtures.Text(step["kind"]);

            var update = kind switch
            {
                "tick" => assembler.Tick(now),
                "finalizeForLanguageSwitch" => assembler.FinalizeForLanguageSwitch(now),
                "sourceDelta" or "translationDelta" => assembler.Ingest(
                    new RealtimeTranslationStreamEvent(
                        ParseLane(SharedFixtures.Text(step["lane"])),
                        ServerEvent(kind, step),
                        SharedFixtures.OptionalNumber(step["epoch"]) ?? epoch),
                    now),
                _ => throw new Xunit.Sdk.XunitException("unhandled step kind " + kind),
            };

            last = update ?? last;
        }

        // Then: 最終字幕更新が期待どおり
        var expected = fixture["expectedFinal"]!.AsObject();
        Assert.NotNull(last);
        Assert.Equal(SharedFixtures.Text(expected["sourceText"]), last.Value.SourceText);
        Assert.Equal(SharedFixtures.Text(expected["translatedText"]), last.Value.TranslatedText);
        Assert.Equal(SharedFixtures.Flag(expected["isTranslationCurrent"]), last.Value.IsTranslationCurrent);
        Assert.Equal(SharedFixtures.Flag(expected["shouldFinalize"]), last.Value.ShouldFinalize);
    }

    private static RealtimeTranslationServerEvent ServerEvent(string kind, JsonObject step)
    {
        var text = SharedFixtures.Text(step["text"]);
        var eventId = SharedFixtures.OptionalText(step["eventId"]);
        var elapsedMs = SharedFixtures.OptionalNumber(step["elapsedMs"]);

        return kind == "sourceDelta"
            ? new RealtimeTranslationServerEvent.InputTranscriptDelta(text, eventId, elapsedMs)
            : new RealtimeTranslationServerEvent.OutputTranscriptDelta(text, eventId, elapsedMs);
    }

    private static RealtimeTranslationLane ParseLane(string value) =>
        value == "source"
            ? RealtimeTranslationLane.Source
            : RealtimeTranslationLane.Translation(RealtimeTranslationWireValues.ParseOutputLanguage(value));

    /// <summary>literal / repeat / concat 記法を展開する。</summary>
    private static string Expand(JsonNode node)
    {
        var value = node.AsObject();
        if (value["literal"] is { } literal)
        {
            return SharedFixtures.Text(literal);
        }

        if (value["repeat"] is { } repeat)
        {
            var unit = SharedFixtures.Text(repeat);
            var count = SharedFixtures.Number(value["count"]);
            var builder = new StringBuilder(unit.Length * count);
            for (var index = 0; index < count; index += 1)
            {
                builder.Append(unit);
            }

            return builder.ToString();
        }

        if (value["concat"] is { } concat)
        {
            var builder = new StringBuilder();
            foreach (var part in concat.AsArray())
            {
                builder.Append(Expand(part!));
            }

            return builder.ToString();
        }

        throw new Xunit.Sdk.XunitException("unhandled text node");
    }

    private static TheoryData<string> AssemblerCaseNames()
    {
        var data = new TheoryData<string>();
        foreach (var item in SharedFixtures.Load("subtitle")["assembler"]!["cases"]!.AsArray())
        {
            data.Add(SharedFixtures.Text(item?["name"]));
        }

        return data;
    }

    private static JsonObject FindAssemblerCase(string name)
    {
        foreach (var item in SharedFixtures.Load("subtitle")["assembler"]!["cases"]!.AsArray())
        {
            if (item is JsonObject candidate && SharedFixtures.Text(candidate["name"]) == name)
            {
                return candidate;
            }
        }

        throw new Xunit.Sdk.XunitException("no assembler case named " + name);
    }
}
