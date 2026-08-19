using System;
using RealtimeTranslator.Core.OpenAI;
using RealtimeTranslator.Core.Realtime;
using Xunit;

namespace RealtimeTranslator.Core.Tests;

public sealed class RealtimeSubtitleAssemblerTests
{
    private static readonly DateTimeOffset Origin = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // Given: 訳文が付いたあとに原文だけが伸びたセグメント
    // When: 同じ期待 lane を再指定してから idle finalize 間隔を超えて Tick する
    // Then: 再指定だけでは旧訳文を現行扱いして確定しない
    [Fact]
    public void IdleTickDoesNotFinalizeStaleTranslationAfterSourceContinues()
    {
        var assembler = NewAssembler();
        assembler.ExpectLane(RealtimeTranslationOutputLanguage.English);
        assembler.Ingest(Source("こんにちは", "s1", 100), Origin);
        assembler.Ingest(Translation(RealtimeTranslationOutputLanguage.English, "Hello", "t1", 200), Origin.AddMilliseconds(200));

        var continued = assembler.Ingest(
            Source("、皆さん", "s2", 300),
            Origin.AddMilliseconds(400));
        assembler.ExpectLane(RealtimeTranslationOutputLanguage.English);
        var idle = assembler.Tick(Origin.AddSeconds(9));

        Assert.NotNull(continued);
        Assert.Equal("こんにちは、皆さん", continued.Value.SourceText);
        Assert.Equal("Hello", continued.Value.TranslatedText);
        Assert.False(continued.Value.IsTranslationCurrent);
        Assert.False(continued.Value.ShouldFinalize);
        Assert.Null(idle);
    }

    // Given: 訳文が stale のまま idle したセグメント
    // When: 次の発話の原文が届く
    // Then: 前の原文へ連結せず、新セグメントとして表示する
    [Fact]
    public void IdleTickAbandonsStaleTranslationSoNextSourceStartsFresh()
    {
        var assembler = NewAssembler();
        assembler.ExpectLane(RealtimeTranslationOutputLanguage.English);
        assembler.Ingest(Source("こんにちは", "s1", 100), Origin);
        assembler.Ingest(Translation(RealtimeTranslationOutputLanguage.English, "Hello", "t1", 200), Origin);
        assembler.Ingest(Source("、皆さん", "s2", 300), Origin.AddMilliseconds(400));
        Assert.Null(assembler.Tick(Origin.AddSeconds(9)));

        var next = assembler.Ingest(Source("ありがとう", "s3", 400), Origin.AddSeconds(9.2));

        Assert.NotNull(next);
        Assert.Equal("ありがとう", next.Value.SourceText);
        Assert.Equal(string.Empty, next.Value.TranslatedText);
        Assert.False(next.Value.IsTranslationCurrent);
        Assert.False(next.Value.ShouldFinalize);
        Assert.Equal(1, next.Value.SegmentGeneration);
    }

    // Given: stale idle で境界だけ進めたあと、次の原文が始まっている
    // When: 既知 elapsed より大きい追いつき訳と、idle 無音より後の新しい訳が届く
    // Then: 追いつき訳は次発話に混ぜず、新しい訳だけを現行にする
    [Fact]
    public void LateTranslationAfterStaleIdleAbandonIsIgnoredByCutoff()
    {
        var assembler = NewAssembler();
        assembler.ExpectLane(RealtimeTranslationOutputLanguage.English);
        assembler.Ingest(Source("こんにちは", "s1", 100), Origin);
        assembler.Ingest(Translation(RealtimeTranslationOutputLanguage.English, "Hello", "t1", 200), Origin);
        assembler.Ingest(Source("、皆さん", "s2", 300), Origin.AddMilliseconds(400));
        Assert.Null(assembler.Tick(Origin.AddSeconds(9)));
        assembler.Ingest(Source("ありがとう", "s3", null), Origin.AddSeconds(9.2));

        var late = assembler.Ingest(
            Translation(RealtimeTranslationOutputLanguage.English, " Late", "t-late", 200),
            Origin.AddSeconds(9.3));
        var catchUp = assembler.Ingest(
            Translation(RealtimeTranslationOutputLanguage.English, " everyone", "t-catchup", 450),
            Origin.AddSeconds(9.35));
        var fresh = assembler.Ingest(
            Translation(RealtimeTranslationOutputLanguage.English, "Thank you", "t-new", 9000),
            Origin.AddSeconds(9.4));

        Assert.Null(late);
        Assert.Null(catchUp);
        Assert.NotNull(fresh);
        Assert.Equal("Thank you", fresh.Value.TranslatedText);
        Assert.True(fresh.Value.IsTranslationCurrent);
    }

    // Given: 原文継続で古くなった訳文のあと、選択 lane の訳文が追いつく
    // When: 新しい訳文 delta を取り込む
    // Then: 訳文を現行に戻し、その後の idle Tick で確定できる
    [Fact]
    public void FreshTranslationAfterStaleSourceAllowsIdleFinalize()
    {
        var assembler = NewAssembler();
        assembler.ExpectLane(RealtimeTranslationOutputLanguage.English);
        assembler.Ingest(Source("こんにちは", "s1", 100), Origin);
        assembler.Ingest(Translation(RealtimeTranslationOutputLanguage.English, "Hello", "t1", 200), Origin);
        assembler.Ingest(Source("、皆さん", "s2", 300), Origin.AddMilliseconds(400));

        var caughtUp = assembler.Ingest(
            Translation(RealtimeTranslationOutputLanguage.English, " everyone", "t2", 450),
            Origin.AddMilliseconds(500));
        var idle = assembler.Tick(Origin.AddSeconds(9));

        Assert.NotNull(caughtUp);
        Assert.True(caughtUp.Value.IsTranslationCurrent);
        Assert.NotNull(idle);
        Assert.True(idle.Value.ShouldFinalize);
        Assert.Equal("こんにちは、皆さん", idle.Value.SourceText);
        Assert.Equal("Hello everyone", idle.Value.TranslatedText);
    }

    // Given: 期待 lane がまだ無く、同言語 echo が先に first-output で lock した
    // When: その後 ExpectLane で本命 lane を指定し、本命の訳文が来る
    // Then: echo では lane を固定せず、期待 lane の訳文を表示する
    [Fact]
    public void ExpectLaneOverridesFirstOutputEchoLock()
    {
        var assembler = NewAssembler();
        assembler.Ingest(Source("Tokyo", "s1", 100), Origin);
        var echo = assembler.Ingest(
            Translation(RealtimeTranslationOutputLanguage.English, "Tokyo", "echo", 150),
            Origin.AddMilliseconds(150));

        assembler.ExpectLane(RealtimeTranslationOutputLanguage.Japanese);
        var update = assembler.Ingest(
            Translation(RealtimeTranslationOutputLanguage.Japanese, "東京", "ja", 200),
            Origin.AddMilliseconds(200));

        Assert.Equal("Tokyo", echo?.TranslatedText);
        Assert.NotNull(update);
        Assert.Equal("東京", update.Value.TranslatedText);
        Assert.True(update.Value.IsTranslationCurrent);
        Assert.False(update.Value.ShouldFinalize);
    }

    // Given: idle 確定したセグメントのあと次の原文が始まっている
    // When: 確定済みセグメントより古い elapsed_ms の訳文が遅れて届く
    // Then: 次発話の訳文として混ぜない
    [Fact]
    public void LateTranslationAfterNextSourceIsIgnoredByFinalizedCutoff()
    {
        var assembler = NewAssembler();
        assembler.ExpectLane(RealtimeTranslationOutputLanguage.English);
        assembler.Ingest(Source("こんにちは", "s1", 100), Origin);
        assembler.Ingest(Translation(RealtimeTranslationOutputLanguage.English, "Hello", "t1", 200), Origin);
        var finalized = assembler.Tick(Origin.AddSeconds(9));
        assembler.ExpectLane(RealtimeTranslationOutputLanguage.English);

        var nextSource = assembler.Ingest(Source("ありがとう", "s2", null), Origin.AddSeconds(9.2));
        var late = assembler.Ingest(
            Translation(RealtimeTranslationOutputLanguage.English, " Late", "t-late", 200),
            Origin.AddSeconds(9.3));
        var fresh = assembler.Ingest(
            Translation(RealtimeTranslationOutputLanguage.English, "Thank you", "t-new", 400),
            Origin.AddSeconds(9.4));

        Assert.True(finalized?.ShouldFinalize);
        Assert.Equal("ありがとう", nextSource?.SourceText);
        Assert.Equal(string.Empty, nextSource?.TranslatedText);
        Assert.Null(late);
        Assert.NotNull(fresh);
        Assert.Equal("Thank you", fresh.Value.TranslatedText);
        Assert.True(fresh.Value.IsTranslationCurrent);
    }

    // Given: 訳文が先に届いたセグメント
    // When: 最初の原文 delta を取り込む
    // Then: 同じ発話の訳文を stale 扱いにしない
    [Fact]
    public void FirstSourceAfterTranslationKeepsTranslationCurrent()
    {
        var assembler = NewAssembler();
        assembler.ExpectLane(RealtimeTranslationOutputLanguage.English);
        assembler.Ingest(
            Translation(RealtimeTranslationOutputLanguage.English, "Hello", "t1", 50),
            Origin);
        var update = assembler.Ingest(Source("こんにちは", "s1", null), Origin.AddMilliseconds(80));

        Assert.NotNull(update);
        Assert.Equal("こんにちは", update.Value.SourceText);
        Assert.Equal("Hello", update.Value.TranslatedText);
        Assert.True(update.Value.IsTranslationCurrent);
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
