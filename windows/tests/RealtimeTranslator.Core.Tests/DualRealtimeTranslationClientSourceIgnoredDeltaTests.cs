using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RealtimeTranslator.Core.Localization;
using RealtimeTranslator.Core.OpenAI;
using RealtimeTranslator.Core.Realtime;
using Xunit;

namespace RealtimeTranslator.Core.Tests;

/// <summary>
/// 原文接続が Ignored / 本文なし error を受けても Dual Events を完了させない契約。
/// 空 delta でストリームが切れると発話の残りが落ち、再接続に見える。
/// </summary>
public sealed class DualRealtimeTranslationClientSourceIgnoredDeltaTests
{
    // Given: ready な Dual
    // When: 空の原文 delta の直後に本文付き delta が届く
    // Then: 空は Events に出さず、後続 delta が読める（ストリームは完了しない）
    [Fact]
    public async Task EmptySourceDeltaIsDroppedAndDoesNotCompleteEvents()
    {
        var source = new FakeRealtimeServerTransport();
        var english = new FakeRealtimeServerTransport();
        var japanese = new FakeRealtimeServerTransport();
        using var dual = CreateDual(source, english, japanese);

        await dual.StartAsync("sk-test", RealtimeSessionTuning.Default);

        source.EnqueueJson(
            """{"type":"conversation.item.input_audio_transcription.delta","delta":"","event_id":"empty-1"}""");
        source.EnqueueJson(
            """{"type":"conversation.item.input_audio_transcription.delta","item_id":"i1","delta":"alive","event_id":"alive-1"}""");

        Assert.Equal(["alive"], await CollectSourceDeltasAsync(dual, 1));

        source.EnqueueJson(
            """{"type":"conversation.item.input_audio_transcription.delta","item_id":"i1","delta":"still","event_id":"alive-2"}""");

        Assert.Equal(["still"], await CollectSourceDeltasAsync(dual, 1));
        await dual.ForceCloseAsync();
    }

    // Given: ready な Dual
    // When: 翻訳接続の type 名を原文ソケットへ流したあと、正規の原文 delta が届く
    // Then: 翻訳形は破棄し、原文 delta だけが Events に出る
    [Fact]
    public async Task TranslationShapedSourceEventIsDroppedThenRealDeltaArrives()
    {
        var source = new FakeRealtimeServerTransport();
        var english = new FakeRealtimeServerTransport();
        var japanese = new FakeRealtimeServerTransport();
        using var dual = CreateDual(source, english, japanese);

        await dual.StartAsync("sk-test", RealtimeSessionTuning.Default);

        source.EnqueueJson("""{"type":"session.input_transcript.delta","delta":"pollute"}""");
        source.EnqueueJson(
            """{"type":"conversation.item.input_audio_transcription.delta","item_id":"i1","delta":"kept","event_id":"kept-1"}""");

        Assert.Equal(["kept"], await CollectSourceDeltasAsync(dual, 1));
        await dual.ForceCloseAsync();
    }

    // Given: ready な Dual
    // When: error 本文が無い runtime error のあと原文 delta が届く
    // Then: 原文汎用 ServerError を 1 件出し、後続 delta は読める
    [Fact]
    public async Task SourceErrorWithoutBodyDoesNotCompleteEvents()
    {
        var source = new FakeRealtimeServerTransport();
        var english = new FakeRealtimeServerTransport();
        var japanese = new FakeRealtimeServerTransport();
        using var dual = CreateDual(source, english, japanese);

        await dual.StartAsync("sk-test", RealtimeSessionTuning.Default);

        source.EnqueueJson("""{"type":"error"}""");

        RealtimeTranslationServerEvent.ServerError? error = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (error is null)
        {
            var streamEvent = await dual.Events.ReadAsync(timeout.Token);
            error = streamEvent.Event as RealtimeTranslationServerEvent.ServerError;
        }

        Assert.Equal(RealtimeSourceTranscriptionCodec.ErrorCode, error.Code);
        Assert.Equal(UserCopy.Current.Text("error.sourceSessionGeneric"), error.Message);

        source.EnqueueJson(
            """{"type":"conversation.item.input_audio_transcription.delta","item_id":"i1","delta":"after-error","event_id":"after-1"}""");

        Assert.Equal(["after-error"], await CollectSourceDeltasAsync(dual, 1));
        await dual.ForceCloseAsync();
    }

    private static DualRealtimeTranslationClient CreateDual(
        FakeRealtimeServerTransport source,
        FakeRealtimeServerTransport english,
        FakeRealtimeServerTransport japanese) =>
        new(
            new RealtimeSourceTranscriptionConnection(source, "test-safety"),
            new RealtimeTranslationConnection(
                RealtimeTranslationOutputLanguage.English,
                english,
                "test-safety"),
            new RealtimeTranslationConnection(
                RealtimeTranslationOutputLanguage.Japanese,
                japanese,
                "test-safety"));

    private static async Task<List<string>> CollectSourceDeltasAsync(
        DualRealtimeTranslationClient dual,
        int count)
    {
        var deltas = new List<string>(count);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (deltas.Count < count)
        {
            var streamEvent = await dual.Events.ReadAsync(timeout.Token);
            if (streamEvent.Event is RealtimeTranslationServerEvent.InputTranscriptDelta delta)
            {
                deltas.Add(delta.Delta);
            }
        }

        return deltas;
    }
}
