using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RealtimeTranslator.Core.OpenAI;
using RealtimeTranslator.Core.Realtime;
using Xunit;

namespace RealtimeTranslator.Core.Tests;

/// <summary>
/// 原文接続の runtime ServerError は Events を閉じない。
/// 壊れた JSON が transport 完了する経路、翻訳 unknown-type の経路とは別契約。
/// </summary>
public sealed class RealtimeSourceTranscriptionRuntimeErrorTests
{
    // Given: ready な原文接続
    // When: runtime ServerError のあと input_transcript.delta が続く
    // Then: Events は完了せず、後続 delta を読める
    [Fact]
    public async Task RuntimeServerErrorDoesNotCompleteEventsSoFollowingDeltaIsReadable()
    {
        var transport = new FakeRealtimeServerTransport();
        var connection = new RealtimeSourceTranscriptionConnection(transport, "test-safety");
        await connection.StartAsync("sk-test", RealtimeSessionTuning.Default);

        transport.EnqueueJson(
            """{"type":"error","error":{"message":"rate_limit exceeded","code":"rate_limit_exceeded"}}""");
        var errorEvent = await ReadOneAsync(connection.Events);
        var error = Assert.IsType<RealtimeTranslationServerEvent.ServerError>(errorEvent.Event);
        Assert.Equal(RealtimeSourceTranscriptionCodec.ErrorCode, error.Code);
        Assert.False(connection.Events.Completion.IsCompleted);

        transport.EnqueueJson(
            """{"type":"conversation.item.input_audio_transcription.delta","delta":"still-here","event_id":"e2"}""");
        var deltaEvent = await ReadOneAsync(connection.Events);
        var delta = Assert.IsType<RealtimeTranslationServerEvent.InputTranscriptDelta>(deltaEvent.Event);

        Assert.Equal("still-here", delta.Delta);
        Assert.False(connection.Events.Completion.IsCompleted);
        await connection.ForceCloseAsync();
    }

    // Given: Dual 経由で原文 runtime ServerError が merge される
    // When: そのあと原文 delta が届く
    // Then: Dual Events は完了せず、後続の原文 delta を読める
    [Fact]
    public async Task DualMergeKeepsReadingSourceDeltasAfterSourceRuntimeServerError()
    {
        var source = new FakeRealtimeServerTransport();
        var english = new FakeRealtimeServerTransport();
        var japanese = new FakeRealtimeServerTransport();
        using var dual = new DualRealtimeTranslationClient(
            new RealtimeSourceTranscriptionConnection(source, "test-safety"),
            new RealtimeTranslationConnection(
                RealtimeTranslationOutputLanguage.English,
                english,
                "test-safety"),
            new RealtimeTranslationConnection(
                RealtimeTranslationOutputLanguage.Japanese,
                japanese,
                "test-safety"));

        await dual.StartAsync("sk-test", RealtimeSessionTuning.Default);

        source.EnqueueJson(
            """{"type":"error","error":{"message":"rate_limit exceeded","code":"rate_limit_exceeded"}}""");
        var errorEvent = await ReadUntilAsync(
            dual,
            streamEvent => streamEvent.Event is RealtimeTranslationServerEvent.ServerError);
        var error = Assert.IsType<RealtimeTranslationServerEvent.ServerError>(errorEvent.Event);
        Assert.Equal(RealtimeSourceTranscriptionCodec.ErrorCode, error.Code);
        Assert.True(errorEvent.Lane.IsSource);
        Assert.False(dual.Events.Completion.IsCompleted);

        source.EnqueueJson(
            """{"type":"conversation.item.input_audio_transcription.delta","delta":"after-error","event_id":"e3"}""");
        var deltaEvent = await ReadUntilAsync(
            dual,
            streamEvent => streamEvent.Event is RealtimeTranslationServerEvent.InputTranscriptDelta);
        var delta = Assert.IsType<RealtimeTranslationServerEvent.InputTranscriptDelta>(deltaEvent.Event);

        Assert.Equal("after-error", delta.Delta);
        Assert.True(deltaEvent.Lane.IsSource);
        Assert.False(dual.Events.Completion.IsCompleted);
        await dual.ForceCloseAsync();
    }

    private static async Task<RealtimeTranslationStreamEvent> ReadOneAsync(
        System.Threading.Channels.ChannelReader<RealtimeTranslationStreamEvent> reader)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        return await reader.ReadAsync(timeout.Token);
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
}
