using System;
using System.Text;
using System.Threading.Tasks;
using RealtimeTranslator.Core.OpenAI;
using RealtimeTranslator.Core.Realtime;
using Xunit;

namespace RealtimeTranslator.Core.Tests;

/// <summary>
/// 接続 receive loop の epoch ゲート。開いている coverage PR の
/// RealtimeConnectionTests とはファイルを分けて衝突を避ける。
/// </summary>
public sealed class RealtimeConnectionReceiveEpochTests
{
    // Given: ready な翻訳接続の receive が訳文 delta を読んだ直後
    // When: Dispose が epoch を進める
    // Then: 旧 epoch の訳文は Events に出ず、閉じた接続へは Append できない
    [Fact]
    public async Task TranslationDisposeDuringTranscriptReceiveDropsDelta()
    {
        var transport = new FakeRealtimeServerTransport();
        var connection = new RealtimeTranslationConnection(
            RealtimeTranslationOutputLanguage.English,
            transport,
            "test-safety");
        await connection.StartAsync(
            "sk-test",
            RealtimeTranslationSessionConfig.EnglishTargetWithoutSourceTranscription());

        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.AfterInboundRead = () =>
        {
            connection.Dispose();
            disposed.TrySetResult();
        };
        transport.EnqueueJson(
            """{"type":"session.output_transcript.delta","delta":"stale translation","event_id":"stale-1"}""");

        await disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await AssertNoTranscriptAsync(connection);
        var appendError = await Assert.ThrowsAsync<RealtimeTranslationException>(
            () => connection.AppendAudioFrameAsync(Encoding.UTF8.GetBytes("frame")));
        Assert.Equal(RealtimeTranslationErrorKind.NotConnected, appendError.Kind);
    }

    // Given: ready な原文接続の receive が delta を読んだ直後
    // When: Dispose が epoch を進める
    // Then: 旧 epoch の原文は Events に出ず、tuning / Append は NotConnected
    [Fact]
    public async Task SourceDisposeDuringDeltaReceiveDropsDelta()
    {
        var transport = new FakeRealtimeServerTransport();
        var connection = new RealtimeSourceTranscriptionConnection(transport, "test-safety");
        await connection.StartAsync("sk-test", RealtimeSessionTuning.Default);

        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.AfterInboundRead = () =>
        {
            connection.Dispose();
            disposed.TrySetResult();
        };
        transport.EnqueueJson(
            """{"type":"conversation.item.input_audio_transcription.delta","item_id":"i1","delta":"stale source","event_id":"stale-src-1"}""");

        await disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await AssertNoSourceDeltaAsync(connection);
        var appendError = await Assert.ThrowsAsync<RealtimeTranslationException>(
            () => connection.AppendAudioFrameAsync(Encoding.UTF8.GetBytes("frame")));
        var tuningError = await Assert.ThrowsAsync<RealtimeTranslationException>(
            () => connection.UpdateTuningAsync(RealtimeSessionTuning.Default));
        Assert.Equal(RealtimeTranslationErrorKind.NotConnected, appendError.Kind);
        Assert.Equal(RealtimeTranslationErrorKind.NotConnected, tuningError.Kind);
    }

    // Given: ready な翻訳接続の receive が壊れた JSON を読んだ直後
    // When: Dispose が epoch を進める
    // Then: decode 失敗を ServerError として下流へ出さない
    [Fact]
    public async Task TranslationDisposeDuringBrokenPayloadDoesNotPublishServerError()
    {
        var transport = new FakeRealtimeServerTransport();
        var connection = new RealtimeTranslationConnection(
            RealtimeTranslationOutputLanguage.Japanese,
            transport,
            "test-safety");
        await connection.StartAsync(
            "sk-test",
            RealtimeTranslationSessionConfig.JapaneseTargetWithoutSourceTranscription());

        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.AfterInboundRead = () =>
        {
            connection.Dispose();
            disposed.TrySetResult();
        };
        transport.EnqueueRaw("{ not json"u8.ToArray());

        await disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await AssertNoServerErrorAsync(connection);
    }

    // Given: ready な原文接続の receive が壊れた JSON を読んだ直後
    // When: Dispose が epoch を進める
    // Then: decode 失敗を ServerError として下流へ出さない
    [Fact]
    public async Task SourceDisposeDuringBrokenPayloadDoesNotPublishServerError()
    {
        var transport = new FakeRealtimeServerTransport();
        var connection = new RealtimeSourceTranscriptionConnection(transport, "test-safety");
        await connection.StartAsync("sk-test", RealtimeSessionTuning.Default);

        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.AfterInboundRead = () =>
        {
            connection.Dispose();
            disposed.TrySetResult();
        };
        transport.EnqueueRaw("{ not json"u8.ToArray());

        await disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await AssertNoServerErrorAsync(connection);
    }

    private static async Task AssertNoTranscriptAsync(RealtimeTranslationConnection connection)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(250);
        while (DateTime.UtcNow < deadline)
        {
            while (connection.Events.TryRead(out var streamEvent))
            {
                Assert.IsNotType<RealtimeTranslationServerEvent.OutputTranscriptDelta>(streamEvent.Event);
            }

            await Task.Delay(10);
        }
    }

    private static async Task AssertNoSourceDeltaAsync(RealtimeSourceTranscriptionConnection connection)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(250);
        while (DateTime.UtcNow < deadline)
        {
            while (connection.Events.TryRead(out var streamEvent))
            {
                Assert.IsNotType<RealtimeTranslationServerEvent.InputTranscriptDelta>(streamEvent.Event);
            }

            await Task.Delay(10);
        }
    }

    private static async Task AssertNoServerErrorAsync<TConnection>(TConnection connection)
        where TConnection : class
    {
        var reader = connection switch
        {
            RealtimeTranslationConnection translation => translation.Events,
            RealtimeSourceTranscriptionConnection source => source.Events,
            _ => throw new ArgumentOutOfRangeException(nameof(connection)),
        };

        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(250);
        while (DateTime.UtcNow < deadline)
        {
            while (reader.TryRead(out var streamEvent))
            {
                Assert.IsNotType<RealtimeTranslationServerEvent.ServerError>(streamEvent.Event);
            }

            await Task.Delay(10);
        }
    }
}
