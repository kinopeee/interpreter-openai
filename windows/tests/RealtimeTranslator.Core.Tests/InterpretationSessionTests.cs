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

    // Given: Dual client の Start が完了するまで待機できる fake
    // When: StartAsync を呼び、Dual Start 完了前を観測する
    // Then: Dual Start 解放後にだけ capture が始まる
    [Fact]
    public async Task StartDoesNotCaptureBeforeDualClientReady()
    {
        var client = new FakeDualClient();
        var audio = new FakeAudioCapture();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.StartGate = gate;
        using var session = NewSession(client, audio: audio);

        var startTask = session.StartAsync();
        await WaitUntilAsync(() => client.StartCount == 1);
        Assert.Equal(0, audio.StartCallCount);

        gate.SetResult();
        client.StartGate = null;
        await WaitUntilAsync(() => audio.StartCallCount == 1);
        await WaitUntilAsync(() => session.State == TranslationState.Listening);
        await startTask;

        await session.StopAsync();
    }

    // Given: Dual Start 待ちで止まっているセッション
    // When: Start 直後に Stop する
    // Then: Idle に戻り、capture は開始されないか停止済みになる
    [Fact]
    public async Task StopDuringStartDoesNotLeaveListening()
    {
        var client = new FakeDualClient();
        var audio = new FakeAudioCapture();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.StartGate = gate;
        using var session = NewSession(client, audio: audio);

        var startTask = session.StartAsync();
        await WaitUntilAsync(() => client.StartCount == 1);

        var stopTask = session.StopAsync();
        await WaitUntilAsync(() =>
            session.State is TranslationState.Closing or TranslationState.Idle);
        client.StartGate = null;
        gate.SetResult();
        await stopTask;
        await startTask;

        Assert.Equal(TranslationState.Idle, session.State);
        Assert.False(audio.IsRunning);
    }

    // Given: 録音中のセッション
    // When: Stop を二重に呼ぶ
    // Then: 2 回目は no-op で Idle のまま壊れない
    [Fact]
    public async Task DoubleStopIsIdempotent()
    {
        var client = new FakeDualClient();
        using var session = NewSession(client);
        await session.StartAsync();
        await WaitUntilAsync(() => session.State == TranslationState.Listening);

        await session.StopAsync();
        Assert.Equal(TranslationState.Idle, session.State);
        var closeCount = client.CloseGracefullyCallCount;

        await session.StopAsync();
        Assert.Equal(TranslationState.Idle, session.State);
        Assert.Equal(closeCount, client.CloseGracefullyCallCount);
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
        RealtimeSubtitleUpdate finalized = default;
        await WaitUntilAsync(() =>
        {
            lock (updates)
            {
                finalized = updates.Find(update => update.ShouldFinalize);
                return finalized.ShouldFinalize;
            }
        });
        Assert.Equal("これはテストです", finalized.SourceText);
        Assert.Equal("This is a test", finalized.TranslatedText);
        await session.StopAsync();
    }

    // Given: 文字種の反転を起こさない英語 delta がサーバから連続で流れ続ける
    // When: 同一セグメント内で delta を大量に取り込む
    // Then: routing 判定バッファは上限までで打ち切られ、その後の反転検出も壊れない
    [Fact]
    public async Task NonFlippingSourceDeltaStreamDoesNotGrowRoutingBufferWithoutBound()
    {
        var client = new FakeDualClient();
        using var session = NewSession(client);

        await session.StartAsync();
        await WaitUntilAsync(() => session.State == TranslationState.Listening);

        client.PublishSourceDelta("we keep talking in english ");
        await WaitUntilAsync(() => client.SpokenLanguages.Count > 0);

        var processedDeltaCount = 0;
        session.BeforeAssemblerIngestForTests = () => Interlocked.Increment(ref processedDeltaCount);
        const int nonFlippingDeltaCount = 200;
        for (var i = 0; i < nonFlippingDeltaCount; i += 1)
        {
            client.PublishSourceDelta("and we never flip the script ");
        }

        // When: 反転前に大量 delta の取り込み完了を待ち、その時点で上限を検証する
        await WaitUntilAsync(() => Volatile.Read(ref processedDeltaCount) >= nonFlippingDeltaCount);
        Assert.True(
            session.RoutingSourceTextLengthForTests <= InterpretationSession.RoutingSourceTextMaxLength,
            $"routing buffer length {session.RoutingSourceTextLengthForTests} exceeded the cap before flip");

        client.PublishSourceDelta("ここで日本語へ反転します");
        await WaitUntilAsync(() => client.SpokenLanguages.Count > 1);

        Assert.Equal([SpokenLanguage.English, SpokenLanguage.Japanese], client.SpokenLanguages);
        Assert.True(
            session.RoutingSourceTextLengthForTests <= InterpretationSession.RoutingSourceTextMaxLength,
            $"routing buffer length {session.RoutingSourceTextLengthForTests} exceeded the cap after flip");
        await session.StopAsync();
    }

    // Given: 日本語セグメントのあと、長い空白 run で隔てられた複数語の英語 delta
    // When: UTF-16 文字数キャップだけだと末尾 1 語しか残らない入力を取り込む
    // Then: RecentEvidence ウィンドウを保ち英語反転できる
    [Fact]
    public async Task WideWhitespaceBetweenLatinWordsStillFlipsJapaneseToEnglish()
    {
        var client = new FakeDualClient();
        using var session = NewSession(client);

        await session.StartAsync();
        await WaitUntilAsync(() => session.State == TranslationState.Listening);

        client.PublishSourceDelta("これはテストです");
        await WaitUntilAsync(() => client.SpokenLanguages.Count > 0);
        Assert.Equal(SpokenLanguage.Japanese, client.SpokenLanguages[0]);

        // 非空白 16 scalar / 2 語以上を満たしつつ、語間空白だけが上限を超える入力。
        var gap = new string(' ', InterpretationSession.RoutingSourceTextMaxLength + 32);
        client.PublishSourceDelta("aa bb cc dd ee ff gg" + gap + " hh");
        await WaitUntilAsync(() => client.SpokenLanguages.Count > 1);

        Assert.Equal([SpokenLanguage.Japanese, SpokenLanguage.English], client.SpokenLanguages);
        Assert.True(
            session.RoutingSourceTextLengthForTests <= InterpretationSession.RoutingSourceTextMaxLength,
            $"routing buffer length {session.RoutingSourceTextLengthForTests} exceeded the cap");
        await session.StopAsync();
    }

    // Given: 訳文がまだ無い日本語原文だけのセグメント
    // When: 英語へ文字種が反転する
    // Then: 不完全ペアを ShouldFinalize せず、routing だけ切り替える
    [Fact]
    public async Task LanguageFlipDoesNotFinalizeIncompleteSourceOnlyPair()
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

        client.PublishSourceDelta("これは原文だけです");
        await WaitUntilAsync(() => client.SpokenLanguages.Count > 0);
        await WaitUntilAsync(() =>
        {
            lock (updates)
            {
                return updates.Exists(update => update.SourceText.Length > 0);
            }
        });
        var resetsAfterJapanese = client.ResetAudioRoutingCount;

        client.PublishSourceDelta(" now we continue in english for a while");
        await WaitUntilAsync(() => client.SpokenLanguages.Count > 1);
        await WaitUntilAsync(() =>
        {
            lock (updates)
            {
                return updates.Exists(update =>
                    update.SourceText.Contains("now we continue", StringComparison.Ordinal));
            }
        });
        // Finalize が遅延して走っても拾えるよう少し待つ。
        await Task.Delay(100);

        Assert.Equal([SpokenLanguage.Japanese, SpokenLanguage.English], client.SpokenLanguages);
        Assert.True(client.ResetAudioRoutingCount > resetsAfterJapanese);
        lock (updates)
        {
            Assert.DoesNotContain(updates, update => update.ShouldFinalize);
        }

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

    // Given: 日本語 routing が確定したあとのセグメント
    // When: 末尾ウィンドウが AmbiguousLatin（ラテン 1 語）だけになる
    // Then: segment 境界として反転せず、英語 lane へ切り替えない
    [Fact]
    public async Task AmbiguousLatinDoesNotFlipEstablishedJapaneseRouting()
    {
        var client = new FakeDualClient();
        using var session = NewSession(client);
        await session.StartAsync();
        await WaitUntilAsync(() => session.State == TranslationState.Listening);

        client.PublishSourceDelta("これはテストですよ今日は良い天気ですね本当に");
        await WaitUntilAsync(() => client.SpokenLanguages.Count == 1);
        Assert.Equal(SpokenLanguage.Japanese, client.SpokenLanguages[0]);

        // 末尾ウィンドウから日本語を追い出し、ラテン 1 語だけを残す。
        client.PublishSourceDelta("................");
        await Task.Delay(40);
        client.PublishSourceDelta("Tokyo");
        await Task.Delay(80);

        Assert.Equal([SpokenLanguage.Japanese], client.SpokenLanguages);
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

    // Given: Listening 中に dual の tuning 更新が RealtimeTranslationException を投げる
    // When: ApplyTuningChangeAsync する
    // Then: 例外を握りつぶして Listening を維持し、Error へ落とさない
    [Fact]
    public async Task ApplyTuningChangeKeepsListeningWhenUpdateFails()
    {
        var client = new FakeDualClient { ThrowRealtimeOnUpdateTuning = true };
        using var session = NewSession(client);
        string? message = null;
        session.MessageEncountered += (_, value) => message = value;

        await session.StartAsync();
        await WaitUntilAsync(() => session.State == TranslationState.Listening);

        await session.ApplyTuningChangeAsync();
        await Task.Delay(40);

        Assert.Equal(TranslationState.Listening, session.State);
        Assert.Null(message);
        Assert.Equal(1, client.UpdateTranscriptionTuningCount);
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

    // Given: Listening 中にイベント channel が完了する
    // When: ConsumeEventsAsync が終端を検出する
    // Then: recoverable として再接続し、新しい epoch で再開する
    [Fact]
    public async Task EventChannelCompletionTriggersReconnect()
    {
        var client = new FakeDualClient();
        using var session = NewSession(client);
        await session.StartAsync();
        await WaitUntilAsync(() => session.State == TranslationState.Listening);

        client.Complete();
        await WaitUntilAsync(() => client.StartCount >= 2);

        Assert.True(client.StartCount >= 2);
        await session.StopAsync();
    }

    // Given: Listening 中に音声 frame channel が完了する
    // When: FeedAudioAsync が終端を検出する
    // Then: recoverable として再接続する（次 Start で frame channel を張り直す）
    [Fact]
    public async Task AudioFrameChannelCompletionTriggersReconnect()
    {
        var client = new FakeDualClient();
        var audio = new FakeAudioCapture();
        using var session = NewSession(client, audio: audio);
        await session.StartAsync();
        await WaitUntilAsync(() => session.State == TranslationState.Listening);

        audio.Complete();
        await WaitUntilAsync(() => client.StartCount >= 2);

        Assert.True(client.StartCount >= 2);
        await session.StopAsync();
    }

    // Given: idle finalize 前の完全ペアと、再接続 Start が連続失敗する dual
    // When: transport error 後に再接続上限へ到達する
    // Then: Error 遷移前に ShouldFinalize し、オプトイン字幕記録の欠落を防ぐ
    [Fact]
    public async Task MaxReconnectAttemptsFinalizesCompletePairBeforeError()
    {
        var client = new FakeDualClient();
        using var session = NewSession(client);
        var updates = new List<RealtimeSubtitleUpdate>();
        var notificationOrder = new List<string>();
        string? message = null;
        session.SubtitleUpdated += (_, update) =>
        {
            lock (updates)
            {
                updates.Add(update);
                if (update.ShouldFinalize)
                {
                    notificationOrder.Add("ShouldFinalize");
                }
            }
        };
        session.StateChanged += (_, state) =>
        {
            if (state == TranslationState.Error)
            {
                lock (updates)
                {
                    notificationOrder.Add("Error");
                }
            }
        };
        session.MessageEncountered += (_, value) => message = value;

        await session.StartAsync();
        await WaitUntilAsync(() => session.State == TranslationState.Listening);

        client.PublishSourceDelta("再接続上限前の完全ペア");
        await WaitUntilAsync(() => client.SpokenLanguages.Count > 0);
        client.PublishTranslationDelta(
            RealtimeTranslationOutputLanguage.English,
            "Complete pair before max reconnect");
        await WaitUntilAsync(() =>
        {
            lock (updates)
            {
                return updates.Exists(update =>
                    update.TranslatedText.Length > 0 && !update.ShouldFinalize);
            }
        });

        // 成功再接続では BeginNewEpoch 前に flush されるが、Start 失敗の連続では
        // 上限到達時の FlushPendingFinalizeIfNeeded だけが最後の機会になる。
        client.RemainingStartFailures = InterpretationSession.MaxReconnectAttempts;
        client.PublishTransportError();
        await WaitUntilAsync(() => session.State == TranslationState.Error);

        // 初回 Start 成功 + MaxReconnectAttempts 回の失敗 Start を消費したこと。
        Assert.Equal(0, client.RemainingStartFailures);
        Assert.Equal(1 + InterpretationSession.MaxReconnectAttempts, client.StartCount);
        Assert.Equal("再接続上限に達しました", message);
        RealtimeSubtitleUpdate finalized;
        lock (updates)
        {
            Assert.Contains("ShouldFinalize", notificationOrder);
            Assert.Contains("Error", notificationOrder);
            Assert.True(
                notificationOrder.IndexOf("ShouldFinalize") <
                notificationOrder.IndexOf("Error"));
            finalized = updates.Find(update => update.ShouldFinalize);
        }

        Assert.Equal("再接続上限前の完全ペア", finalized.SourceText);
        Assert.Equal("Complete pair before max reconnect", finalized.TranslatedText);
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

    // Given: 停止時点では字幕がまだ無く、commit/session.close の drain で完全ペアが届く
    // When: 利用者が録音を停止する
    // Then: close drain の原文+訳文を取り込んで ShouldFinalize する（字幕記録欠落を防ぐ）
    [Fact]
    public async Task StopIngestsCompletePairPublishedDuringGracefulClose()
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

        client.OnCloseGracefully = () =>
        {
            client.PublishSourceDelta("停止時の最終原文");
            client.PublishTranslationDelta(
                RealtimeTranslationOutputLanguage.English,
                "Final source at stop");
            client.Complete();
            return Task.CompletedTask;
        };

        await session.StopAsync();

        Assert.Equal(TranslationState.Idle, session.State);
        RealtimeSubtitleUpdate finalized;
        lock (updates)
        {
            finalized = updates.Find(update => update.ShouldFinalize);
        }

        Assert.Equal("停止時の最終原文", finalized.SourceText);
        Assert.Equal("Final source at stop", finalized.TranslatedText);
    }

    // Given: close 自体は失敗するが、drain 済みの完全ペアは channel に残っている
    // When: 利用者が録音を停止する
    // Then: ForceClose 後も drain イベントを取り込んで ShouldFinalize する
    [Fact]
    public async Task StopIngestsCloseDrainEventsEvenWhenGracefulCloseFails()
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

        client.OnCloseGracefully = () =>
        {
            client.PublishSourceDelta("失敗経路の最終原文");
            client.PublishTranslationDelta(
                RealtimeTranslationOutputLanguage.English,
                "Final source on close failure");
            // Complete せずに失敗させる。ForceClose が channel を閉じる前提。
            throw new InvalidOperationException("graceful close failed");
        };

        await session.StopAsync();

        Assert.Equal(TranslationState.Idle, session.State);
        Assert.Equal(1, client.CloseGracefullyCallCount);
        Assert.True(client.ForceCloseCallCount >= 1);
        RealtimeSubtitleUpdate finalized;
        lock (updates)
        {
            finalized = updates.Find(update => update.ShouldFinalize);
        }

        Assert.Equal("失敗経路の最終原文", finalized.SourceText);
        Assert.Equal("Final source on close failure", finalized.TranslatedText);
    }

    // Given: idle finalize 前の完全な原文+訳文ペア
    // When: StopAsync を経ずに Dispose される（OnExit / プロセス終了相当）
    // Then: 破棄前に ShouldFinalize が発行され、オプトイン字幕記録へ届く
    [Fact]
    public async Task DisposeFinalizesCompletePairWithoutStop()
    {
        var client = new FakeDualClient();
        var session = NewSession(client);
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

        client.PublishSourceDelta("終了前の完全ペア");
        await WaitUntilAsync(() => client.SpokenLanguages.Count > 0);
        client.PublishTranslationDelta(RealtimeTranslationOutputLanguage.English, "Complete pair before exit");
        await WaitUntilAsync(() =>
        {
            lock (updates)
            {
                return updates.Exists(update =>
                    update.TranslatedText.Length > 0 && !update.ShouldFinalize);
            }
        });

        session.Dispose();

        RealtimeSubtitleUpdate finalized;
        lock (updates)
        {
            finalized = updates.Find(update => update.ShouldFinalize);
        }

        Assert.Equal("終了前の完全ペア", finalized.SourceText);
        Assert.Equal("Complete pair before exit", finalized.TranslatedText);
    }

    // Given: 完全ペア確定後に次イベントが generation 確認済み・Ingest 前で停止している
    // When: その間に Dispose して取り込みをフェンスする
    // Then: 既存完全ペアは ShouldFinalize され、停止中イベントは assembler を更新しない
    [Fact]
    public async Task DisposeFencesInFlightIngestBeforeFlushingCompletePair()
    {
        var client = new FakeDualClient();
        var session = NewSession(client);
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

        client.PublishSourceDelta("フェンス前の完全ペア");
        await WaitUntilAsync(() => client.SpokenLanguages.Count > 0);
        client.PublishTranslationDelta(RealtimeTranslationOutputLanguage.English, "Complete pair before fence");
        await WaitUntilAsync(() =>
        {
            lock (updates)
            {
                return updates.Exists(update =>
                    update.TranslatedText.Length > 0 && !update.ShouldFinalize);
            }
        });

        using var enteredHook = new ManualResetEventSlim(false);
        using var releaseHook = new ManualResetEventSlim(false);
        session.BeforeAssemblerIngestForTests = () =>
        {
            enteredHook.Set();
            if (!releaseHook.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("Dispose fence test hook was not released");
            }
        };

        client.PublishSourceDelta("Dispose中に取り込ませない原文");
        Assert.True(enteredHook.Wait(TimeSpan.FromSeconds(5)));

        session.Dispose();
        releaseHook.Set();

        // consumer が stale ingest を捨てて戻るのを待つ。
        await Task.Delay(100);

        RealtimeSubtitleUpdate finalized;
        lock (updates)
        {
            finalized = updates.Find(update => update.ShouldFinalize);
            Assert.DoesNotContain(
                updates,
                update => update.SourceText.Contains("取り込ませない", StringComparison.Ordinal));
        }

        Assert.Equal("フェンス前の完全ペア", finalized.SourceText);
        Assert.Equal("Complete pair before fence", finalized.TranslatedText);
    }

    // Given: 訳文がまだ無い原文だけのセグメント
    // When: StopAsync を経ずに Dispose される
    // Then: 不完全ペアを ShouldFinalize しない（字幕記録へゴミを書かない）
    [Fact]
    public async Task DisposeDoesNotFinalizeIncompleteSourceOnlyPair()
    {
        var client = new FakeDualClient();
        var session = NewSession(client);
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

        client.PublishSourceDelta("終了前の原文だけ");
        await WaitUntilAsync(() => client.SpokenLanguages.Count > 0);
        await WaitUntilAsync(() =>
        {
            lock (updates)
            {
                return updates.Exists(update => update.SourceText.Length > 0);
            }
        });

        session.Dispose();
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

    // Given: 訳文がまだ無い原文だけのセグメント
    // When: 利用者が録音を停止する
    // Then: 不完全ペアを ShouldFinalize せず Idle へ戻る
    [Fact]
    public async Task StopDoesNotFinalizeIncompleteSourceOnlyPair()
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

        client.PublishSourceDelta("停止前の原文だけ");
        await WaitUntilAsync(() => client.SpokenLanguages.Count > 0);
        await WaitUntilAsync(() =>
        {
            lock (updates)
            {
                return updates.Exists(update => update.SourceText.Length > 0);
            }
        });

        await session.StopAsync();

        Assert.Equal(TranslationState.Idle, session.State);
        lock (updates)
        {
            Assert.DoesNotContain(updates, update => update.ShouldFinalize);
        }
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

    // Given: Listening 中のセッション
    // When: キー断片を含む invalid_api_key エラーが届く
    // Then: 認証エラー文言になり、sk- や原文メッセージは MessageEncountered へ出ない
    [Fact]
    public async Task InvalidApiKeyRuntimeErrorDoesNotLeakKeyMaterial()
    {
        var client = new FakeDualClient();
        using var session = NewSession(client);
        string? message = null;
        session.MessageEncountered += (_, value) => message = value;

        await session.StartAsync();
        await WaitUntilAsync(() => session.State == TranslationState.Listening);

        client.PublishServerError("Incorrect API key provided: sk-leak-example", "invalid_api_key");
        await WaitUntilAsync(() => session.State == TranslationState.Error);
        await WaitUntilAsync(() => message is not null);

        Assert.Equal("OpenAI APIキーが無効です", message);
        Assert.DoesNotContain("sk-", message, StringComparison.Ordinal);
        Assert.Equal(1, client.StartCount);
    }

    // Given: Listening 中のセッション
    // When: code は非認証だが文言に API キー断片が含まれる
    // Then: 汎用エラー文言に置換され秘密情報は出ない
    [Fact]
    public async Task NonAuthServerErrorRedactsApiKeyLikePayload()
    {
        var client = new FakeDualClient();
        using var session = NewSession(client);
        string? message = null;
        session.MessageEncountered += (_, value) => message = value;

        await session.StartAsync();
        await WaitUntilAsync(() => session.State == TranslationState.Listening);

        client.PublishServerError("Provider echo included sk-should-not-appear", "server_error");
        await WaitUntilAsync(() => session.State == TranslationState.Error);
        await WaitUntilAsync(() => message is not null);

        Assert.Equal(RealtimeTranslationException.GenericServerMessage, message);
        Assert.DoesNotContain("sk-", message, StringComparison.Ordinal);
        Assert.Equal(1, client.StartCount);
    }

    // Given: Listening 中のセッション
    // When: auth 部分文字列を含むが非認証のエラーが届く
    // Then: 無効 API キー扱いではなく、サーバー文言経路になる
    [Fact]
    public async Task AuthorityLikeServerErrorIsNotTreatedAsInvalidApiKey()
    {
        var client = new FakeDualClient();
        using var session = NewSession(client);
        string? message = null;
        session.MessageEncountered += (_, value) => message = value;

        await session.StartAsync();
        await WaitUntilAsync(() => session.State == TranslationState.Listening);

        client.PublishServerError(
            "certificate authority rejected the peer (code 4010)",
            "authority_mismatch");
        await WaitUntilAsync(() => session.State == TranslationState.Error);
        await WaitUntilAsync(() => message is not null);

        Assert.NotEqual("OpenAI APIキーが無効です", message);
        Assert.Equal("certificate authority rejected the peer (code 4010)", message);
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
        Func<RealtimeSessionTuning>? tuningProvider = null,
        FakeAudioCapture? audio = null) =>
        new(
            new FakeApiKeyStore(apiKey),
            audio ?? new FakeAudioCapture(),
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
        private readonly object _sync = new();
        private Channel<ReadOnlyMemory<byte>> _frames =
            Channel.CreateUnbounded<ReadOnlyMemory<byte>>();

        public int StartCallCount { get; private set; }

        public int StopCallCount { get; private set; }

        public bool IsRunning { get; private set; }

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
                StartCallCount += 1;
                IsRunning = true;
                // 再接続時に完了済み channel を使い回すと即 recoverable になるため張り直す。
                if (_frames.Reader.Completion.IsCompleted)
                {
                    _frames = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
                }
            }

            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            lock (_sync)
            {
                StopCallCount += 1;
                IsRunning = false;
            }

            return Task.CompletedTask;
        }

        public void Complete()
        {
            lock (_sync)
            {
                _frames.Writer.TryComplete();
            }
        }
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

        public int CloseGracefullyCallCount { get; private set; }

        public int ForceCloseCallCount { get; private set; }

        public RealtimeSessionTuning? LastTuning { get; private set; }

        public bool ThrowOnNextStart { get; set; }

        /// <summary>UpdateTranscriptionTuningAsync で RealtimeTranslationException を投げる。</summary>
        public bool ThrowRealtimeOnUpdateTuning { get; set; }

        /// <summary>StartAsync を指定回数だけ失敗させる（再接続上限テスト用）。</summary>
        public int RemainingStartFailures { get; set; }

        /// <summary>CloseGracefully 時に close drain イベントを流すテスト用フック。</summary>
        public Func<Task>? OnCloseGracefully { get; set; }

        /// <summary>StartAsync 入口で待つゲート（capture 順序・Stop 競合用）。</summary>
        public TaskCompletionSource? StartGate { get; set; }

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

        public async Task StartAsync(
            string apiKey,
            RealtimeSessionTuning tuning,
            CancellationToken cancellationToken = default)
        {
            Task? gateTask;
            lock (_sync)
            {
                StartCount += 1;
                if (ThrowOnNextStart)
                {
                    ThrowOnNextStart = false;
                    throw new InvalidOperationException("unexpected device failure");
                }

                if (RemainingStartFailures > 0)
                {
                    RemainingStartFailures -= 1;
                    throw new InvalidOperationException("repeated device failure");
                }

                gateTask = StartGate?.Task;
            }

            if (gateTask is not null)
            {
                await gateTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            lock (_sync)
            {
                _epoch += 1;
                _spokenLanguages.Clear();
                ResetAudioRoutingCount = 0;
                UpdateTranscriptionTuningCount = 0;
                LastTuning = null;
                OnCloseGracefully = null;
                _events = Channel.CreateUnbounded<RealtimeTranslationStreamEvent>();
            }
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
                if (ThrowRealtimeOnUpdateTuning)
                {
                    throw new RealtimeTranslationException(
                        RealtimeTranslationErrorKind.SessionUpdateTimeout);
                }

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

        public async Task CloseGracefullyAsync(CancellationToken cancellationToken = default)
        {
            CloseGracefullyCallCount += 1;
            if (OnCloseGracefully is { } hook)
            {
                await hook().ConfigureAwait(false);
                return;
            }

            Complete();
        }

        public Task ForceCloseAsync()
        {
            ForceCloseCallCount += 1;
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

        public void Complete()
        {
            lock (_sync)
            {
                _events.Writer.TryComplete();
            }
        }

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
    }
}
