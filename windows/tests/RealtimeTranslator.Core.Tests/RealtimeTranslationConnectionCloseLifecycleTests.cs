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
/// 翻訳接続の CloseGracefully 再入と、session.closed 待ち中の呼び出し側キャンセル。
/// 開いている close-gate / drain / lifecycle PR とは交差しない。
/// </summary>
public sealed class RealtimeTranslationConnectionCloseLifecycleTests
{
    // Given: session.closed を返さず CloseTimeout になった翻訳接続
    // When: もう一度 CloseGracefully する
    // Then: session.close を再送せず、追加の TearDown もしない
    [Fact]
    public async Task SecondCloseGracefullyAfterTimeoutDoesNotResendSessionClose()
    {
        var transport = new FakeRealtimeServerTransport();
        var connection = new RealtimeTranslationConnection(
            RealtimeTranslationOutputLanguage.English,
            transport,
            "test-safety",
            closeTimeout: TimeSpan.FromMilliseconds(250));
        await connection.StartAsync(
            "sk-test",
            RealtimeTranslationSessionConfig.EnglishTargetWithoutSourceTranscription());

        var first = await Assert.ThrowsAsync<RealtimeTranslationException>(
            () => connection.CloseGracefullyAsync());
        Assert.Equal(RealtimeTranslationErrorKind.CloseTimeout, first.Kind);
        var closeCountAfterFirst = transport.CloseCount;
        var sessionCloseCount = SentTypes(transport).Count(type => type == "session.close");

        await connection.CloseGracefullyAsync();

        Assert.Equal(closeCountAfterFirst, transport.CloseCount);
        Assert.Equal(sessionCloseCount, SentTypes(transport).Count(type => type == "session.close"));
        Assert.Equal(1, sessionCloseCount);
    }

    // Given: ready な翻訳接続が session.closed 待ち中
    // When: 並行してもう一本 CloseGracefully する
    // Then: session.close は 1 回だけ。後続は _isClosing で即 return する
    [Fact]
    public async Task ConcurrentCloseGracefullySendsSessionCloseOnce()
    {
        var transport = new FakeRealtimeServerTransport();
        var connection = new RealtimeTranslationConnection(
            RealtimeTranslationOutputLanguage.Japanese,
            transport,
            "test-safety",
            closeTimeout: TimeSpan.FromMilliseconds(400));
        await connection.StartAsync(
            "sk-test",
            RealtimeTranslationSessionConfig.JapaneseTargetWithoutSourceTranscription());

        var first = connection.CloseGracefullyAsync();
        var second = connection.CloseGracefullyAsync();

        var firstError = await Assert.ThrowsAsync<RealtimeTranslationException>(() => first);
        await second;

        Assert.Equal(RealtimeTranslationErrorKind.CloseTimeout, firstError.Kind);
        Assert.Equal(1, SentTypes(transport).Count(type => type == "session.close"));
    }

    // Given: ready な翻訳接続が session.closed を待っている
    // When: 呼び出し側 token をキャンセルする
    // Then: CloseTimeout まで待たず OperationCanceledException になり、transport は解放される
    [Fact]
    public async Task CloseGracefullyCancelDuringClosedPollAbortsWithoutWaitingForTimeout()
    {
        var transport = new FakeRealtimeServerTransport();
        var connection = new RealtimeTranslationConnection(
            RealtimeTranslationOutputLanguage.English,
            transport,
            "test-safety",
            closeTimeout: TimeSpan.FromSeconds(5));
        await connection.StartAsync(
            "sk-test",
            RealtimeTranslationSessionConfig.EnglishTargetWithoutSourceTranscription());

        using var caller = new CancellationTokenSource();
        var closeTask = connection.CloseGracefullyAsync(caller.Token);
        await WaitUntilAsync(() => SentTypes(transport).Contains("session.close"));
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
