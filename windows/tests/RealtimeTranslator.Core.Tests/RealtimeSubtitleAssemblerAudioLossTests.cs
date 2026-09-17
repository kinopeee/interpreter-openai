using System;
using RealtimeTranslator.Core.OpenAI;
using RealtimeTranslator.Core.Realtime;
using Xunit;

namespace RealtimeTranslator.Core.Tests;

public sealed class RealtimeSubtitleAssemblerAudioLossTests
{
    private static readonly DateTimeOffset Origin =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // Given: 音声欠落を検知して汚染窓を開始する
    // When: 原文と訳文を受け取り、idle finalize を評価する
    // Then: 確定せず、無効化更新で汚染セグメントのバッファを破棄する
    [Fact]
    public void TaintedSegmentIsAbandonedInsteadOfFinalized()
    {
        var assembler = NewAssembler();
        assembler.MarkAudioLoss(Origin);
        assembler.Ingest(Source("こんにちは", "s1"), Origin.AddMilliseconds(1));
        assembler.Ingest(Translation("Hello", "t1"), Origin.AddMilliseconds(2));

        var update = assembler.Tick(Origin.AddSeconds(9));

        Assert.NotNull(update);
        Assert.True(update.Value.IsInvalidation);
        Assert.False(update.Value.ShouldFinalize);
        Assert.Equal(string.Empty, assembler.CurrentSourceText);
        Assert.False(assembler.IsCurrentSegmentTainted);
    }

    // Given: 音声欠落の汚染窓が期限切れになっている
    // When: 期限後に新しい原文と訳文を受け取り idle finalize する
    // Then: 新しいセグメントは通常どおり確定する
    [Fact]
    public void SegmentAfterTaintWindowCanFinalize()
    {
        var assembler = NewAssembler();
        assembler.MarkAudioLoss(Origin);
        assembler.Ingest(
            Source("ありがとう", "s1"),
            Origin.Add(RealtimeSubtitleAssembler.AudioLossTaintWindow).AddMilliseconds(1));
        assembler.Ingest(Translation("Thank you", "t1"), Origin.AddSeconds(8.2));

        var update = assembler.Tick(Origin.AddSeconds(17));

        Assert.NotNull(update);
        Assert.True(update.Value.ShouldFinalize);
        Assert.Equal("ありがとう", update.Value.SourceText);
        Assert.Equal("Thank you", update.Value.TranslatedText);
    }

    // Given: 音声欠落より前に字幕ペアが確定している
    // When: その後に音声欠落を通知する
    // Then: 既に確定した更新は変更されない
    [Fact]
    public void FinalizedSegmentIsPreservedAcrossAudioLoss()
    {
        var assembler = NewAssembler();
        assembler.Ingest(Source("こんにちは", "s1"), Origin);
        assembler.Ingest(Translation("Hello", "t1"), Origin.AddMilliseconds(1));
        var finalized = assembler.Tick(Origin.AddSeconds(9));

        assembler.MarkAudioLoss(Origin.AddSeconds(10));

        Assert.NotNull(finalized);
        Assert.True(finalized.Value.ShouldFinalize);
        Assert.Equal("こんにちは", finalized.Value.SourceText);
        Assert.Equal("Hello", finalized.Value.TranslatedText);
    }

    // Given: 音声欠落後の汚染セグメントに完全なペアがある
    // When: 言語切替で分割する
    // Then: 汚染された prefix は確定しない
    [Fact]
    public void TaintedLanguageSwitchDoesNotFinalizePrefix()
    {
        var assembler = NewAssembler();
        assembler.MarkAudioLoss(Origin);
        assembler.Ingest(Source("こんにちは", "s1"), Origin.AddMilliseconds(1));
        assembler.Ingest(Translation("Hello", "t1"), Origin.AddMilliseconds(2));

        var split = assembler.SplitForLanguageSwitch(3, Origin.AddMilliseconds(3));

        Assert.Null(split.Finalized);
    }

    // Given: 汚染された原文の途中に言語切替境界がある
    // When: suffix を残したあと新しい訳文を受け取り idle finalize する
    // Then: 汚染 suffix は確定せず無効化される
    [Fact]
    public void TaintedLanguageSwitchKeepsSuffixFromFinalizing()
    {
        var assembler = NewAssembler();
        assembler.MarkAudioLoss(Origin);
        assembler.Ingest(Source("こんにちはHello", "s1"), Origin.AddMilliseconds(1));
        assembler.Ingest(Translation("Hello", "t1"), Origin.AddMilliseconds(2));

        var split = assembler.SplitForLanguageSwitch(5, Origin.AddMilliseconds(3));

        Assert.Null(split.Finalized);
        Assert.Equal("Hello", assembler.CurrentSourceText);
        Assert.True(assembler.IsCurrentSegmentTainted);

        assembler.ExpectLane(RealtimeTranslationOutputLanguage.Japanese);
        assembler.Ingest(
            new RealtimeTranslationStreamEvent(
                RealtimeTranslationLane.Translation(RealtimeTranslationOutputLanguage.Japanese),
                new RealtimeTranslationServerEvent.OutputTranscriptDelta("こんにちは", "t2", 300),
                1),
            Origin.AddSeconds(1));
        var update = assembler.Tick(Origin.AddSeconds(10));

        Assert.NotNull(update);
        Assert.True(update.Value.IsInvalidation);
        Assert.False(update.Value.ShouldFinalize);
        Assert.Equal(string.Empty, assembler.CurrentSourceText);
        Assert.False(assembler.IsCurrentSegmentTainted);
    }

    private static RealtimeSubtitleAssembler NewAssembler()
    {
        var assembler = new RealtimeSubtitleAssembler();
        assembler.Reset(1);
        assembler.ExpectLane(RealtimeTranslationOutputLanguage.English);
        return assembler;
    }

    private static RealtimeTranslationStreamEvent Source(string text, string eventId) =>
        new(
            RealtimeTranslationLane.Source,
            new RealtimeTranslationServerEvent.InputTranscriptDelta(text, eventId, 100),
            1);

    private static RealtimeTranslationStreamEvent Translation(string text, string eventId) =>
        new(
            RealtimeTranslationLane.Translation(RealtimeTranslationOutputLanguage.English),
            new RealtimeTranslationServerEvent.OutputTranscriptDelta(text, eventId, 200),
            1);
}
