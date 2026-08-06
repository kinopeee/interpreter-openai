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

    [Fact]
    public void LimitsMatchFixture()
    {
        var limits = SharedFixtures.Load("subtitle")["limits"]!.AsObject();

        Assert.Equal(SharedFixtures.Number(limits["japaneseCharacterLimit"]), SubtitleTailClipper.JapaneseCharacterLimit);
        Assert.Equal(SharedFixtures.Number(limits["englishCharacterLimit"]), SubtitleTailClipper.EnglishCharacterLimit);
        Assert.Equal(SharedFixtures.Text(limits["ellipsis"]), SubtitleTailClipper.Ellipsis);
    }

    [Theory]
    [MemberData(nameof(ClipCases))]
    public void ClipMatchesFixture(string name)
    {
        var fixture = SharedFixtures.Case("subtitle", "clip", name);

        Assert.Equal(
            Expand(fixture["expected"]!),
            SubtitleTailClipper.Clip(Expand(fixture["input"]!)));
    }

    [Fact]
    public void IdleIntervalMatchesFixture()
    {
        var assembler = SharedFixtures.Load("subtitle")["assembler"]!.AsObject();

        Assert.Equal(
            TimeSpan.FromSeconds(SharedFixtures.Number(assembler["idleFinalizeSeconds"])),
            RealtimeSubtitleAssembler.IdleFinalizeInterval);
    }

    [Theory]
    [MemberData(nameof(AssemblerCases))]
    public void AssemblerMatchesFixture(string name)
    {
        var fixture = FindAssemblerCase(name);
        var epoch = SharedFixtures.Number(fixture["epoch"]);

        var assembler = new RealtimeSubtitleAssembler();
        assembler.Reset(epoch);
        assembler.ExpectLane(
            SharedFixtures.OptionalText(fixture["expectLane"]) is { } lane
                ? RealtimeTranslationWireValues.ParseOutputLanguage(lane)
                : null);

        RealtimeSubtitleUpdate? last = null;
        foreach (var item in fixture["steps"]!.AsArray())
        {
            var step = item!.AsObject();
            var now = Origin.AddSeconds(SharedFixtures.Real(step["at"]));
            var kind = SharedFixtures.Text(step["kind"]);

            var update = kind switch
            {
                "tick" => assembler.Tick(now),
                "sourceDelta" or "translationDelta" => assembler.Ingest(
                    new RealtimeTranslationStreamEvent(
                        RealtimeTranslationWireValues.ParseOutputLanguage(SharedFixtures.Text(step["lane"])),
                        ServerEvent(kind, step),
                        SharedFixtures.OptionalNumber(step["epoch"]) ?? epoch),
                    now),
                _ => throw new Xunit.Sdk.XunitException("unhandled step kind " + kind),
            };

            last = update ?? last;
        }

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
