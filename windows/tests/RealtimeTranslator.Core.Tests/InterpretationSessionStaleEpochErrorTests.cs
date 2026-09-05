using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using RealtimeTranslator.Core.Audio;
using RealtimeTranslator.Core.OpenAI;
using RealtimeTranslator.Core.Realtime;
using Xunit;

namespace RealtimeTranslator.Core.Tests;

/// <summary>
/// セッションが古い接続 epoch の ServerError を現行セッションへ適用しないこと。
/// InterpretationSessionTests の開いている PR とはファイルを分けて衝突を避ける。
/// </summary>
public sealed class InterpretationSessionStaleEpochErrorTests
{
    // Given: Listening 中の現行 epoch
    // When: 古い epoch の認証エラーが届く
    // Then: Error に落ちず Listening のまま。現行 epoch の原文はまだルーティングできる
    [Fact]
    public async Task StaleEpochAuthenticationErrorDoesNotStopSession()
    {
        var client = new FakeDualClient();
        using var session = NewSession(client);
        string? message = null;
        session.MessageEncountered += (_, value) => message = value;

        await session.StartAsync();
        await WaitUntilAsync(() => session.State == TranslationState.Listening);
        var staleEpoch = client.ConnectionEpoch - 1;
        Assert.True(staleEpoch >= 0);

        client.PublishServerError("Incorrect API key provided: sk-stale", "invalid_api_key", staleEpoch);
        await Task.Delay(80);

        Assert.Equal(TranslationState.Listening, session.State);
        Assert.Null(message);
        Assert.Equal(1, client.StartCount);

        client.PublishSourceDelta("こんにちは、今日は");
        await WaitUntilAsync(() => client.SpokenLanguages.Count > 0);
        Assert.Equal(SpokenLanguage.Japanese, client.SpokenLanguages[0]);
        Assert.Equal(TranslationState.Listening, session.State);
        await session.StopAsync();
    }

    // Given: Listening 中の現行 epoch
    // When: 古い epoch の transport ServerError が届く
    // Then: 再接続せず Listening のまま
    [Fact]
    public async Task StaleEpochTransportErrorDoesNotReconnect()
    {
        var client = new FakeDualClient();
        using var session = NewSession(client);
        await session.StartAsync();
        await WaitUntilAsync(() => session.State == TranslationState.Listening);
        var startCount = client.StartCount;
        var staleEpoch = client.ConnectionEpoch - 1;

        client.PublishServerError(
            DualRealtimeTranslationClient.TransportErrorMessage,
            DualRealtimeTranslationClient.TransportErrorCode,
            staleEpoch);
        await Task.Delay(80);

        Assert.Equal(TranslationState.Listening, session.State);
        Assert.Equal(startCount, client.StartCount);
        await session.StopAsync();
    }

    // Given: Listening 中に現行 epoch の transport error が届く
    // When: 再接続判定する
    // Then: 対比として再接続が走る（stale 側が無視されていることの健全性）
    [Fact]
    public async Task CurrentEpochTransportErrorStillReconnects()
    {
        var client = new FakeDualClient();
        using var session = NewSession(client);
        await session.StartAsync();
        await WaitUntilAsync(() => session.State == TranslationState.Listening);

        client.PublishServerError(
            DualRealtimeTranslationClient.TransportErrorMessage,
            DualRealtimeTranslationClient.TransportErrorCode);
        await WaitUntilAsync(() => client.StartCount >= 2);

        Assert.True(client.StartCount >= 2);
        await session.StopAsync();
    }

    // Given: Listening 中の現在 epoch と直前 epoch
    // When: backlog transport error を現在 epoch、続けて stale epoch へ発行する
    // Then: 現在 epoch だけが reconnect を開始する
    [Fact]
    public async Task CurrentBacklogErrorReconnectsButStaleBacklogErrorDoesNot()
    {
        var client = new FakeDualClient();
        using var session = NewSession(client);
        await session.StartAsync();
        await WaitUntilAsync(() => session.State == TranslationState.Listening);
        var staleEpoch = client.ConnectionEpoch - 1;

        client.PublishServerError(
            DualRealtimeTranslationClient.TranslationBacklogErrorMessage,
            DualRealtimeTranslationClient.TransportErrorCode);
        await WaitUntilAsync(() => client.StartCount >= 2);
        var reconnectCount = client.StartCount;

        client.PublishServerError(
            DualRealtimeTranslationClient.TranslationBacklogErrorMessage,
            DualRealtimeTranslationClient.TransportErrorCode,
            staleEpoch);
        await Task.Delay(80);

        Assert.Equal(reconnectCount, client.StartCount);
        await session.StopAsync();
    }

    private static InterpretationSession NewSession(FakeDualClient client) =>
        new(
            new FakeApiKeyStore("sk-test"),
            new FakeAudioCapture(),
            client,
            initialReconnectDelay: TimeSpan.FromMilliseconds(1),
            tickInterval: TimeSpan.FromMilliseconds(15));

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
        private Channel<ReadOnlyMemory<byte>> _frames = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();

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
        private readonly List<SpokenLanguage> _spokenLanguages = [];
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

        public IReadOnlyList<SpokenLanguage> SpokenLanguages
        {
            get
            {
                lock (_sync)
                {
                    return [.. _spokenLanguages];
                }
            }
        }

        public LanguagePair? LastStartedPair { get; private set; }

        public Task StartAsync(
            string apiKey,
            RealtimeSessionTuning tuning,
            CancellationToken cancellationToken = default) =>
            StartAsync(apiKey, tuning, LanguagePair.JaEn, cancellationToken);

        public Task StartAsync(
            string apiKey,
            RealtimeSessionTuning tuning,
            LanguagePair pair,
            CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                StartCount += 1;
                LastStartedPair = pair;
                _epoch += 1;
                DeliveryState = new EventDeliveryState(_epoch);
                _spokenLanguages.Clear();
                _events = Channel.CreateUnbounded<RealtimeTranslationStreamEvent>();
            }

            return Task.CompletedTask;
        }

        public Task AppendAudioFrameAsync(
            ReadOnlyMemory<byte> pcm16LittleEndian,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SelectTranslationTargetAsync(
            RealtimeTranslationOutputLanguage? target,
            CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                if (target is { } selected
                    && LastStartedPair is { } pair
                    && pair.Counterpart(selected) is { } spoken)
                {
                    _spokenLanguages.Add(spoken);
                }
            }

            return Task.CompletedTask;
        }

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

        public void PublishSourceDelta(string delta, int? epoch = null) => PublishLane(
            RealtimeTranslationLane.Source,
            new RealtimeTranslationServerEvent.InputTranscriptDelta(delta, Guid.NewGuid().ToString(), null),
            epoch);

        public void PublishServerError(string message, string code, int? epoch = null) => PublishLane(
            RealtimeTranslationLane.Translation(RealtimeTranslationOutputLanguage.English),
            new RealtimeTranslationServerEvent.ServerError(message, code),
            epoch);

        private void Complete()
        {
            lock (_sync)
            {
                _events.Writer.TryComplete();
            }
        }

        private void PublishLane(
            RealtimeTranslationLane lane,
            RealtimeTranslationServerEvent serverEvent,
            int? epoch = null)
        {
            lock (_sync)
            {
                _events.Writer.TryWrite(
                    new RealtimeTranslationStreamEvent(lane, serverEvent, epoch ?? _epoch));
            }
        }
    }
}
