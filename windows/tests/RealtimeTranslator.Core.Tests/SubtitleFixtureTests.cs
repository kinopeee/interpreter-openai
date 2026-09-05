using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Nodes;
using RealtimeTranslator.Core.Audio;
using RealtimeTranslator.Core.OpenAI;
using RealtimeTranslator.Core.Realtime;
using RealtimeTranslator.Core.Subtitles;
using Xunit;

namespace RealtimeTranslator.Core.Tests;

public sealed class SubtitleFixtureTests
{
    private static readonly DateTimeOffset Origin = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static TheoryData<string> ClipCases => SharedFixtures.CaseNames("subtitle", "clip", 2);

    public static TheoryData<string> AssemblerCases => AssemblerCaseNames();

    public static TheoryData<string> BoundaryCases => BoundaryCaseNames();

    // Given: shared fixture の字幕文字数上限
    // When: clipper の定数と照合する
    // Then: 日本語 60 / 英語 120 / 省略記号が一致する
    [Fact]
    public void LimitsMatchFixture()
    {
        // Given: subtitle clip limits fixture
        var limits = SharedFixtures.Load("subtitle", 2)["limits"]!.AsObject();

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
        var fixture = SharedFixtures.Case("subtitle", "clip", name, 2);

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
        var assembler = SharedFixtures.Load("subtitle", 2)["assembler"]!.AsObject();

        // When/Then: IdleFinalizeInterval が一致する
        Assert.Equal(
            TimeSpan.FromSeconds(SharedFixtures.Number(assembler["idleFinalizeSeconds"])),
            RealtimeSubtitleAssembler.IdleFinalizeInterval);
    }

    [Theory]
    [MemberData(nameof(BoundaryCases))]
    public void BoundaryMatchesFixture(string name)
    {
        // Given: v2 boundary fixture
        var fixture = FindBoundaryCase(name);
            var pair = LanguagePairExtensions.ParseLanguagePair(
                SharedFixtures.Text(fixture["pair"]));
            var currentLanguage = ParseLanguage(SharedFixtures.Text(fixture["currentLanguage"]));
            var currentTarget = pair.TranslationTarget(currentLanguage)
                ?? throw new Xunit.Sdk.XunitException("missing initial target");
            var reverseEvidenceCount = 0;
            var routing = string.Empty;
            var source = string.Empty;
            var tracker = new SourceBoundaryTracker();
            var candidates = new List<int?>();
            int? switchDelta = null;

            // When: source delta を routing / detector / selector / tracker に順に渡す
            for (var index = 0; index < fixture["deltas"]!.AsArray().Count; index += 1)
            {
                var delta = SharedFixtures.Text(fixture["deltas"]![index]);
                var deltaStart = source.Length;
                source += delta;
                routing = RoutingSourceTextWindow.Trim(routing + delta, pair);
                var evidence = SpokenLanguageDetector.RecentEvidence(routing, pair);
                var selection = TranslationTargetSelector.Select(
                    pair,
                    currentTarget,
                    reverseEvidenceCount,
                    evidence);
                reverseEvidenceCount = selection.ReverseEvidenceCount;

                if (selection.Target == currentTarget)
                {
                    tracker.Observe(
                        source,
                        deltaStart,
                        0,
                        pair,
                        currentLanguage,
                        reverseEvidenceCount);
                    candidates.Add(tracker.CandidateOffset);
                }
                else
                {
                    if (pair != LanguagePair.EnEs)
                    {
                        tracker.Observe(
                            source,
                            deltaStart,
                            0,
                            pair,
                            currentLanguage,
                            0);
                    }

                    candidates.Add(tracker.CandidateOffset ?? deltaStart);
                    switchDelta = index;
                    break;
                }
            }

        var expectedCandidates = new List<int?>();
        foreach (var value in fixture["expectedCandidateOffsets"]!.AsArray())
        {
            expectedCandidates.Add(value is null ? null : SharedFixtures.Number(value));
        }

        Assert.Equal(expectedCandidates, candidates);
        Assert.Equal(
            SharedFixtures.OptionalNumber(fixture["expectedSwitchAtDelta"]),
            switchDelta);

        if (switchDelta is { } switchIndex)
        {
            var splitOffset = candidates[switchIndex] ?? source.Length;
            Assert.Equal(
                SharedFixtures.OptionalText(fixture["expectedOldSource"]),
                source[..splitOffset]);
            Assert.Equal(
                SharedFixtures.OptionalText(fixture["expectedNewSource"]),
                source[splitOffset..]);
        }
    }

    // Given: en-es pair の英語原文と未選択の翻訳 lane
    // When: assembler が原文の文字種を補助信号として使う
    // Then: 話者英語の相手側であるスペイン語 lane を選ぶ
    [Fact]
    public void EnEsFallbackSelectsSpanishLaneForEnglishSource()
    {
        var assembler = new RealtimeSubtitleAssembler(LanguagePair.EnEs);
        assembler.Reset(1);

        assembler.Ingest(
            new RealtimeTranslationStreamEvent(
                RealtimeTranslationLane.Source,
                new RealtimeTranslationServerEvent.InputTranscriptDelta(
                    "the meeting is today",
                    "source-1",
                    null),
                1),
            Origin);

        var update = assembler.Ingest(
            new RealtimeTranslationStreamEvent(
                RealtimeTranslationLane.Translation(RealtimeTranslationOutputLanguage.Spanish),
                new RealtimeTranslationServerEvent.OutputTranscriptDelta(
                    "la reunión es hoy",
                    "translation-1",
                    null),
                1),
            Origin);

        Assert.NotNull(update);
        Assert.Equal("la reunión es hoy", update.Value.TranslatedText);
    }

    // Given: ja-es pair の日本語原文と未選択の翻訳 lane
    // When: assembler が原文の文字種を補助信号として使う
    // Then: 話者日本語の相手側であるスペイン語 lane を選ぶ（既定 ja-en の英語 lane ではない）
    [Fact]
    public void JaEsFallbackSelectsSpanishLaneForJapaneseSource()
    {
        var assembler = new RealtimeSubtitleAssembler(LanguagePair.JaEs);
        assembler.Reset(1);

        assembler.Ingest(
            new RealtimeTranslationStreamEvent(
                RealtimeTranslationLane.Source,
                new RealtimeTranslationServerEvent.InputTranscriptDelta(
                    "会議を始めます",
                    "source-jaes",
                    null),
                1),
            Origin);

        var update = assembler.Ingest(
            new RealtimeTranslationStreamEvent(
                RealtimeTranslationLane.Translation(RealtimeTranslationOutputLanguage.Spanish),
                new RealtimeTranslationServerEvent.OutputTranscriptDelta(
                    "Empezamos la reunión",
                    "translation-jaes",
                    null),
                1),
            Origin);

        Assert.NotNull(update);
        Assert.Equal("Empezamos la reunión", update.Value.TranslatedText);
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
        var finalizedPairs = new List<(string Source, string Translation)>();
        foreach (var item in fixture["steps"]!.AsArray())
        {
            var step = item!.AsObject();
            var now = Origin.AddSeconds(SharedFixtures.Real(step["at"]));
            var kind = SharedFixtures.Text(step["kind"]);

            var update = kind switch
            {
                "tick" => assembler.Tick(now),
                "languageSwitch" => Split(
                    assembler,
                    SharedFixtures.Number(step["boundaryOffset"]),
                    now,
                    finalizedPairs),
                "sourceDelta" or "translationDelta" => assembler.Ingest(
                    new RealtimeTranslationStreamEvent(
                        ParseLane(SharedFixtures.Text(step["lane"])),
                        ServerEvent(kind, step),
                        SharedFixtures.OptionalNumber(step["epoch"]) ?? epoch),
                    now),
                _ => throw new Xunit.Sdk.XunitException("unhandled step kind " + kind),
            };

            last = update ?? last;
            if (update is { ShouldFinalize: true })
            {
                finalizedPairs.Add((update.Value.SourceText, update.Value.TranslatedText));
            }
        }

        // Then: 最終字幕更新が期待どおり
        var expectedPairs = fixture["expectedFinalizedPairs"]!.AsArray();
        Assert.Equal(expectedPairs.Count, finalizedPairs.Count);
        for (var index = 0; index < expectedPairs.Count; index += 1)
        {
            var expectedPair = expectedPairs[index]!.AsObject();
            Assert.Equal(SharedFixtures.Text(expectedPair["sourceText"]), finalizedPairs[index].Source);
            Assert.Equal(SharedFixtures.Text(expectedPair["translatedText"]), finalizedPairs[index].Translation);
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

    private static SpokenLanguage ParseLanguage(string value) =>
        value switch
        {
            "ja" => SpokenLanguage.Japanese,
            "en" => SpokenLanguage.English,
            "es" => SpokenLanguage.Spanish,
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, "unknown language code"),
        };

    private static RealtimeSubtitleUpdate Split(
        RealtimeSubtitleAssembler assembler,
        int offset,
        DateTimeOffset now,
        List<(string Source, string Translation)> finalizedPairs)
    {
        var split = assembler.SplitForLanguageSwitch(offset, now);
        if (split.Finalized is { } finalized)
        {
            finalizedPairs.Add((finalized.SourceText, finalized.TranslatedText));
        }

        return split.Current;
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
        foreach (var item in SharedFixtures.Load("subtitle", 2)["assembler"]!["cases"]!.AsArray())
        {
            data.Add(SharedFixtures.Text(item?["name"]));
        }

        return data;
    }

    private static TheoryData<string> BoundaryCaseNames()
    {
        var data = new TheoryData<string>();
        foreach (var item in SharedFixtures.Load("subtitle", 2)["boundary"]!["cases"]!.AsArray())
        {
            data.Add(SharedFixtures.Text(item?["name"]));
        }

        return data;
    }

    private static JsonObject FindBoundaryCase(string name)
    {
        foreach (var item in SharedFixtures.Load("subtitle", 2)["boundary"]!["cases"]!.AsArray())
        {
            if (item is JsonObject candidate && SharedFixtures.Text(candidate["name"]) == name)
            {
                return candidate;
            }
        }

        throw new Xunit.Sdk.XunitException("no boundary case named " + name);
    }

    private static JsonObject FindAssemblerCase(string name)
    {
        foreach (var item in SharedFixtures.Load("subtitle", 2)["assembler"]!["cases"]!.AsArray())
        {
            if (item is JsonObject candidate && SharedFixtures.Text(candidate["name"]) == name)
            {
                return candidate;
            }
        }

        throw new Xunit.Sdk.XunitException("no assembler case named " + name);
    }
}
