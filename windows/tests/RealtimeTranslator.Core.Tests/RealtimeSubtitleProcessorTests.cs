using System;
using RealtimeTranslator.Core.Audio;
using RealtimeTranslator.Core.OpenAI;
using RealtimeTranslator.Core.Realtime;
using Xunit;

namespace RealtimeTranslator.Core.Tests;

public sealed class RealtimeSubtitleProcessorTests
{
    private static readonly DateTimeOffset Origin = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // Given: ja-en の epoch 1 を開始した processor
    // When: 判定前の日本語原文 delta を取り込む
    // Then: 英語 target が選択され、原文 update が返る
    [Fact]
    public void JapaneseSourceSelectsEnglishTarget()
    {
        var processor = NewProcessor();

        var result = processor.Process(Source("今日は晴れです。", "s1", 1), Origin);

        Assert.NotNull(result);
        Assert.Equal(
            new RealtimeSubtitleRoutingAction.Select(RealtimeTranslationOutputLanguage.English),
            result.RoutingAction);
        Assert.True(result.IsSourceUpdate);
        Assert.Single(result.Updates);
        Assert.Equal(result.IngestedUpdate, result.Updates[0]);
        Assert.Equal("今日は晴れです。", result.Updates[0].SourceText);
        Assert.False(result.Updates[0].ShouldFinalize);
        Assert.Equal("今日は晴れです。", processor.RoutingSourceText);
    }

    // Given: 日本語原文で英語 target が選択済み
    // When: 英語の訳文 delta を取り込む
    // Then: routing は変わらず訳文 update だけが返る
    [Fact]
    public void TranslationDeltaDoesNotChangeRouting()
    {
        var processor = NewProcessor();
        processor.Process(Source("今日は晴れです。", "s1", 1), Origin);

        var result = processor.Process(
            Translation(RealtimeTranslationOutputLanguage.English, "It is sunny today.", "t1", 2),
            Origin.AddMilliseconds(2));

        Assert.NotNull(result);
        Assert.Equal(new RealtimeSubtitleRoutingAction.None(), result.RoutingAction);
        Assert.False(result.IsSourceUpdate);
        Assert.Single(result.Updates);
        Assert.Equal("It is sunny today.", result.Updates[0].TranslatedText);
        Assert.True(result.Updates[0].IsTranslationCurrent);
        Assert.False(result.Updates[0].ShouldFinalize);
    }

    // Given: 日本語原文と英訳で英語 target が選択済み
    // When: 原文が英語へ切り替わる delta を取り込む
    // Then: 確定プレフィックスと現行サフィックスを返し target を日本語へ切り替える
    [Fact]
    public void LanguageSwitchFinalizesPrefixAndKeepsCurrentSuffix()
    {
        var processor = NewProcessor();
        processor.Process(Source("今日は晴れです。", "s1", 1), Origin);
        processor.Process(
            Translation(RealtimeTranslationOutputLanguage.English, "It is sunny today.", "t1", 2),
            Origin.AddMilliseconds(2));

        // 直近16 scalar 窓に日本語が残るため、この時点では切り替わらない
        var partial = processor.Process(Source("To", "s2", 3), Origin.AddMilliseconds(3));
        Assert.NotNull(partial);
        Assert.Equal(new RealtimeSubtitleRoutingAction.None(), partial.RoutingAction);

        var result = processor.Process(
            Source("day it is sunny outside", "s3", 4),
            Origin.AddMilliseconds(4));

        Assert.NotNull(result);
        Assert.Equal(
            new RealtimeSubtitleRoutingAction.Switch(RealtimeTranslationOutputLanguage.Japanese),
            result.RoutingAction);
        Assert.Equal(2, result.Updates.Count);
        Assert.True(result.Updates[0].ShouldFinalize);
        Assert.Equal("今日は晴れです。", result.Updates[0].SourceText);
        Assert.Equal("It is sunny today.", result.Updates[0].TranslatedText);
        Assert.False(result.Updates[1].ShouldFinalize);
        Assert.Equal("Today it is sunny outside", result.Updates[1].SourceText);
        // 切替後は RoutingSourceTextWindow が末尾16非空白 scalar に切り詰める
        Assert.Equal("it is sunny outside", processor.RoutingSourceText);
    }

    // Given: 日本語原文と stale な英訳で英語 target が選択済み
    // When: ゲート未満の英語 delta を取り込んで idle する
    // Then: 境界候補が pending のまま原文を保持し、後続 delta で候補位置から切り替える
    [Fact]
    public void JapaneseBoundaryCandidateRemainsPendingUntilLatinGate()
    {
        var processor = NewProcessor();
        processor.Process(Source("今日は晴れです。", "s1", 1), Origin);
        processor.Process(
            Translation(RealtimeTranslationOutputLanguage.English, "It is sunny today.", "t1", 2),
            Origin.AddMilliseconds(2));

        var partial = processor.Process(
            Source("Today it is", "s2", 3),
            Origin.AddMilliseconds(3));
        Assert.NotNull(partial);
        Assert.Equal(new RealtimeSubtitleRoutingAction.None(), partial.RoutingAction);
        Assert.Null(processor.Tick(Origin.AddSeconds(9)));
        Assert.Equal("今日は晴れです。Today it is".Length, processor.CurrentSourceLength);

        var result = processor.Process(
            Source(" sunny outside", "s3", 4),
            Origin.AddSeconds(9).AddMilliseconds(4));

        Assert.NotNull(result);
        Assert.Equal(
            new RealtimeSubtitleRoutingAction.Switch(RealtimeTranslationOutputLanguage.Japanese),
            result.RoutingAction);
        Assert.Equal("今日は晴れです。", result.Updates[0].SourceText);
        Assert.True(result.Updates[0].ShouldFinalize);
        Assert.Equal("Today it is sunny outside", result.Updates[1].SourceText);
    }

    // Given: 日本語原文で英語 target が選択済み
    // When: DiscardUnconfirmed を呼ぶ
    // Then: 無効化 update が返り routing がリセットされて再選択できる
    [Fact]
    public void DiscardUnconfirmedInvalidatesAndResetsRouting()
    {
        var processor = NewProcessor();
        processor.Process(Source("今日は晴れです。", "s1", 1), Origin);

        var invalidation = processor.DiscardUnconfirmed();

        Assert.True(invalidation.IsInvalidation);
        Assert.Equal(string.Empty, invalidation.SourceText);
        Assert.Equal(string.Empty, invalidation.TranslatedText);
        Assert.False(invalidation.ShouldFinalize);
        Assert.Equal(string.Empty, processor.RoutingSourceText);

        // selected target がリセットされたので次の日本語原文で再選択される
        var result = processor.Process(
            Source("こんにちは", "s4", 5),
            Origin.AddMilliseconds(5));
        Assert.NotNull(result);
        Assert.Equal(
            new RealtimeSubtitleRoutingAction.Select(RealtimeTranslationOutputLanguage.English),
            result.RoutingAction);
    }

    // Given: 日本語原文「今日は晴れです。」で英語 target が選択済み
    // When: 音声欠落後、新しい原文なしで日本語 lane、続いて英語 lane の訳文 delta を取り込む
    // Then: stale な expected lane を使わず first-output で日本語 lane を選び、その後も日本語訳を保持する
    [Fact]
    public void AudioLossClearsExpectedLaneBeforeFirstTranslationOutput()
    {
        var processor = NewProcessor();
        processor.Process(Source("今日は晴れです。", "s1", 1), Origin);
        processor.MarkAudioLoss(Origin);

        var japanese = processor.Process(
            Translation(RealtimeTranslationOutputLanguage.Japanese, "こんにちは", "t-loss-1", 10),
            Origin.AddMilliseconds(10));
        var english = processor.Process(
            Translation(RealtimeTranslationOutputLanguage.English, "Hello", "t-loss-2", 11),
            Origin.AddMilliseconds(11));

        Assert.NotNull(japanese);
        Assert.Equal("こんにちは", japanese.Updates[0].TranslatedText);
        Assert.True(japanese.Updates[0].IsTranslationCurrent);
        Assert.NotNull(english);
        Assert.Equal("こんにちは", english.Updates[0].TranslatedText);
    }

    // Given: epoch 1 を開始した直後
    // When: 旧 epoch の原文 delta を取り込む
    // Then: イベントは無視され state は変わらない
    [Fact]
    public void StaleEpochEventIsIgnored()
    {
        var processor = NewProcessor();

        var result = processor.Process(
            new RealtimeTranslationStreamEvent(
                RealtimeTranslationLane.Source,
                new RealtimeTranslationServerEvent.InputTranscriptDelta("こんにちは", "s0", 1),
                0),
            Origin);

        Assert.Null(result);
        Assert.Equal(string.Empty, processor.RoutingSourceText);
        Assert.Equal(0, processor.CurrentSourceLength);
    }

    // Given: 日本語原文で英語 target が選択済み
    // When: ResetRoutingForNextSegment 後に次セグメントの原文を取り込む
    // Then: target が再選択される
    [Fact]
    public void ResetRoutingForNextSegmentAllowsReselection()
    {
        var processor = NewProcessor();
        processor.Process(Source("今日は晴れです。", "s1", 1), Origin);

        processor.ResetRoutingForNextSegment();
        var result = processor.Process(
            Source("こんにちは", "s5", 6),
            Origin.AddMilliseconds(6));

        Assert.NotNull(result);
        Assert.Equal(
            new RealtimeSubtitleRoutingAction.Select(RealtimeTranslationOutputLanguage.English),
            result.RoutingAction);
        Assert.Equal("こんにちは", processor.RoutingSourceText);
    }

    // Given: 文字種の反転を起こさない英語 delta が連続で流れ続ける
    // When: 同一セグメント内で delta を大量に取り込んだあと日本語へ反転する
    // Then: routing 判定バッファは上限までで打ち切られ、その後の反転検出も壊れない
    [Fact]
    public void NonFlippingSourceDeltaStreamDoesNotGrowRoutingBufferWithoutBound()
    {
        var processor = NewProcessor();

        var first = processor.Process(Source("we keep talking in english ", "s0", 1), Origin);
        Assert.NotNull(first);
        Assert.Equal(
            new RealtimeSubtitleRoutingAction.Select(RealtimeTranslationOutputLanguage.Japanese),
            first.RoutingAction);

        const int nonFlippingDeltaCount = 200;
        for (var i = 0; i < nonFlippingDeltaCount; i += 1)
        {
            processor.Process(
                Source("and we never flip the script ", $"s{i + 1}", i + 2),
                Origin);
        }

        Assert.True(
            processor.RoutingSourceText.Length <= RoutingSourceTextWindow.MaxLength,
            $"routing buffer length {processor.RoutingSourceText.Length} exceeded the cap before flip");

        var result = processor.Process(Source("ここで日本語へ反転します", "flip", 999), Origin);

        Assert.NotNull(result);
        Assert.Equal(
            new RealtimeSubtitleRoutingAction.Switch(RealtimeTranslationOutputLanguage.English),
            result.RoutingAction);
        Assert.True(
            processor.RoutingSourceText.Length <= RoutingSourceTextWindow.MaxLength,
            $"routing buffer length {processor.RoutingSourceText.Length} exceeded the cap after flip");
    }

    // Given: 長い英語原文で target が確定したあとに日本語へ反転する
    // When: 切替を起こした delta を取り込む
    // Then: routing バッファは反転 delta だけになり、切替前の英語尾を残さない
    [Fact]
    public void LanguageFlipResetsRoutingBufferToTheFlipDelta()
    {
        var processor = NewProcessor();
        const string flipDelta = "ここで日本語へ反転します";

        processor.Process(Source("we keep talking in english ", "s1", 1), Origin);
        processor.Process(Source("and we never flip the script ", "s2", 2), Origin);

        Assert.Contains("script", processor.RoutingSourceText, StringComparison.Ordinal);
        Assert.True(
            processor.RoutingSourceText.Length > flipDelta.Length,
            "pre-flip routing buffer should still hold the English tail");

        var result = processor.Process(Source(flipDelta, "s3", 3), Origin);

        Assert.NotNull(result);
        Assert.Equal(
            new RealtimeSubtitleRoutingAction.Switch(RealtimeTranslationOutputLanguage.English),
            result.RoutingAction);
        Assert.Equal(
            " " + RoutingSourceTextWindow.Trim(flipDelta, LanguagePair.JaEn),
            processor.RoutingSourceText);
        Assert.DoesNotContain("script", processor.RoutingSourceText, StringComparison.Ordinal);
        Assert.DoesNotContain("english", processor.RoutingSourceText, StringComparison.Ordinal);
    }

    // Given: en-es で英語 target 確定後、上限を超える空白なしトークン
    // When: 長い1語の source delta を取り込む
    // Then: routing バッファは上限以内に収まる
    [Fact]
    public void EnEsLongWhitespaceFreeTokenDoesNotGrowRoutingBufferPastMaxLength()
    {
        var processor = new RealtimeSubtitleProcessor();
        processor.BeginEpoch(1, LanguagePair.EnEs);

        var selected = processor.Process(Source("the and is are of to it that", "s1", 1), Origin);
        Assert.NotNull(selected);
        Assert.Equal(
            new RealtimeSubtitleRoutingAction.Select(RealtimeTranslationOutputLanguage.Spanish),
            selected.RoutingAction);

        processor.Process(
            Source(new string('x', RoutingSourceTextWindow.MaxLength + 32), "s2", 2),
            Origin);

        Assert.True(
            processor.RoutingSourceText.Length <= RoutingSourceTextWindow.MaxLength,
            $"routing buffer length {processor.RoutingSourceText.Length} exceeded the cap");
    }

    // Given: 日本語原文と英訳のあと、ゲート未達の 3 語製品名が続く
    // When: idle finalize 間隔を超えて Tick する
    // Then: 切替は起きず、pending のまま stale セグメントを abandon しない
    [Fact]
    public void GatedJapaneseProductNameKeepsPendingAndDoesNotAbandon()
    {
        var processor = NewProcessor();
        processor.Process(Source("今日は晴れです。", "s1", 1), Origin);
        processor.Process(
            Translation(RealtimeTranslationOutputLanguage.English, "It is sunny today.", "t1", 2),
            Origin.AddMilliseconds(2));

        var gated = processor.Process(
            Source(" Google Cloud Platform", "s2", 3),
            Origin.AddMilliseconds(3));
        Assert.NotNull(gated);
        Assert.Equal(new RealtimeSubtitleRoutingAction.None(), gated.RoutingAction);
        Assert.False(gated.Updates[0].IsTranslationCurrent);

        var generation = processor.SegmentGeneration;
        var sourceLength = processor.CurrentSourceLength;

        var tick = processor.Tick(Origin.AddSeconds(9));

        Assert.Null(tick);
        Assert.Equal(generation, processor.SegmentGeneration);
        Assert.Equal(sourceLength, processor.CurrentSourceLength);
    }

    // Given: 日本語セグメントのあと、長い空白 run で隔てられた複数語の英語 delta
    // When: UTF-16 文字数キャップだけだと末尾 1 語しか残らない入力を取り込む
    // Then: RecentEvidence ウィンドウを保ち英語反転し、バッファは上限以内に収まる
    [Fact]
    public void WideWhitespaceBetweenLatinWordsStillFlipsJapaneseToEnglish()
    {
        var processor = NewProcessor();

        var first = processor.Process(Source("これはテストです", "s1", 1), Origin);
        Assert.NotNull(first);
        Assert.Equal(
            new RealtimeSubtitleRoutingAction.Select(RealtimeTranslationOutputLanguage.English),
            first.RoutingAction);

        var gap = new string(' ', RoutingSourceTextWindow.MaxLength + 32);
        var result = processor.Process(
            Source("aa bb cc dd ee ff gg" + gap + " hh", "s2", 2),
            Origin);

        Assert.NotNull(result);
        Assert.Equal(
            new RealtimeSubtitleRoutingAction.Switch(RealtimeTranslationOutputLanguage.Japanese),
            result.RoutingAction);
        Assert.True(
            processor.RoutingSourceText.Length <= RoutingSourceTextWindow.MaxLength,
            $"routing buffer length {processor.RoutingSourceText.Length} exceeded the cap");
    }

    private static RealtimeSubtitleProcessor NewProcessor()
    {
        var processor = new RealtimeSubtitleProcessor();
        processor.BeginEpoch(1, LanguagePair.JaEn);
        return processor;
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
