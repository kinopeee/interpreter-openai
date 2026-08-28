using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using RealtimeTranslator.Core.OpenAI;
using RealtimeTranslator.Core.Realtime;
using Xunit;

namespace RealtimeTranslator.Core.Tests;

/// <summary>
/// 翻訳 lane の runtime ServerError が Dual merge を通り、セッション分類・秘匿まで届く契約。
/// FakeDualClient 経路や原文 lane の remap とは別に、実 Dual で固定する。
/// </summary>
public sealed class InterpretationSessionTranslationLaneMergeTests
{
    // Given: Listening 中の実 Dual セッション
    // When: 英語翻訳 lane へ鍵断片付きの非認証 error が届く
    // Then: Error になり、MessageEncountered は汎用文言だけ。再接続しない
    [Fact]
    public async Task TranslationLaneRuntimeKeyLeakIsRedactedAndStops()
    {
        var source = new FakeRealtimeServerTransport();
        var english = new FakeRealtimeServerTransport();
        var japanese = new FakeRealtimeServerTransport();
        using var dual = CreateDual(source, english, japanese);
        using var session = NewSession(dual);
        string? message = null;
        session.MessageEncountered += (_, value) => message = value;

        await session.StartAsync();
        await WaitUntilAsync(() => session.State == TranslationState.Listening);
        Assert.Equal(1, english.ConnectCount);

        english.EnqueueJson(
            """{"type":"error","error":{"message":"Provider echo included sk-lane-secret","code":"server_error"}}""");

        await WaitUntilAsync(() => session.State == TranslationState.Error);
        await WaitUntilAsync(() => message is not null);
        await Task.Delay(40);

        Assert.Equal(RealtimeTranslationException.GenericServerMessage, message);
        Assert.DoesNotContain("sk-", message, StringComparison.Ordinal);
        Assert.DoesNotContain("lane-secret", message, StringComparison.Ordinal);
        Assert.Equal(1, english.ConnectCount);
        Assert.Equal(1, source.ConnectCount);
    }

    // Given: Listening 中の実 Dual セッション
    // When: 英語翻訳 lane へ秘密を含まない fatal error が届く
    // Then: 再接続せず Error になり、サーバー文言をそのまま出す
    [Fact]
    public async Task TranslationLaneRuntimeFatalStopsWithoutReconnect()
    {
        var source = new FakeRealtimeServerTransport();
        var english = new FakeRealtimeServerTransport();
        var japanese = new FakeRealtimeServerTransport();
        using var dual = CreateDual(source, english, japanese);
        using var session = NewSession(dual);
        string? message = null;
        session.MessageEncountered += (_, value) => message = value;

        await session.StartAsync();
        await WaitUntilAsync(() => session.State == TranslationState.Listening);
        Assert.Equal(1, english.ConnectCount);

        english.EnqueueJson(
            """{"type":"error","error":{"message":"rate_limit exceeded for translation lane","code":"rate_limit"}}""");

        await WaitUntilAsync(() => session.State == TranslationState.Error);
        await WaitUntilAsync(() => message is not null);
        await Task.Delay(40);

        Assert.Equal("rate_limit exceeded for translation lane", message);
        Assert.NotEqual("OpenAI APIキーが無効です", message);
        Assert.Equal(1, english.ConnectCount);
        Assert.Equal(1, source.ConnectCount);
    }

    private static InterpretationSession NewSession(IDualRealtimeTranslationClient dual) =>
        new(
            new FakeApiKeyStore("sk-test"),
            new FakeAudioCapture(),
            dual,
            initialReconnectDelay: TimeSpan.FromMilliseconds(1),
            tickInterval: TimeSpan.FromMilliseconds(20));

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

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(5);
        }

        Assert.Fail("condition was not met in time");
    }

    private sealed class FakeApiKeyStore(string? apiKey) : IApiKeyStore
    {
        public string? Load() => apiKey;
    }

    private sealed class FakeAudioCapture : IRealtimeAudioCapture
    {
        private readonly object _sync = new();
        private Channel<ReadOnlyMemory<byte>> _frames =
            Channel.CreateUnbounded<ReadOnlyMemory<byte>>();

        public ChannelReader<ReadOnlyMemory<byte>> Frames
        {
            get
            {
                lock (_sync)
                {
                    return _frames.Reader;
                }
            }
        }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                if (_frames.Reader.Completion.IsCompleted)
                {
                    _frames = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
                }
            }

            return Task.CompletedTask;
        }

        public Task StopAsync() => Task.CompletedTask;
    }
}
