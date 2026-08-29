using System;
using RealtimeTranslator.Core.Audio;
using RealtimeTranslator.Core.OpenAI;
using RealtimeTranslator.Core.Realtime;
using Xunit;

namespace RealtimeTranslator.Core.Tests;

/// <summary>
/// 選択済み lane の現行フラグと、ExpectLane 待ち中の兄弟 first-output。
/// echo lock 後の ExpectLane 上書き（既存 assembler / #111）とは交差しない。
/// </summary>
public sealed class RealtimeSubtitleAssemblerSiblingLaneTests
{
    private static readonly DateTimeOffset Origin = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // Given: 期待 lane の訳文が現行になっている
    // When: 兄弟 lane の delta が届く
    // Then: 表示中の選択 lane は現行のまま。兄弟は buffer のみで表示を差し替えない
    [Fact]
    public void SiblingLaneDeltaDoesNotClearSelectedTranslationCurrency()
    {
        var assembler = NewAssembler();
        assembler.ExpectLane(RealtimeTranslationOutputLanguage.English);
        assembler.Ingest(Source("こんにちは", "s1", 100), Origin);
        var selected = assembler.Ingest(
            Translation(RealtimeTranslationOutputLanguage.English, "Hello", "en-1", 200),
            Origin.AddMilliseconds(200));

        var sibling = assembler.Ingest(
            Translation(RealtimeTranslationOutputLanguage.Spanish, "Hola", "es-1", 250),
            Origin.AddMilliseconds(250));

        Assert.NotNull(selected);
        Assert.Equal("Hello", selected.Value.TranslatedText);
        Assert.True(selected.Value.IsTranslationCurrent);
        Assert.NotNull(sibling);
        Assert.Equal("こんにちは", sibling.Value.SourceText);
        Assert.Equal("Hello", sibling.Value.TranslatedText);
        Assert.True(sibling.Value.IsTranslationCurrent);
        Assert.False(sibling.Value.ShouldFinalize);
    }

    // Given: ExpectLane で本命を先に指定した
    // When: 兄弟 lane が first-output する
    // Then: 兄弟では lane を lock せず、訳文スロットは空のまま本命を待つ
    [Fact]
    public void ExpectedLaneWaitsAndDoesNotLockOnSiblingFirstOutput()
    {
        var assembler = NewAssembler();
        assembler.ExpectLane(RealtimeTranslationOutputLanguage.English);
        assembler.Ingest(Source("hola a todos", "s1", 100), Origin);

        var sibling = assembler.Ingest(
            Translation(RealtimeTranslationOutputLanguage.Spanish, "hola a todos", "echo-es", 150),
            Origin.AddMilliseconds(150));
        var expected = assembler.Ingest(
            Translation(RealtimeTranslationOutputLanguage.English, "hello everyone", "en-1", 200),
            Origin.AddMilliseconds(200));

        Assert.NotNull(sibling);
        Assert.Equal("hola a todos", sibling.Value.SourceText);
        Assert.Equal(string.Empty, sibling.Value.TranslatedText);
        Assert.False(sibling.Value.IsTranslationCurrent);
        Assert.NotNull(expected);
        Assert.Equal("hello everyone", expected.Value.TranslatedText);
        Assert.True(expected.Value.IsTranslationCurrent);
        Assert.False(expected.Value.ShouldFinalize);
    }

    private static RealtimeSubtitleAssembler NewAssembler()
    {
        var assembler = new RealtimeSubtitleAssembler(LanguagePair.JaEs);
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
