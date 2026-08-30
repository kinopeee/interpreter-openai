using System;
using System.Threading;
using System.Threading.Tasks;
using RealtimeTranslator.Core.Audio;
using RealtimeTranslator.Core.OpenAI;
using RealtimeTranslator.Core.Realtime;
using Xunit;

namespace RealtimeTranslator.Core.Tests;

/// <summary>
/// Dual merge の寿命。1 本の完了イベントや runtime error で Dual Events を閉じると
/// 残レーンの字幕が落ち、session は eventStreamStopped として再接続してしまう。
/// #111 の原文 runtime ServerError とは交差しない。
/// </summary>
public sealed class DualRealtimeTranslationClientMergeLifetimeTests
{
    // Given: ja-en で開始済みの Dual
    // When: 原文が transcription.completed を返したあと、さらに原文 delta が届く
    // Then: Dual Events は完了せず、後続の原文 delta を読める（completed は close drain 用で切断ではない）
    [Fact]
    public async Task SourceCompletedDoesNotCompleteDualEventsSoFollowingDeltaIsReadable()
    {
        await using var harness = await MergeHarness.StartAsync();

        harness.Source.EnqueueJson(
            """{"type":"conversation.item.input_audio_transcription.completed"}""");
        harness.Source.EnqueueJson(
            """{"type":"conversation.item.input_audio_transcription.delta","delta":"after-completed","event_id":"e-after-completed"}""");
        var deltaEvent = await ReadUntilAsync(
            harness.Dual,
            streamEvent => streamEvent.Event is RealtimeTranslationServerEvent.InputTranscriptDelta);
        var delta = Assert.IsType<RealtimeTranslationServerEvent.InputTranscriptDelta>(deltaEvent.Event);

        Assert.Equal("after-completed", delta.Delta);
        Assert.True(deltaEvent.Lane.IsSource);
        Assert.False(harness.Dual.Events.Completion.IsCompleted);
    }

    // Given: ja-en で開始済みの Dual
    // When: 英語翻訳レーンが runtime ServerError を返したあと、原文 delta が届く
    // Then: Dual Events は完了せず、原文 delta を読める（翻訳レーンの error で multiplexer を閉じない）
    [Fact]
    public async Task TranslationLaneRuntimeServerErrorDoesNotCompleteDualEventsSoSourceDeltaIsReadable()
    {
        await using var harness = await MergeHarness.StartAsync();

        harness.English.EnqueueJson(
            """{"type":"error","error":{"message":"rate_limit exceeded","code":"rate_limit_exceeded"}}""");
        var errorEvent = await ReadUntilAsync(
            harness.Dual,
            streamEvent => streamEvent.Event is RealtimeTranslationServerEvent.ServerError);
        var error = Assert.IsType<RealtimeTranslationServerEvent.ServerError>(errorEvent.Event);
        Assert.Equal("rate_limit_exceeded", error.Code);
        Assert.Equal(RealtimeTranslationOutputLanguage.English, errorEvent.Target);
        Assert.False(harness.Dual.Events.Completion.IsCompleted);

        harness.English.EnqueueJson(
            """{"type":"session.output_transcript.delta","delta":"kept after error","event_id":"out-after-error"}""");
        var translationEvent = await ReadUntilAsync(
            harness.Dual,
            streamEvent => streamEvent.Event is RealtimeTranslationServerEvent.OutputTranscriptDelta);
        var translation = Assert.IsType<RealtimeTranslationServerEvent.OutputTranscriptDelta>(
            translationEvent.Event);
        Assert.Equal("kept after error", translation.Delta);
        Assert.Equal(RealtimeTranslationOutputLanguage.English, translationEvent.Target);
        Assert.False(harness.Dual.Events.Completion.IsCompleted);

        harness.Source.EnqueueJson(
            """{"type":"conversation.item.input_audio_transcription.delta","delta":"after-translation-error","event_id":"e-src"}""");
        var deltaEvent = await ReadUntilAsync(
            harness.Dual,
            streamEvent => streamEvent.Event is RealtimeTranslationServerEvent.InputTranscriptDelta);
        var delta = Assert.IsType<RealtimeTranslationServerEvent.InputTranscriptDelta>(deltaEvent.Event);

        Assert.Equal("after-translation-error", delta.Delta);
        Assert.True(deltaEvent.Lane.IsSource);
        Assert.False(harness.Dual.Events.Completion.IsCompleted);
    }

    // Given: ja-en で開始済みの Dual
    // When: 英語翻訳レーンが session.closed を返したあと、原文 delta が届く
    // Then: Dual Events は完了せず、原文 delta を読める（1 本の closed で Dual 全体を閉じない）
    [Fact]
    public async Task TranslationLaneSessionClosedDoesNotCompleteDualEventsSoSourceDeltaIsReadable()
    {
        await using var harness = await MergeHarness.StartAsync();

        harness.English.EnqueueJson("""{"type":"session.closed"}""");
        var closedEvent = await ReadUntilAsync(
            harness.Dual,
            streamEvent => streamEvent.Event is RealtimeTranslationServerEvent.SessionClosed);
        Assert.Equal(RealtimeTranslationOutputLanguage.English, closedEvent.Target);
        Assert.False(harness.Dual.Events.Completion.IsCompleted);

        harness.Source.EnqueueJson(
            """{"type":"conversation.item.input_audio_transcription.delta","delta":"after-english-closed","event_id":"e-src-2"}""");
        var deltaEvent = await ReadUntilAsync(
            harness.Dual,
            streamEvent => streamEvent.Event is RealtimeTranslationServerEvent.InputTranscriptDelta);
        var delta = Assert.IsType<RealtimeTranslationServerEvent.InputTranscriptDelta>(deltaEvent.Event);

        Assert.Equal("after-english-closed", delta.Delta);
        Assert.True(deltaEvent.Lane.IsSource);
        Assert.False(harness.Dual.Events.Completion.IsCompleted);
    }

    private static async Task<RealtimeTranslationStreamEvent> ReadUntilAsync(
        DualRealtimeTranslationClient dual,
        Func<RealtimeTranslationStreamEvent, bool> match)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var streamEvent = await dual.Events.ReadAsync(timeout.Token);
            if (match(streamEvent))
            {
                return streamEvent;
            }
        }
    }

    private sealed class MergeHarness : IAsyncDisposable
    {
        private MergeHarness(
            FakeRealtimeServerTransport source,
            FakeRealtimeServerTransport english,
            FakeRealtimeServerTransport japanese,
            DualRealtimeTranslationClient dual)
        {
            Source = source;
            English = english;
            Japanese = japanese;
            Dual = dual;
        }

        public FakeRealtimeServerTransport Source { get; }

        public FakeRealtimeServerTransport English { get; }

        public FakeRealtimeServerTransport Japanese { get; }

        public DualRealtimeTranslationClient Dual { get; }

        public static async Task<MergeHarness> StartAsync()
        {
            var source = new FakeRealtimeServerTransport();
            var english = new FakeRealtimeServerTransport();
            var japanese = new FakeRealtimeServerTransport();
            var dual = new DualRealtimeTranslationClient(
                new RealtimeSourceTranscriptionConnection(source, "test-safety"),
                new RealtimeTranslationConnection(
                    RealtimeTranslationOutputLanguage.English,
                    english,
                    "test-safety"),
                new RealtimeTranslationConnection(
                    RealtimeTranslationOutputLanguage.Japanese,
                    japanese,
                    "test-safety"));

            await dual.StartAsync("sk-test", RealtimeSessionTuning.Default, LanguagePair.JaEn);
            while (dual.Events.TryRead(out _))
            {
            }

            return new MergeHarness(source, english, japanese, dual);
        }

        public async ValueTask DisposeAsync() => await Dual.ForceCloseAsync();
    }
}
