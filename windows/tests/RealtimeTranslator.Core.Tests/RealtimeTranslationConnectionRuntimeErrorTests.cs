using System;
using System.Threading;
using System.Threading.Tasks;
using RealtimeTranslator.Core.OpenAI;
using RealtimeTranslator.Core.Realtime;
using Xunit;

namespace RealtimeTranslator.Core.Tests;

/// <summary>
/// 翻訳接続の runtime error / 後続訳文。handshake 失敗や broken JSON の transport 完了とは交差しない。
/// 原文側の同契約は #111 の RealtimeSourceTranscriptionRuntimeErrorTests。
/// </summary>
public sealed class RealtimeTranslationConnectionRuntimeErrorTests
{
    // Given: handshake 済みの翻訳接続
    // When: runtime の rate_limit error のあと output_transcript.delta が届く
    // Then: Events は完了せず、後続の訳文 delta を読める（error を切断扱いにしない）
    [Fact]
    public async Task RuntimeServerErrorDoesNotCompleteEventsSoFollowingTranscriptIsReadable()
    {
        var transport = new FakeRealtimeServerTransport();
        var connection = new RealtimeTranslationConnection(
            RealtimeTranslationOutputLanguage.English,
            transport,
            "test-safety");
        await connection.StartAsync(
            "sk-test",
            RealtimeTranslationSessionConfig.EnglishTargetWithoutSourceTranscription());

        transport.EnqueueJson(
            """{"type":"error","error":{"message":"rate_limit exceeded","code":"rate_limit_exceeded"}}""");
        var errorEvent = await ReadOneAsync(connection.Events);
        var error = Assert.IsType<RealtimeTranslationServerEvent.ServerError>(errorEvent.Event);
        Assert.Equal("rate_limit_exceeded", error.Code);
        Assert.Equal(RealtimeTranslationOutputLanguage.English, errorEvent.Target);
        Assert.False(connection.Events.Completion.IsCompleted);

        transport.EnqueueJson(
            """{"type":"session.output_transcript.delta","delta":"kept after error","event_id":"out-2"}""");
        var deltaEvent = await ReadOneAsync(connection.Events);
        var delta = Assert.IsType<RealtimeTranslationServerEvent.OutputTranscriptDelta>(deltaEvent.Event);

        Assert.Equal("kept after error", delta.Delta);
        Assert.False(connection.Events.Completion.IsCompleted);
        await connection.ForceCloseAsync();
    }

    // Given: handshake 済みの翻訳接続
    // When: runtime の Unknown イベントのあと output_transcript.delta が届く
    // Then: Unknown で Events を閉じず、後続の訳文を読める
    [Fact]
    public async Task UnknownRuntimeEventDoesNotCompleteEventsSoFollowingTranscriptIsReadable()
    {
        var transport = new FakeRealtimeServerTransport();
        var connection = new RealtimeTranslationConnection(
            RealtimeTranslationOutputLanguage.Japanese,
            transport,
            "test-safety");
        await connection.StartAsync(
            "sk-test",
            RealtimeTranslationSessionConfig.JapaneseTargetWithoutSourceTranscription());

        transport.EnqueueJson("""{"type":"session.foo_bar"}""");
        var unknownEvent = await ReadOneAsync(connection.Events);
        var unknown = Assert.IsType<RealtimeTranslationServerEvent.Unknown>(unknownEvent.Event);
        Assert.Equal("session.foo_bar", unknown.Type);
        Assert.False(connection.Events.Completion.IsCompleted);

        transport.EnqueueJson(
            """{"type":"session.output_transcript.delta","delta":"kept after unknown","event_id":"out-3"}""");
        var deltaEvent = await ReadOneAsync(connection.Events);
        var delta = Assert.IsType<RealtimeTranslationServerEvent.OutputTranscriptDelta>(deltaEvent.Event);

        Assert.Equal("kept after unknown", delta.Delta);
        Assert.False(connection.Events.Completion.IsCompleted);
        await connection.ForceCloseAsync();
    }

    private static async Task<RealtimeTranslationStreamEvent> ReadOneAsync(
        System.Threading.Channels.ChannelReader<RealtimeTranslationStreamEvent> reader)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        return await reader.ReadAsync(timeout.Token);
    }
}
