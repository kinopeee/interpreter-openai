using System;
using System.Collections.Generic;
using System.Collections.Immutable;
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
    // Then: 音声 routing を英語へ切り替え直し、前セグメントを確定して preroll をリセットする
    [Fact]
    public async Task LanguageFlipFinalizesAndReroutes()
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

        client.PublishSourceDelta("これはテストです");
        await WaitUntilAsync(() => client.SpokenLanguages.Count > 0);
        client.PublishTranslationDelta(RealtimeTranslationOutputLanguage.English, "This is a test");
        await WaitUntilAsync(() =>
        {
            lock (updates)
            {
                return updates.Exists(update => update.TranslatedText.Length > 0);
            }
        });
        var resetsAfterJapanese = client.ResetAudioRoutingCount;

        // When: 間を空けず英語原文が続く
        client.PublishSourceDelta(" now we continue in english for a while");
        await WaitUntilAsync(() => client.SpokenLanguages.Count > 1);

        // Then: 言語切替で再ルーティングし、前セグメントが確定する
        Assert.Equal([SpokenLanguage.Japanese, SpokenLanguage.English], client.SpokenLanguages);
        Assert.True(client.ResetAudioRoutingCount > resetsAfterJapanese);
        await WaitUntilAsync(() =>
        {
            lock (updates)
            {
                return updates.Exists(update => update.ShouldFinalize)
                    || updates.Exists(update => update.SourceText.Contains("now we continue", StringComparison.Ordinal));
            }
        });
        await session.StopAsync();
    }

    // Given: ラテン文字 1 語だけの原文 delta
    // When: 未判定状態で routing を更新する
    // Then: AmbiguousLatin でも英語 lane を先に開く
    [Fact]
    public async Task AmbiguousLatinOpensTheEnglishTranslationTarget()
    {
        var client = new FakeDualClient();
        using var session = NewSession(client);
        await session.StartAsync();
        await WaitUntilAsync(() => session.State == TranslationState.Listening);

        client.PublishSourceDelta("Tokyo");
        await WaitUntilAsync(() => client.SpokenLanguages.Count > 0);

        Assert.Equal(SpokenLanguage.English, client.SpokenLanguages[0]);
        await session.StopAsync();
    }

    // Given: Listening 中の現行 epoch
    // When: 古い epoch の原文 delta が届く
    // Then: routing も字幕も更新しない
    [Fact]
    public async Task StaleEpochSourceDeltaIsIgnored()
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
        var staleEpoch = client.ConnectionEpoch - 1;
        Assert.True(staleEpoch >= 0);

        client.PublishSourceDelta("これは古い接続です", epoch: staleEpoch);
        await Task.Delay(80);

        Assert.Empty(client.SpokenLanguages);
        lock (updates)
        {
            Assert.Empty(updates);
        }

        await session.StopAsync();
    }

    // Given: Listening 中のセッションとカスタム tuningProvider
    // When: tuning を変えて ApplyTuningChangeAsync する
    // Then: dual へ最新 tuning が転送される
    [Fact]
    public async Task ApplyTuningChangeForwardsWhileListening()
    {
        var client = new FakeDualClient();
        var currentTuning = RealtimeSessionTuning.Default;
        using var session = NewSession(client, tuningProvider: () => currentTuning);
        await session.StartAsync();
        await WaitUntilAsync(() => session.State == TranslationState.Listening);
        Assert.Equal(0, client.UpdateTranscriptionTuningCount);

        currentTuning = new RealtimeSessionTuning(
            RealtimeTranslationNoiseReduction.NearField,
            RealtimeTranscriptionDelay.High,
            "Updated glossary",
            ImmutableArray.Create("Acme"));
        await session.ApplyTuningChangeAsync();

        Assert.Equal(1, client.UpdateTranscriptionTuningCount);
        Assert.Equal("Updated glossary", client.LastTuning?.TranscriptionPrompt);
        Assert.Equal(ImmutableArray.Create("Acme"), client.LastTuning?.TranscriptionKeywords);
        await session.StopAsync();
    }

    // Given: Idle のセッション
    // When: ApplyTuningChangeAsync する
    // Then: dual へ転送しない
    [Fact]
    public async Task ApplyTuningChangeIsNoOpWhenIdle()
    {
        var client = new FakeDualClient();
        using var session = NewSession(client);

        await session.ApplyTuningChangeAsync();

        Assert.Equal(0, client.UpdateTranscriptionTuningCount);
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

    // Given: 訳文がまだ無い原文だけのセグメント
    // When: transport error で再接続する
    // Then: 不完全ペアを ShouldFinalize しない（字幕記録へゴミを書かない）
    [Fact]
    public async Task ReconnectDoesNotFinalizeIncompleteSourceOnlyPair()
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

        client.PublishSourceDelta("まだ訳がない発話です");
        await WaitUntilAsync(() => client.SpokenLanguages.Count > 0);
        await WaitUntilAsync(() =>
        {
            lock (updates)
            {
                return updates.Exists(update => update.SourceText.Length > 0);
            }
        });

        client.PublishTransportError();
        await WaitUntilAsync(() => client.StartCount >= 2);

        lock (updates)
        {
            Assert.DoesNotContain(updates, update => update.ShouldFinalize);
        }

        await session.StopAsync();
    }

    // Given: 訳文がまだ無い原文だけのセグメント
    // When: 認証失敗でセッションが止まる
    // Then: 不完全ペアを ShouldFinalize しない
    [Fact]
    public async Task FatalErrorDoesNotFinalizeIncompleteSourceOnlyPair()
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

        client.PublishSourceDelta("認証失敗前の原文だけ");
        await WaitUntilAsync(() => client.SpokenLanguages.Count > 0);
        await WaitUntilAsync(() =>
        {
            lock (updates)
            {
                return updates.Exists(update => update.SourceText.Length > 0);
            }
        });

        client.PublishServerError("Incorrect API key provided", "invalid_api_key");
        await WaitUntilAsync(() => session.State == TranslationState.Error);
        // Flush が遅延して走っても拾えるよう少し待つ。
        await Task.Delay(100);

        lock (updates)
        {
            Assert.DoesNotContain(updates, update => update.ShouldFinalize);
        }
    }

    // Given: idle finalize 前の完全な原文+訳文ペア
    // When: 利用者が録音を停止する
    // Then: Idle へ戻る前に ShouldFinalize が発行される
    [Fact]
    public async Task StopFinalizesCompletePairBeforeIdle()
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

        client.PublishSourceDelta("停止前の完全ペア");
        await WaitUntilAsync(() => client.SpokenLanguages.Count > 0);
        client.PublishTranslationDelta(RealtimeTranslationOutputLanguage.English, "Complete pair before stop");
        await WaitUntilAsync(() =>
        {
            lock (updates)
            {
                return updates.Exists(update =>
                    update.TranslatedText.Length > 0 && !update.ShouldFinalize);
            }
        });

        await session.StopAsync();

        Assert.Equal(TranslationState.Idle, session.State);
        RealtimeSubtitleUpdate finalized;
        lock (updates)
        {
            finalized = updates.Find(update => update.ShouldFinalize);
        }

        Assert.Equal("停止前の完全ペア", finalized.SourceText);
        Assert.Equal("Complete pair before stop", finalized.TranslatedText);
    }

    // Given: idle finalize 前の完全な原文+訳文ペア
    // When: transport error で再接続し BeginNewEpoch する
    // Then: 捨てる前に ShouldFinalize が発行され、オプトイン字幕記録へ届く
    [Fact]
    public async Task ReconnectFinalizesCompletePairBeforeNewEpoch()
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

        client.PublishSourceDelta("会議を始めます");
        await WaitUntilAsync(() => client.SpokenLanguages.Count > 0);
        client.PublishTranslationDelta(RealtimeTranslationOutputLanguage.English, "Let's start the meeting");
        await WaitUntilAsync(() =>
        {
            lock (updates)
            {
                return updates.Exists(update =>
                    update.TranslatedText.Length > 0 && !update.ShouldFinalize);
            }
        });

        client.PublishTransportError();
        await WaitUntilAsync(() =>
        {
            lock (updates)
            {
                return updates.Exists(update => update.ShouldFinalize);
            }
        });
        await WaitUntilAsync(() => client.StartCount >= 2);

        RealtimeSubtitleUpdate finalized;
        lock (updates)
        {
            finalized = updates.Find(update => update.ShouldFinalize);
        }

        Assert.Equal("会議を始めます", finalized.SourceText);
        Assert.Equal("Let's start the meeting", finalized.TranslatedText);
        await session.StopAsync();
    }

    // Given: idle finalize 前の完全ペア
    // When: 認証失敗でセッションが止まる
    // Then: エラー遷移前に ShouldFinalize が発行される
    [Fact]
    public async Task FatalErrorFinalizesCompletePairBeforeStopping()
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

        client.PublishSourceDelta("ありがとうございます");
        await WaitUntilAsync(() => client.SpokenLanguages.Count > 0);
        client.PublishTranslationDelta(RealtimeTranslationOutputLanguage.English, "Thank you");
        await WaitUntilAsync(() =>
        {
            lock (updates)
            {
                return updates.Exists(update =>
                    update.TranslatedText.Length > 0 && !update.ShouldFinalize);
            }
        });

        client.PublishServerError("Incorrect API key provided", "invalid_api_key");
        await WaitUntilAsync(() => session.State == TranslationState.Error);
        await WaitUntilAsync(() =>
        {
            lock (updates)
            {
                return updates.Exists(update => update.ShouldFinalize);
            }
        });

        RealtimeSubtitleUpdate finalized;
        lock (updates)
        {
            finalized = updates.Find(update => update.ShouldFinalize);
        }

        Assert.Equal("ありがとうございます", finalized.SourceText);
        Assert.Equal("Thank you", finalized.TranslatedText);
        Assert.Equal(1, client.StartCount);
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

    // Given: 翻訳とは無関係な想定外の失敗 (音声デバイス障害など)
    // When: セッション中にその例外が投げられる
    // Then: session task を落とさず再接続して録音を継続する
    [Fact]
    public async Task UnexpectedFailureIsTreatedAsRecoverable()
    {
        var client = new FakeDualClient { ThrowOnNextStart = true };
        using var session = NewSession(client);

        await session.StartAsync();
        await WaitUntilAsync(() => session.State == TranslationState.Listening);

        Assert.True(client.StartCount >= 2);
        await session.StopAsync();
        Assert.Equal(TranslationState.Idle, session.State);
    }

    private static InterpretationSession NewSession(
        FakeDualClient client,
        string? apiKey = "sk-test",
        Func<RealtimeSessionTuning>? tuningProvider = null) =>
        new(
            new FakeApiKeyStore(apiKey),
            new FakeAudioCapture(),
            client,
            tuningProvider,
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

        public int ResetAudioRoutingCount { get; private set; }

        public int UpdateTranscriptionTuningCount { get; private set; }

        public RealtimeSessionTuning? LastTuning { get; private set; }

        public bool ThrowOnNextStart { get; set; }

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
                if (ThrowOnNextStart)
                {
                    ThrowOnNextStart = false;
                    throw new InvalidOperationException("unexpected device failure");
                }

                _epoch += 1;
                _spokenLanguages.Clear();
                ResetAudioRoutingCount = 0;
                UpdateTranscriptionTuningCount = 0;
                LastTuning = null;
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
            CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                UpdateTranscriptionTuningCount += 1;
                LastTuning = tuning;
            }

            return Task.CompletedTask;
        }

        public Task ResetAudioRoutingAsync()
        {
            lock (_sync)
            {
                ResetAudioRoutingCount += 1;
            }

            return Task.CompletedTask;
        }

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

        public void PublishSourceDelta(string delta, int? epoch = null) => Publish(
            RealtimeTranslationOutputLanguage.English,
            new RealtimeTranslationServerEvent.InputTranscriptDelta(delta, Guid.NewGuid().ToString(), null),
            epoch);

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

        private void Publish(
            RealtimeTranslationOutputLanguage target,
            RealtimeTranslationServerEvent serverEvent,
            int? epoch = null)
        {
            lock (_sync)
            {
                _events.Writer.TryWrite(
                    new RealtimeTranslationStreamEvent(target, serverEvent, epoch ?? _epoch));
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
