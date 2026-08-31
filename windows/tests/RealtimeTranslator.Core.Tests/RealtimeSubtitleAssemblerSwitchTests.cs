using System;
using RealtimeTranslator.Core.OpenAI;
using RealtimeTranslator.Core.Realtime;
using Xunit;

namespace RealtimeTranslator.Core.Tests;

/// <summary>
/// 言語切替境界の assembler 契約。開いている PR が触る RealtimeSubtitleAssemblerTests とは分ける。
/// </summary>
public sealed class RealtimeSubtitleAssemblerSwitchTests
{
    private static readonly DateTimeOffset Origin = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // Given: 原文だけのセグメントで言語切替確定した（不完全ペア）
    // When: 次の原文より先に旧セグメントの訳文が届く
    // Then: 切替後の新発話へ混ぜず、次の原文から新しいセグメントを始める
    [Fact]
    public void LateTranslationAfterIncompleteLanguageSwitchIsIgnored()
    {
        var assembler = NewAssembler();
        assembler.ExpectLane(RealtimeTranslationOutputLanguage.English);
        assembler.Ingest(Source("こんにちは", "s1", 100), Origin);

        var switched = assembler.FinalizeForLanguageSwitch(Origin.AddMilliseconds(150));
        var late = assembler.Ingest(
            Translation(RealtimeTranslationOutputLanguage.English, "Hello", "t-late", 200),
            Origin.AddMilliseconds(200));
        var next = assembler.Ingest(Source("Hello there", "s2", 300), Origin.AddMilliseconds(250));

        Assert.Null(switched);
        Assert.Null(late);
        Assert.NotNull(next);
        Assert.Equal("Hello there", next.Value.SourceText);
        Assert.Equal(string.Empty, next.Value.TranslatedText);
        Assert.False(next.Value.IsTranslationCurrent);
        Assert.False(next.Value.ShouldFinalize);
        Assert.Equal(1, next.Value.SegmentGeneration);
    }

    // Given: 不完全切替で遅延訳を捨てたあと、次の原文が始まっている
    // When: 新しい訳文が届く
    // Then: 切替前の原文へ戻さず、新発話の訳として現行にする
    [Fact]
    public void FreshTranslationAfterIncompleteSwitchAttachesToNextSourceOnly()
    {
        var assembler = NewAssembler();
        assembler.ExpectLane(RealtimeTranslationOutputLanguage.English);
        assembler.Ingest(Source("こんにちは", "s1", 100), Origin);
        assembler.FinalizeForLanguageSwitch(Origin.AddMilliseconds(150));
        Assert.Null(assembler.Ingest(
            Translation(RealtimeTranslationOutputLanguage.English, "Hello", "t-late", 200),
            Origin.AddMilliseconds(200)));

        assembler.ExpectLane(RealtimeTranslationOutputLanguage.Japanese);
        assembler.Ingest(Source("Hello there", "s2", 300), Origin.AddMilliseconds(250));
        var fresh = assembler.Ingest(
            Translation(RealtimeTranslationOutputLanguage.Japanese, "こんにちは、皆さん", "t-new", 400),
            Origin.AddMilliseconds(350));

        Assert.NotNull(fresh);
        Assert.Equal("Hello there", fresh.Value.SourceText);
        Assert.Equal("こんにちは、皆さん", fresh.Value.TranslatedText);
        Assert.True(fresh.Value.IsTranslationCurrent);
        Assert.False(fresh.Value.ShouldFinalize);
    }

    private static RealtimeSubtitleAssembler NewAssembler()
    {
        var assembler = new RealtimeSubtitleAssembler();
        assembler.Reset(1);
        return assembler;
    }

    private static RealtimeTranslationStreamEvent Source(string text, string eventId, int? elapsedMs) =>
        new(
            RealtimeTranslationLane.Source,
            new RealtimeTranslationServerEvent.InputTranscriptDelta(text, eventId, elapsedMs),
            1);

    private static RealtimeTranslationStreamEvent Translation(
        RealtimeTranslationOutputLanguage target,
        string text,
        string eventId,
        int? elapsedMs) =>
        new(
            RealtimeTranslationLane.Translation(target),
            new RealtimeTranslationServerEvent.OutputTranscriptDelta(text, eventId, elapsedMs),
            1);
}
