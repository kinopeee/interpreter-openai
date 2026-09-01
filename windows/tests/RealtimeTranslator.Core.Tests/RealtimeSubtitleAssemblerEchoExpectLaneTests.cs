using System;
using RealtimeTranslator.Core.OpenAI;
using RealtimeTranslator.Core.Realtime;
using Xunit;

namespace RealtimeTranslator.Core.Tests;

/// <summary>
/// echo lock のあと ExpectLane が中間表示を空白化する契約。
/// 本命訳が来るまでの上書きは <c>ExpectLaneOverridesFirstOutputEchoLock</c> が担う。
/// </summary>
public sealed class RealtimeSubtitleAssemblerEchoExpectLaneTests
{
    private static readonly DateTimeOffset Origin = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // Given: 同言語 echo が first-output で lock し、本命 lane の訳はまだ無い
    // When: ExpectLane で本命 lane を指定した直後に原文が伸びる
    // Then: echo 訳文を消して現行扱いを外し、誤った言語の字幕を残さない
    [Fact]
    public void ExpectLaneAfterEchoLockBlanksTranslationUntilExpectedLaneArrives()
    {
        var assembler = NewAssembler();
        assembler.Ingest(Source("Tokyo", "s1", 100), Origin);
        var echo = assembler.Ingest(
            Translation(RealtimeTranslationOutputLanguage.English, "Tokyo", "echo", 150),
            Origin.AddMilliseconds(150));

        assembler.ExpectLane(RealtimeTranslationOutputLanguage.Japanese);
        var blanked = assembler.Ingest(Source(" now", "s2", 180), Origin.AddMilliseconds(180));
        var idle = assembler.Tick(Origin.AddSeconds(9));

        Assert.Equal("Tokyo", echo?.TranslatedText);
        Assert.True(echo?.IsTranslationCurrent);
        Assert.NotNull(blanked);
        Assert.Equal("Tokyo now", blanked.Value.SourceText);
        Assert.Equal(string.Empty, blanked.Value.TranslatedText);
        Assert.False(blanked.Value.IsTranslationCurrent);
        Assert.False(blanked.Value.ShouldFinalize);
        Assert.Null(idle);
    }

    // Given: ExpectLane で echo を空白化したあと、本命 lane の訳文が届く
    // When: 本命の訳文 delta を取り込む
    // Then: 空白化を解除し、期待 lane を現行にして idle 確定できる
    [Fact]
    public void ExpectedLaneAfterEchoBlankRestoresCurrentTranslation()
    {
        var assembler = NewAssembler();
        assembler.Ingest(Source("Tokyo", "s1", 100), Origin);
        assembler.Ingest(
            Translation(RealtimeTranslationOutputLanguage.English, "Tokyo", "echo", 150),
            Origin.AddMilliseconds(150));
        assembler.ExpectLane(RealtimeTranslationOutputLanguage.Japanese);
        assembler.Ingest(Source(" now", "s2", 180), Origin.AddMilliseconds(180));

        var restored = assembler.Ingest(
            Translation(RealtimeTranslationOutputLanguage.Japanese, "東京", "ja", 220),
            Origin.AddMilliseconds(220));
        var idle = assembler.Tick(Origin.AddSeconds(9));

        Assert.NotNull(restored);
        Assert.Equal("東京", restored.Value.TranslatedText);
        Assert.True(restored.Value.IsTranslationCurrent);
        Assert.False(restored.Value.ShouldFinalize);
        Assert.NotNull(idle);
        Assert.True(idle.Value.ShouldFinalize);
        Assert.Equal("Tokyo now", idle.Value.SourceText);
        Assert.Equal("東京", idle.Value.TranslatedText);
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
