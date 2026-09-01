using System;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using RealtimeTranslator.Core.OpenAI;
using RealtimeTranslator.Core.Realtime;
using Xunit;

namespace RealtimeTranslator.Core.Tests;

/// <summary>
/// 翻訳接続の graceful close 中ゲート。
/// <c>RealtimeConnectionTests</c> を触っている開いているカバレッジ PR とは交差しない。
/// </summary>
public sealed class RealtimeTranslationConnectionCloseGateTests
{
    // Given: ready な翻訳接続が session.closed 待ちで CloseGracefully している
    // When: その待ちのあいだに音声 frame を Append する
    // Then: 閉じかけ socket へは送らず NotConnected になり、close 自体は完了できる
    [Fact]
    public async Task AppendDuringCloseGracefullyWaitIsNotConnected()
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
        var closeCountAfterStart = transport.CloseCount;

        var closeTask = connection.CloseGracefullyAsync();
        await WaitUntilAsync(() => TypeOf(transport.Sent[^1]) == "session.close");

        var appendError = await Assert.ThrowsAsync<RealtimeTranslationException>(
            () => connection.AppendAudioFrameAsync(Encoding.UTF8.GetBytes("late-frame")));

        Assert.Equal(RealtimeTranslationErrorKind.NotConnected, appendError.Kind);
        Assert.DoesNotContain("late-frame", transport.AppendedFrameTexts());

        transport.EnqueueJson("""{"type":"session.closed"}""");
        await closeTask;
        Assert.True(transport.CloseCount > closeCountAfterStart);
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

    private static string? TypeOf(byte[] payload) =>
        JsonNode.Parse(payload)!.AsObject()["type"]?.GetValue<string>();
}
