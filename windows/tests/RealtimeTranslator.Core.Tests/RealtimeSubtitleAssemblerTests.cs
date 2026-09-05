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
    // When: 捨てたセグメントより古い elapsed の訳と、新しい訳が届く
    // Then: 遅延訳は次発話に混ぜず、新しい訳だけを現行にする
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
        var fresh = assembler.Ingest(
            Translation(RealtimeTranslationOutputLanguage.English, "Thank you", "t-new", 400),
            Origin.AddSeconds(9.4));

        Assert.Null(late);
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

    // Given: echo が first-output で lock したあと、本命 lane の訳文がすでに buffer にある
    // When: ExpectLane で本命 lane を指定してから idle Tick する
    // Then: 後着の本命訳を待たず、buffer 済みの期待 lane を即選択して確定する
    [Fact]
    public void ExpectLaneSwitchesImmediatelyWhenExpectedTranslationIsAlreadyBuffered()
    {
        var assembler = NewAssembler();
        assembler.Ingest(Source("Tokyo", "s1", 100), Origin);
        assembler.Ingest(
            Translation(RealtimeTranslationOutputLanguage.English, "Tokyo", "echo", 150),
            Origin.AddMilliseconds(150));
        var buffered = assembler.Ingest(
            Translation(RealtimeTranslationOutputLanguage.Japanese, "東京", "ja", 200),
            Origin.AddMilliseconds(200));

        assembler.ExpectLane(RealtimeTranslationOutputLanguage.Japanese);
        var idle = assembler.Tick(Origin.AddSeconds(9));

        Assert.Equal("Tokyo", buffered?.TranslatedText);
        Assert.NotNull(idle);
        Assert.True(idle.Value.ShouldFinalize);
        Assert.Equal("東京", idle.Value.TranslatedText);
        Assert.True(idle.Value.IsTranslationCurrent);
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

    // Given: 原文だけのセグメントで、空の英語訳が先に届く
    // When: そのあと日本語訳が届く
    // Then: 空 delta は first-output として lane を固定せず、日本語訳が現行になる
    [Fact]
    public void EmptyTranslationDeltaDoesNotLockFirstOutputLane()
    {
        var assembler = NewAssembler();
        assembler.Ingest(Source("hello", "s1", 100), Origin);

        var empty = assembler.Ingest(
            Translation(RealtimeTranslationOutputLanguage.English, string.Empty, "t-empty", 150),
            Origin.AddMilliseconds(150));
        var japanese = assembler.Ingest(
            Translation(RealtimeTranslationOutputLanguage.Japanese, "こんにちは", "t-ja", 200),
            Origin.AddMilliseconds(200));

        Assert.Null(empty);
        Assert.NotNull(japanese);
        Assert.Equal("hello", japanese.Value.SourceText);
        Assert.Equal("こんにちは", japanese.Value.TranslatedText);
        Assert.True(japanese.Value.IsTranslationCurrent);
        Assert.False(japanese.Value.ShouldFinalize);
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

    // Given: 翻訳 lane に乗った input_transcript
    // When: assembler へ取り込む
    // Then: 原文 authority にせず、後続の source lane 原文だけを表示する
    [Fact]
    public void InputTranscriptDeltaOnTranslationLaneIsIgnored()
    {
        var assembler = NewAssembler();
        assembler.ExpectLane(RealtimeTranslationOutputLanguage.English);

        var polluted = assembler.Ingest(
            new RealtimeTranslationStreamEvent(
                RealtimeTranslationLane.Translation(RealtimeTranslationOutputLanguage.English),
                new RealtimeTranslationServerEvent.InputTranscriptDelta("polluting source", "p1", 10),
                1),
            Origin);
        var source = assembler.Ingest(Source("こんにちは", "s1", 100), Origin.AddMilliseconds(80));

        Assert.Null(polluted);
        Assert.NotNull(source);
        Assert.Equal("こんにちは", source.Value.SourceText);
        Assert.Equal(string.Empty, source.Value.TranslatedText);
        Assert.False(source.Value.IsTranslationCurrent);
    }

    // Given: 期待 lane 未出力のまま、別 lane の echo だけが先に buffer されている
    // When: 最初の原文 delta を取り込む
    // Then: orphan echo を捨てて新セグメントを始め、旧訳文と対にしない
    [Fact]
    public void FirstSourceAfterOrphanEchoClearsWrongLaneTranslation()
    {
        var assembler = NewAssembler();
        assembler.ExpectLane(RealtimeTranslationOutputLanguage.Japanese);
        var echo = assembler.Ingest(
            Translation(RealtimeTranslationOutputLanguage.English, "Tokyo", "echo", 50),
            Origin);

        var source = assembler.Ingest(Source("こんにちは", "s1", 80), Origin.AddMilliseconds(80));

        Assert.NotNull(echo);
        Assert.Equal(string.Empty, echo.Value.TranslatedText);
        Assert.NotNull(source);
        Assert.Equal("こんにちは", source.Value.SourceText);
        Assert.Equal(string.Empty, source.Value.TranslatedText);
        Assert.False(source.Value.IsTranslationCurrent);
        Assert.Equal(1, source.Value.SegmentGeneration);
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

    // Given: ExpectLane(Japanese) で英語入力、期待 lane はまだ未出力
    // When: 英語 echo が先に届き、そのあと日本語訳が届く
    // Then: echo の時点では訳文を出さず、日本語到着後にだけ現行訳にする
    [Fact]
    public void ExpectedLaneSuppressesEchoTranslationUntilExpectedLaneOutputs()
    {
        var assembler = NewAssembler();
        assembler.ExpectLane(RealtimeTranslationOutputLanguage.Japanese);
        assembler.Ingest(Source("Hello there", "s1", 100), Origin);

        var echo = assembler.Ingest(
            Translation(RealtimeTranslationOutputLanguage.English, "Hello there", "t-echo", 150),
            Origin.AddMilliseconds(150));
        var expected = assembler.Ingest(
            Translation(RealtimeTranslationOutputLanguage.Japanese, "こんにちは", "t1", 200),
            Origin.AddMilliseconds(200));

        Assert.NotNull(echo);
        Assert.Equal("Hello there", echo.Value.SourceText);
        Assert.Equal(string.Empty, echo.Value.TranslatedText);
        Assert.False(echo.Value.IsTranslationCurrent);
        Assert.NotNull(expected);
        Assert.Equal("こんにちは", expected.Value.TranslatedText);
        Assert.True(expected.Value.IsTranslationCurrent);
        Assert.False(expected.Value.ShouldFinalize);
    }

    // Given: 完全な進行中ペア（訳文 event_id t1）
    // When: 同じ event_id の訳文 delta が再送される
    // Then: 二重追記せず、表示中の訳文は元のまま
    [Fact]
    public void DuplicateTranslationEventIdIsIgnored()
    {
        var assembler = NewAssembler();
        assembler.ExpectLane(RealtimeTranslationOutputLanguage.English);
        assembler.Ingest(Source("こんにちは", "s1", 100), Origin);
        assembler.Ingest(Translation(RealtimeTranslationOutputLanguage.English, "Hello", "t1", 200), Origin);

        var duplicate = assembler.Ingest(
            Translation(RealtimeTranslationOutputLanguage.English, " again", "t1", 250),
            Origin.AddMilliseconds(250));
        var idle = assembler.Tick(Origin.AddSeconds(9));

        Assert.Null(duplicate);
        Assert.NotNull(idle);
        Assert.Equal("こんにちは", idle.Value.SourceText);
        Assert.Equal("Hello", idle.Value.TranslatedText);
        Assert.True(idle.Value.ShouldFinalize);
    }

    // Given: idle 確定したセグメント（訳文 elapsed_ms が cutoff になる）
    // When: cutoff 以下の elapsed を持つ遅延原文が届く
    // Then: 新セグメントへ連結せず、確定済みペアを上書きしない
    [Fact]
    public void LateSourceDeltaAfterFinalizeIsIgnoredByElapsedCutoff()
    {
        var assembler = NewAssembler();
        assembler.ExpectLane(RealtimeTranslationOutputLanguage.English);
        assembler.Ingest(Source("こんにちは", "s1", 100), Origin);
        assembler.Ingest(Translation(RealtimeTranslationOutputLanguage.English, "Hello", "t1", 200), Origin);
        var finalized = assembler.Tick(Origin.AddSeconds(9));

        var late = assembler.Ingest(Source("遅延原文", "s-late", 150), Origin.AddSeconds(9.2));
        var fresh = assembler.Ingest(Source("ありがとう", "s-new", 400), Origin.AddSeconds(9.4));

        Assert.True(finalized?.ShouldFinalize);
        Assert.Equal("こんにちは", finalized?.SourceText);
        Assert.Equal("Hello", finalized?.TranslatedText);
        Assert.Null(late);
        Assert.NotNull(fresh);
        Assert.Equal("ありがとう", fresh.Value.SourceText);
        Assert.Equal(string.Empty, fresh.Value.TranslatedText);
        Assert.False(fresh.Value.IsTranslationCurrent);
        Assert.Equal(1, fresh.Value.SegmentGeneration);
    }

    // Given: epoch 1 で event_id を消費し、idle 確定で elapsed cutoff が残っている assembler
    // When: BeginNewEpoch(2) したあと、同じ event_id と旧 cutoff 以下の elapsed で新接続の delta が届く
    // Then: 再接続後の字幕を重複/遅延扱いせず、原文も訳文も表示する
    [Fact]
    public void BeginNewEpochAcceptsReusedEventIdsAndElapsedBelowOldCutoff()
    {
        var assembler = NewAssembler();
        assembler.ExpectLane(RealtimeTranslationOutputLanguage.English);
        assembler.Ingest(Source("こんにちは", "s1", 100), Origin);
        assembler.Ingest(
            Translation(RealtimeTranslationOutputLanguage.English, "Hello", "t1", 200),
            Origin);
        var finalized = assembler.Tick(Origin.AddSeconds(9));
        Assert.True(finalized?.ShouldFinalize);

        assembler.BeginNewEpoch(2);
        assembler.ExpectLane(RealtimeTranslationOutputLanguage.English);

        var reusedSource = assembler.Ingest(
            Source("ありがとう", "s1", 50, epoch: 2),
            Origin.AddSeconds(10));
        var reusedTranslation = assembler.Ingest(
            Translation(RealtimeTranslationOutputLanguage.English, "Thank you", "t1", 80, epoch: 2),
            Origin.AddSeconds(10.1));

        Assert.NotNull(reusedSource);
        Assert.Equal("ありがとう", reusedSource.Value.SourceText);
        Assert.Equal(string.Empty, reusedSource.Value.TranslatedText);
        Assert.NotNull(reusedTranslation);
        Assert.Equal("Thank you", reusedTranslation.Value.TranslatedText);
        Assert.True(reusedTranslation.Value.IsTranslationCurrent);
        Assert.False(reusedTranslation.Value.ShouldFinalize);
    }

    [Fact]
    public void SplitForLanguageSwitchDoesNotFinalizeStalePair()
    {
        // Given: source ????? translation ????????????? assembler
        var assembler = NewAssembler();
        assembler.ExpectLane(RealtimeTranslationOutputLanguage.English);
        assembler.Ingest(Source("?????", "s1", 100), Origin);
        assembler.Ingest(Translation(RealtimeTranslationOutputLanguage.English, "Hello", "t1", 200), Origin);
        assembler.Ingest(Source("????", "s2", 300), Origin.AddMilliseconds(100));

        // When: ???????????????????????????
        var split = assembler.SplitForLanguageSwitch(9, Origin.AddMilliseconds(200));

        // Then: stale pair ???????? suffix ??????
        Assert.Null(split.Finalized);
        Assert.Equal(string.Empty, split.Current.SourceText);
        Assert.Equal(string.Empty, split.Current.TranslatedText);
        Assert.False(split.Current.ShouldFinalize);
    }

    [Fact]
    public void BoundaryCandidatePendingDoesNotBlockIdleFinalizeOfCurrentPair()
    {
        // Given: 完全ペアのあと境界候補が pending の assembler
        var assembler = NewAssembler();
        assembler.ExpectLane(RealtimeTranslationOutputLanguage.English);
        assembler.Ingest(Source("今日は OpenAI", "s1", 100), Origin);
        assembler.Ingest(
            Translation(RealtimeTranslationOutputLanguage.English, "Today it is OpenAI", "t1", 200),
            Origin);
        assembler.SetBoundaryCandidatePending(true);

        // When: idle finalize 間隔を超えて Tick する
        var update = assembler.Tick(Origin.AddSeconds(9));

        // Then: 誤検知候補でも現行の完全ペアは確定する
        Assert.NotNull(update);
        Assert.True(update.Value.ShouldFinalize);
        Assert.Equal("今日は OpenAI", update.Value.SourceText);
        Assert.Equal("Today it is OpenAI", update.Value.TranslatedText);
    }

    [Fact]
    public void SplitForLanguageSwitchFinalizesWhenLateTranslationPassedBoundary()
    {
        // Given: 新言語側の原文が先に伸びてからプレフィックスの訳文が届く
        var assembler = NewAssembler();
        assembler.ExpectLane(RealtimeTranslationOutputLanguage.English);
        assembler.Ingest(Source("こんにちは", "s1", 100), Origin);
        assembler.Ingest(Source(" Hello", "s2", 200), Origin.AddMilliseconds(100));
        assembler.Ingest(
            Translation(RealtimeTranslationOutputLanguage.English, "Hello", "t1", 300),
            Origin.AddMilliseconds(200));

        // When: 確認済み境界で split する
        var split = assembler.SplitForLanguageSwitch(5, Origin.AddMilliseconds(300));

        // Then: 境界を越えて届いた訳文でもプレフィックスを確定する
        Assert.NotNull(split.Finalized);
        Assert.True(split.Finalized.Value.ShouldFinalize);
        Assert.Equal("こんにちは", split.Finalized.Value.SourceText);
        Assert.Equal("Hello", split.Finalized.Value.TranslatedText);
        Assert.Equal(" Hello", split.Current.SourceText);
    }

    [Fact]
    public void SplitForLanguageSwitchClampsAndAlignsSurrogateBoundary()
    {
        // Given: surrogate pair を含む source
        var assembler = NewAssembler();
        assembler.Ingest(Source("A😀B", "s1", 100), Origin);

        // When: surrogate pair の途中と範囲外で split する
        var inside = assembler.SplitForLanguageSwitch(2, Origin);
        var after = assembler.SplitForLanguageSwitch(99, Origin);

        // Then: scalar 境界に下げ、上限を source length に clamp する
        Assert.Equal("😀B", inside.Current.SourceText);
        Assert.Equal(string.Empty, after.Current.SourceText);
    }

    private static RealtimeSubtitleAssembler NewAssembler()
    {
        var assembler = new RealtimeSubtitleAssembler();
        assembler.Reset(1);
        return assembler;
    }

    private static RealtimeTranslationStreamEvent Source(
        string text,
        string eventId,
        int? elapsedMs,
        int epoch = 1) =>
        new(
            RealtimeTranslationLane.Source,
            new RealtimeTranslationServerEvent.InputTranscriptDelta(text, eventId, elapsedMs),
            epoch);

    private static RealtimeTranslationStreamEvent Translation(
        RealtimeTranslationOutputLanguage target,
        string text,
        string eventId,
        int? elapsedMs,
        int epoch = 1) =>
        new(
            RealtimeTranslationLane.Translation(target),
            new RealtimeTranslationServerEvent.OutputTranscriptDelta(text, eventId, elapsedMs),
            epoch);
}
