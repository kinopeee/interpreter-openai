using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using RealtimeTranslator.Core.Audio;
using RealtimeTranslator.Core.OpenAI;
using RealtimeTranslator.Core.Realtime;
using Xunit;

namespace RealtimeTranslator.Core.Tests;

/// <summary>
/// 再接続 Start の例外分類と、stop 世代更新後の致命エラーを Error に昇格させない契約。
/// InterpretationSessionTests の開いている PR とはファイルを分けて衝突を避ける。
/// </summary>
public sealed class InterpretationSessionReconnectClassificationTests
{
    // Given: Listening 後の再接続 Start が SessionUpdateTimeout する
    // When: transport error で再接続する
    // Then: 回復可能として Error にせず、次の Start で Listening に戻る
    [Fact]
    public async Task SessionUpdateTimeoutOnReconnectStartKeepsReconnecting()
    {
        var client = new FakeDualClient();
        using var session = NewSession(client);
        string? message = null;
        session.MessageEncountered += (_, value) => message = value;

        await session.StartAsync();
        await WaitUntilAsync(() => session.State == TranslationState.Listening);
        Assert.Equal(1, client.StartCount);

        client.NextStartErrorKind = RealtimeTranslationErrorKind.SessionUpdateTimeout;
        client.PublishTransportError();
        await WaitUntilAsync(() =>
            session.State == TranslationState.Listening && client.StartCount >= 3);

        Assert.Equal(TranslationState.Listening, session.State);
        Assert.Null(message);
        Assert.True(client.StartCount >= 3);
        await session.StopAsync();
        Assert.Equal(TranslationState.Idle, session.State);
    }

    // Given: Listening 後の再接続 Start が CloseTimeout する
    // When: transport error で再接続する
    // Then: 回復不能として Error になり、追加の再接続 Start をしない
    [Fact]
    public async Task CloseTimeoutOnReconnectStartEntersErrorWithoutRetry()
    {
        var client = new FakeDualClient();
        using var session = NewSession(client);
        string? message = null;
        session.MessageEncountered += (_, value) => message = value;

        await session.StartAsync();
        await WaitUntilAsync(() => session.State == TranslationState.Listening);
        Assert.Equal(1, client.StartCount);

        client.NextStartErrorKind = RealtimeTranslationErrorKind.CloseTimeout;
        client.PublishTransportError();
        await WaitUntilAsync(() => session.State == TranslationState.Error);
        await WaitUntilAsync(() => message is not null);
        await Task.Delay(40);

        Assert.Equal(TranslationState.Error, session.State);
        Assert.Equal("翻訳セッションの終了待ちがタイムアウトしました", message);
        Assert.Equal(2, client.StartCount);
    }

    // Given: Listening 後の再接続 Start が AuthenticationFailed する
    // When: transport error で再接続する
    // Then: 認証失敗として Error になり、再接続を続けない
    [Fact]
    public async Task AuthenticationFailureOnReconnectStartStopsWithoutRetry()
    {
        var client = new FakeDualClient();
        using var session = NewSession(client);
        string? message = null;
        session.MessageEncountered += (_, value) => message = value;

        await session.StartAsync();
        await WaitUntilAsync(() => session.State == TranslationState.Listening);
        Assert.Equal(1, client.StartCount);

        client.NextStartErrorKind = RealtimeTranslationErrorKind.AuthenticationFailed;
        client.PublishTransportError();
        await WaitUntilAsync(() => session.State == TranslationState.Error);
        await WaitUntilAsync(() => message is not null);
        await Task.Delay(40);

        Assert.Equal(TranslationState.Error, session.State);
        Assert.Equal("OpenAI APIキーが無効です", message);
        Assert.Equal(2, client.StartCount);
    }

    // Given: 再接続 Start がゲート待ちのあいだに利用者が Stop する
    // When: 世代更新後に Dual.Start が致命エラーを投げる
    // Then: stop 中の teardown 失敗は Error に昇格せず Idle へ戻る
    [Fact]
    public async Task FatalStartAfterStopDoesNotEnterError()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeDualClient();
        using var session = NewSession(client);
        string? message = null;
        var sawError = false;
        session.MessageEncountered += (_, value) => message = value;
        session.StateChanged += (_, state) =>
        {
            if (state == TranslationState.Error)
            {
                sawError = true;
            }
        };

        await session.StartAsync();
        await WaitUntilAsync(() => session.State == TranslationState.Listening);

        client.IgnoreStartCancellation = true;
        client.NextStartErrorKind = RealtimeTranslationErrorKind.AuthenticationFailed;
        client.StartGate = gate;
        client.PublishTransportError();
        await WaitUntilAsync(() => client.StartCount == 2);

        var stopTask = session.StopAsync();
        await WaitUntilAsync(() => session.State == TranslationState.Closing);
        gate.TrySetResult();
        await stopTask;

        Assert.Equal(TranslationState.Idle, session.State);
        Assert.False(sawError);
        Assert.Null(message);
        Assert.Equal(2, client.StartCount);
    }

    private static InterpretationSession NewSession(FakeDualClient client) =>
        new(
            new FakeApiKeyStore("sk-test"),
            new FakeAudioCapture(),
            client,
            initialReconnectDelay: TimeSpan.FromMilliseconds(1),
            tickInterval: TimeSpan.FromMilliseconds(20));

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

    private sealed class FakeDualClient : IDualRealtimeTranslationClient
    {
        private readonly object _sync = new();
        private Channel<RealtimeTranslationStreamEvent> _events =
            Channel.CreateUnbounded<RealtimeTranslationStreamEvent>();
        private int _epoch;
        public EventDeliveryState DeliveryState { get; private set; } = new(0);
        public RealtimeEventFeed Feed => new(Events, ConnectionEpoch, DeliveryState);

        public ChannelReader<RealtimeTranslationStreamEvent> Events
        {
            get
            {
                lock (_sync)
                {
                    return _events.Reader;
                }
            }
        }

        public int ConnectionEpoch
        {
            get
            {
                lock (_sync)
                {
                    return _epoch;
                }
            }
        }

        public int StartCount { get; private set; }

        public RealtimeTranslationErrorKind? NextStartErrorKind { get; set; }

        public bool IgnoreStartCancellation { get; set; }

        public TaskCompletionSource? StartGate { get; set; }

        public Task StartAsync(
            string apiKey,
            RealtimeSessionTuning tuning,
            CancellationToken cancellationToken = default) =>
            StartAsync(apiKey, tuning, LanguagePair.JaEn, cancellationToken);

        public async Task StartAsync(
            string apiKey,
            RealtimeSessionTuning tuning,
            LanguagePair pair,
            CancellationToken cancellationToken = default)
        {
            Task? gateTask;
            RealtimeTranslationErrorKind? throwKind;
            bool ignoreCancellation;
            lock (_sync)
            {
                StartCount += 1;
                gateTask = StartGate?.Task;
                throwKind = NextStartErrorKind;
                NextStartErrorKind = null;
                ignoreCancellation = IgnoreStartCancellation;
            }

            if (gateTask is not null)
            {
                if (ignoreCancellation)
                {
                    await gateTask.ConfigureAwait(false);
                }
                else
                {
                    await gateTask.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            if (throwKind is { } kind)
            {
                throw new RealtimeTranslationException(kind);
            }

            lock (_sync)
            {
                _epoch += 1;
                DeliveryState = new EventDeliveryState(_epoch);
                _events = Channel.CreateUnbounded<RealtimeTranslationStreamEvent>();
            }
        }

        public Task AppendAudioFrameAsync(
            ReadOnlyMemory<byte> pcm16LittleEndian,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SelectTranslationTargetAsync(
            RealtimeTranslationOutputLanguage? target,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task UpdateTranscriptionTuningAsync(
            RealtimeSessionTuning tuning,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ResetAudioRoutingAsync() => Task.CompletedTask;

        public Task CloseGracefullyAsync(CancellationToken cancellationToken = default)
        {
            Complete();
            return Task.CompletedTask;
        }

        public Task ForceCloseAsync()
        {
            Complete();
            return Task.CompletedTask;
        }

        public void PublishTransportError() => Publish(
            new RealtimeTranslationServerEvent.ServerError(
                DualRealtimeTranslationClient.TransportErrorMessage,
                DualRealtimeTranslationClient.TransportErrorCode));

        private void Complete()
        {
            lock (_sync)
            {
                _events.Writer.TryComplete();
            }
        }

        private void Publish(RealtimeTranslationServerEvent serverEvent)
        {
            lock (_sync)
            {
                _events.Writer.TryWrite(
                    new RealtimeTranslationStreamEvent(
                        RealtimeTranslationLane.Translation(RealtimeTranslationOutputLanguage.English),
                        serverEvent,
                        _epoch));
            }
        }
    }
}
