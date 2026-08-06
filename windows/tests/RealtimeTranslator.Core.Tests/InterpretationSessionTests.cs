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

/// <summary>録音・3 接続・字幕組み立てを束ねるセッションの契約。</summary>
public sealed class InterpretationSessionTests
{
    // Given: API キー未登録
    // When: セッションを開始する
    // Then: 接続を試みずエラー状態へ落ちる
    [Fact]
    public async Task StartWithoutApiKeyEntersErrorState()
    {
        var client = new FakeDualClient();
        using var session = NewSession(client, apiKey: null);
        var states = new List<TranslationState>();
        session.StateChanged += (_, state) => states.Add(state);

        await session.StartAsync();
        await WaitUntilAsync(() => session.State == TranslationState.Error);

        Assert.Equal(0, client.StartCount);
        Assert.Contains(TranslationState.Error, states);
    }

    // Given: 日本語の原文 delta
    // When: セッションが routing を更新する
    // Then: 話者を日本語と判定し、音声を英語 target へ切り替える
    [Fact]
    public async Task JapaneseSourceRoutesAudioToTheEnglishTarget()
    {
        var client = new FakeDualClient();
        using var session = NewSession(client);
        await session.StartAsync();
        await WaitUntilAsync(() => session.State == TranslationState.Listening);

        client.PublishSourceDelta("こんにちは、今日は");
        await WaitUntilAsync(() => client.SpokenLanguages.Count > 0);

        Assert.Equal(SpokenLanguage.Japanese, client.SpokenLanguages[0]);
        await session.StopAsync();
    }

    // Given: 英語の原文 delta と日本語 lane の訳文
    // When: 原文と訳文が揃う
    // Then: 原文 authority と訳文をペアにした字幕を発行する
    [Fact]
    public async Task PairsSourceTranscriptWithTheSelectedTranslationLane()
    {
        var client = new FakeDualClient();
        using var session = NewSession(client);
        var updates = new List<RealtimeSubtitleUpdate>();
        session.SubtitleUpdated += (_, update) =>
        {
            lock (updates)
            {
                updates.Add(update);
            }
        };

        await session.StartAsync();
        await WaitUntilAsync(() => session.State == TranslationState.Listening);

        client.PublishSourceDelta("good morning everyone");
        await WaitUntilAsync(() => client.SpokenLanguages.Count > 0);
        client.PublishTranslationDelta(RealtimeTranslationOutputLanguage.Japanese, "おはようございます");

        await WaitUntilAsync(() =>
        {
            lock (updates)
            {
                return updates.Exists(update => update.TranslatedText.Length > 0);
            }
        });

        RealtimeSubtitleUpdate paired;
        lock (updates)
        {
            paired = updates.FindLast(update => update.TranslatedText.Length > 0);
        }

        Assert.Equal(SpokenLanguage.English, client.SpokenLanguages[0]);
        Assert.Equal("good morning everyone", paired.SourceText);
        Assert.Equal("おはようございます", paired.TranslatedText);
        await session.StopAsync();
    }

    // Given: 日本語で話し始めた後に英語へ切り替わる原文
    // When: 文字種の反転を検出する
    // Then: 音声 routing を日本語 target へ切り替え直す
    [Fact]
    public async Task LanguageFlipSwitchesTheTranslationTarget()
    {
        var client = new FakeDualClient();
        using var session = NewSession(client);
        await session.StartAsync();
        await WaitUntilAsync(() => session.State == TranslationState.Listening);

        client.PublishSourceDelta("これはテストです");
        await WaitUntilAsync(() => client.SpokenLanguages.Count > 0);
        client.PublishSourceDelta(" now we continue in english for a while");
        await WaitUntilAsync(() => client.SpokenLanguages.Count > 1);

        Assert.Equal([SpokenLanguage.Japanese, SpokenLanguage.English], client.SpokenLanguages);
        await session.StopAsync();
    }

    // Given: 翻訳送信の連続失敗による transport error
    // When: セッションがイベントを受け取る
    // Then: 再接続して新しい epoch で再開する
    [Fact]
    public async Task RecoverableTransportErrorTriggersReconnect()
    {
        var client = new FakeDualClient();
        using var session = NewSession(client);
        await session.StartAsync();
        await WaitUntilAsync(() => session.State == TranslationState.Listening);

        client.PublishTransportError();
        await WaitUntilAsync(() => client.StartCount >= 2);

        Assert.True(client.StartCount >= 2);
        await session.StopAsync();
    }

    // Given: 認証に失敗するサーバー
    // When: セッションがエラーイベントを受け取る
    // Then: 再接続せずエラー状態で止まる
    [Fact]
    public async Task AuthenticationFailureStopsWithoutReconnect()
    {
        var client = new FakeDualClient();
        using var session = NewSession(client);
        await session.StartAsync();
        await WaitUntilAsync(() => session.State == TranslationState.Listening);

        client.PublishServerError("Incorrect API key provided", "invalid_api_key");
        await WaitUntilAsync(() => session.State == TranslationState.Error);

        Assert.Equal(1, client.StartCount);
    }

    private static InterpretationSession NewSession(FakeDualClient client, string? apiKey = "sk-test") =>
        new(
            new FakeApiKeyStore(apiKey),
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
        private readonly Channel<ReadOnlyMemory<byte>> _frames =
            Channel.CreateUnbounded<ReadOnlyMemory<byte>>();

        public ChannelReader<ReadOnlyMemory<byte>> Frames => _frames.Reader;

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync() => Task.CompletedTask;
    }

    private sealed class FakeDualClient : IDualRealtimeTranslationClient
    {
        private readonly object _sync = new();
        private readonly List<SpokenLanguage> _spokenLanguages = [];
        private Channel<RealtimeTranslationStreamEvent> _events =
            Channel.CreateUnbounded<RealtimeTranslationStreamEvent>();

        private int _epoch;

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

        public Task StartAsync(
            string apiKey,
            RealtimeSessionTuning tuning,
            CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                StartCount += 1;
                _epoch += 1;
                _spokenLanguages.Clear();
                _events = Channel.CreateUnbounded<RealtimeTranslationStreamEvent>();
            }

            return Task.CompletedTask;
        }

        public Task AppendAudioFrameAsync(
            ReadOnlyMemory<byte> pcm16LittleEndian,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SetSpokenLanguageAsync(
            SpokenLanguage language,
            CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                _spokenLanguages.Add(language);
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

        public void PublishSourceDelta(string delta) => Publish(
            RealtimeTranslationOutputLanguage.English,
            new RealtimeTranslationServerEvent.InputTranscriptDelta(delta, Guid.NewGuid().ToString(), null));

        public void PublishTranslationDelta(RealtimeTranslationOutputLanguage target, string delta) => Publish(
            target,
            new RealtimeTranslationServerEvent.OutputTranscriptDelta(delta, Guid.NewGuid().ToString(), null));

        public void PublishTransportError() => Publish(
            RealtimeTranslationOutputLanguage.English,
            new RealtimeTranslationServerEvent.ServerError(
                DualRealtimeTranslationClient.TransportErrorMessage,
                DualRealtimeTranslationClient.TransportErrorCode));

        public void PublishServerError(string message, string code) => Publish(
            RealtimeTranslationOutputLanguage.English,
            new RealtimeTranslationServerEvent.ServerError(message, code));

        private void Publish(RealtimeTranslationOutputLanguage target, RealtimeTranslationServerEvent serverEvent)
        {
            lock (_sync)
            {
                _events.Writer.TryWrite(new RealtimeTranslationStreamEvent(target, serverEvent, _epoch));
            }
        }

        private void Complete()
        {
            lock (_sync)
            {
                _events.Writer.TryComplete();
            }
        }
    }
}
