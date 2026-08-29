using System;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using RealtimeTranslator.Core.OpenAI;
using RealtimeTranslator.Core.Realtime;
using Xunit;

namespace RealtimeTranslator.Core.Tests;

/// <summary>
/// 原文接続の completed 待ち中キャンセル。CloseTimeout 経路とは交差しない。
/// </summary>
public sealed class RealtimeSourceTranscriptionConnectionCloseCancelTests
{
    // Given: transcription.completed を返さない ready な原文接続
    // When: graceful close の completed 待ち中に呼び出し側 token をキャンセルする
    // Then: CloseTimeout まで待たず OperationCanceledException になり、transport は解放される
    [Fact]
    public async Task CloseGracefullyCancelDuringCompletedPollAbortsWithoutWaitingForTimeout()
    {
        var transport = new FakeRealtimeServerTransport();
        var connection = new RealtimeSourceTranscriptionConnection(
            transport,
            "test-safety",
            closeTimeout: TimeSpan.FromSeconds(5));
        await connection.StartAsync("sk-test", RealtimeSessionTuning.Default);

        using var caller = new CancellationTokenSource();
        var closeTask = connection.CloseGracefullyAsync(caller.Token);
        await WaitUntilAsync(() => SentTypes(transport).Contains("input_audio_buffer.commit"));
        var closeCountBeforeCancel = transport.CloseCount;
        var started = Stopwatch.StartNew();
        await caller.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => closeTask);
        started.Stop();

        Assert.True(started.Elapsed < TimeSpan.FromSeconds(2));
        Assert.True(transport.CloseCount > closeCountBeforeCancel);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail("condition was not met in time");
    }

    private static string[] SentTypes(FakeRealtimeServerTransport transport) =>
        transport.Sent
            .Select(payload => JsonNode.Parse(payload)?.AsObject()["type"]?.GetValue<string>() ?? string.Empty)
            .Where(type => type.Length > 0)
            .ToArray();
}
