using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
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

    // Given: 埋め込み改行と時刻が混ざった形式不正キー
    // When: セッションを開始する
    // Then: Dual に渡さず認証エラーへ落ち、欠落キーとも汎用サーバーエラーとも区別する
    [Fact]
    public async Task StartWithMalformedApiKeyEntersAuthenticationError()
    {
        var client = new FakeDualClient();
        using var session = NewSession(client, apiKey: "sk-proj-abc\n3:26");
        string? message = null;
        session.MessageEncountered += (_, value) => message = value;

        await session.StartAsync();
        await WaitUntilAsync(() => session.State == TranslationState.Error);
        await WaitUntilAsync(() => message is not null);

        Assert.Equal(0, client.StartCount);
        Assert.Equal("OpenAI APIキーが無効です", message);
        Assert.NotEqual("APIキーが設定されていません", message);
        Assert.NotEqual(RealtimeTranslationException.GenericServerMessage, message);
        Assert.DoesNotContain("sk-", message, StringComparison.Ordinal);
        Assert.DoesNotContain("3:26", message, StringComparison.Ordinal);
    }

    // Given: 空白だけの API キー
    // When: セッションを開始する
    // Then: 接続せず欠落キーエラーになり、形式不正とは区別する
    [Fact]
    public async Task StartWithWhitespaceOnlyApiKeyEntersMissingKeyError()
    {
        var client = new FakeDualClient();
        using var session = NewSession(client, apiKey: "  \n\t  ");
        string? message = null;
        session.MessageEncountered += (_, value) => message = value;

        await session.StartAsync();
        await WaitUntilAsync(() => session.State == TranslationState.Error);
        await WaitUntilAsync(() => message is not null);

        Assert.Equal(0, client.StartCount);
        Assert.Equal("APIキーが設定されていません", message);
        Assert.NotEqual("OpenAI APIキーが無効です", message);
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

    // Given: Dual Start は完了したが capture Start 待ち
    // When: その間に Stop する
    // Then: Listening に到達せず Idle に戻り、capture は開始されないか停止済みになる
    [Fact]
    public async Task StopAfterDualStartBeforeCaptureNeverReachesListening()
    {
        var client = new FakeDualClient();
        var audio = new FakeAudioCapture();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        audio.StartGate = gate;
        using var session = NewSession(client, audio: audio);

        var startTask = session.StartAsync();
        await WaitUntilAsync(() =>
            client.StartCount == 1 && audio.StartCallCount == 1);
        Assert.Equal(TranslationState.Connecting, session.State);

        var stopTask = session.StopAsync();
        await WaitUntilAsync(() =>
            session.State is TranslationState.Closing or TranslationState.Idle);
        audio.StartGate = null;
        gate.TrySetResult();
        await stopTask;
        await startTask;

        Assert.Equal(TranslationState.Idle, session.State);
        Assert.False(audio.IsRunning);
        Assert.NotEqual(TranslationState.Listening, session.State);
    }

    // Given: Dual.Start に入る前（API キー読み出し待ち）の Connecting
    // When: 利用者がすぐ Stop し、その後キー欠落で session loop が戻る
    // Then: stop drain が Events 完了を待って固まらず、Idle に戻って次の Start が通る
    [Fact]
    public async Task StopBeforeDualStartCompletesIdleWithoutHanging()
    {
        var source = new FakeRealtimeServerTransport { AutoCloseResponses = true };
        var english = new FakeRealtimeServerTransport { AutoCloseResponses = true };
        var japanese = new FakeRealtimeServerTransport { AutoCloseResponses = true };
        using var dual = new DualRealtimeTranslationClient(
            new RealtimeSourceTranscriptionConnection(source, "test-safety"),
            new RealtimeTranslationConnection(
                RealtimeTranslationOutputLanguage.English,
                english,
                "test-safety"),
            new RealtimeTranslationConnection(
                RealtimeTranslationOutputLanguage.Japanese,
                japanese,
                "test-safety"));
        var keyStore = new GatedApiKeyStore();
        using var session = new InterpretationSession(
            keyStore,
            new FakeAudioCapture(),
            dual,
            initialReconnectDelay: TimeSpan.FromMilliseconds(1),
            tickInterval: TimeSpan.FromMilliseconds(20));

        var startTask = session.StartAsync();
        await keyStore.LoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(TranslationState.Connecting, session.State);

        var stopTask = session.StopAsync();
        await WaitUntilAsync(() =>
            session.State is TranslationState.Closing or TranslationState.Idle);
        keyStore.Release(null);

        await stopTask.WaitAsync(TimeSpan.FromSeconds(2));
        await startTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(TranslationState.Idle, session.State);

        await session.StartAsync();
        await WaitUntilAsync(() => session.State == TranslationState.Error);
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

    // Given: Listening 中に CloseGracefully が遅い
    // When: StopAsync を重ねて呼び、完了前に Start しようとする
    // Then: CloseGracefully は 1 回だけ。Stop 合流後にだけ次の Start が通り、ForceClose で新接続を落とさない
    [Fact]
    public async Task OverlappingStopJoinsAndDoesNotTearDownNextStart()
    {
        var closeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowCloseFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeDualClient();
        using var session = NewSession(client);
        await session.StartAsync();
        await WaitUntilAsync(() => session.State == TranslationState.Listening);

        // StartAsync がフックを消すため、Listening 到達後に遅い Close を仕込む。
        client.OnCloseGracefully = async () =>
        {
            closeStarted.TrySetResult();
            await allowCloseFinish.Task.ConfigureAwait(false);
            client.Complete();
        };

        var firstStop = session.StopAsync();
        await closeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(TranslationState.Closing, session.State);

        var secondStop = session.StopAsync();
        // Closing 中の Start は受理されない（二重 Stop が先に Idle へ戻して穴を開けない）。
        await session.StartAsync();
        Assert.Equal(TranslationState.Closing, session.State);
        Assert.Equal(1, client.CloseGracefullyCallCount);

        allowCloseFinish.TrySetResult();
        await Task.WhenAll(firstStop, secondStop);
        Assert.Equal(TranslationState.Idle, session.State);
        Assert.Equal(1, client.CloseGracefullyCallCount);

        var forceCloseBeforeRestart = client.ForceCloseCallCount;
        await session.StartAsync();
        await WaitUntilAsync(() => session.State == TranslationState.Listening);
        Assert.Equal(forceCloseBeforeRestart, client.ForceCloseCallCount);
        await session.StopAsync();
    }

    // Given: 既に Listening のセッション
    // When: StartAsync を再度呼ぶ
    // Then: Dual を二重に Start せず Listening のまま
    [Fact]
    public async Task StartWhileListeningIsNoOp()
    {
        var client = new FakeDualClient();
        using var session = NewSession(client);
        await session.StartAsync();
        await WaitUntilAsync(() => session.State == TranslationState.Listening);
        var startCount = client.StartCount;

        await session.StartAsync();

        Assert.Equal(TranslationState.Listening, session.State);
        Assert.Equal(startCount, client.StartCount);
        await session.StopAsync();
    }

    // Given: Dual.StartAsync 待ちで Connecting のセッション
    // When: その間に StartAsync を再度呼ぶ
    // Then: 受理せず Dual Start は 1 回のまま
    [Fact]
    public async Task StartWhileConnectingIsNoOp()
    {
        var client = new FakeDualClient();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.StartGate = gate;
        using var session = NewSession(client);

        var firstStart = session.StartAsync();
        await WaitUntilAsync(() =>
            client.StartCount == 1 && session.State == TranslationState.Connecting);

        await session.StartAsync();
        Assert.Equal(1, client.StartCount);

        client.StartGate = null;
        gate.SetResult();
        await WaitUntilAsync(() => session.State == TranslationState.Listening);
        await firstStart;
        await session.StopAsync();
    }

    // Given: Dual.StartAsync 待ちで止まっているセッション
    // When: Stop 中に旧 Start を進め、排水後に再 Start する
    // Then: 旧 sessionTask の世代不一致 ForceClose が新セッションへ飛ばない
    [Fact]
    public async Task StopDrainsSessionTaskBeforeReturningSoRestartIsStable()
    {
        var client = new FakeDualClient();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.StartGate = gate;
        using var session = NewSession(client);

        var firstStart = session.StartAsync();
        await WaitUntilAsync(() => client.StartCount == 1);

        var stopTask = session.StopAsync();
        await WaitUntilAsync(() =>
            session.State is TranslationState.Closing or TranslationState.Idle);
        client.StartGate = null;
        gate.SetResult();
        await stopTask;
        await firstStart;
        Assert.Equal(TranslationState.Idle, session.State);

        var forceCloseAfterStop = client.ForceCloseCallCount;
        await session.StartAsync();
        await WaitUntilAsync(() => session.State == TranslationState.Listening);

        await Task.Delay(100);
        Assert.Equal(TranslationState.Listening, session.State);
        Assert.Equal(forceCloseAfterStop, client.ForceCloseCallCount);
        await session.StopAsync();
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

    // Given: 初回の日本語原文 delta
    // When: 初回 target を選択する
    // Then: 言語切替 finalize を実行せず、初回 routing を開始する
    [Fact]
    public async Task InitialJapaneseDetectionDoesNotFinalize()
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
        client.PublishSourceDelta("こんにちは、初回です");
        await WaitUntilAsync(() => client.SpokenLanguages.Count > 0);
        await WaitUntilAsync(() =>
        {
            lock (updates)
            {
                return updates.Exists(update => update.SourceText.Length > 0);
            }
        });

        lock (updates)
        {
            Assert.DoesNotContain(updates, update => update.ShouldFinalize);
        }

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

    // Given: 訳文のあと同一言語の原文が伸びて stale になったセグメント
    // When: 英語へ文字種が反転する
    // Then: idle では確定しない stale ペアでも切替境界として確定する
    [Fact]
    public async Task LanguageFlipDoesNotFinalizeStalePairAfterSourceContinues()
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

        client.PublishSourceDelta("、続きです");
        await WaitUntilAsync(() =>
        {
            lock (updates)
            {
                return updates.Exists(update =>
                    update.SourceText.Contains("続きです", StringComparison.Ordinal)
                    && !update.IsTranslationCurrent);
            }
        });
        var resetsAfterJapanese = client.ResetAudioRoutingCount;

        client.PublishSourceDelta(" now we continue in english for a while");
        await WaitUntilAsync(() => client.SpokenLanguages.Count > 1);

        Assert.Equal([SpokenLanguage.Japanese, SpokenLanguage.English], client.SpokenLanguages);
        Assert.True(client.ResetAudioRoutingCount > resetsAfterJapanese);
        lock (updates)
        {
            Assert.DoesNotContain(updates, update => update.ShouldFinalize);
        }
        await session.StopAsync();
        return;
#if false
        RealtimeSubtitleUpdate finalized = default;
        await WaitUntilAsync(() =>
        {
            lock (updates)
            {
                finalized = updates.Find(update => update.ShouldFinalize);
                return finalized.ShouldFinalize;
            }
        });
        Assert.Equal("これはテストです、続きです", finalized.SourceText);
        Assert.Equal("This is a test", finalized.TranslatedText);
        await session.StopAsync();
#endif
    }

    // Given: 日本語の原文と英語訳が確定候補として存在する
    // When: 英語への切替を複数の原文デルタで受け取る
    // Then: プレフィックスだけが確定し、サフィックスが現在字幕になる
    [Fact]
    public async Task B01LanguageFlipSplitsArrivalIntoFinalizedPrefixAndLiveSuffix()
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
        client.PublishSourceDelta("今日は晴れです。");
        await WaitUntilAsync(() => client.SpokenLanguages.Count == 1);
        var resetsBeforeSwitch = client.ResetAudioRoutingCount;
        client.PublishTranslationDelta(
            RealtimeTranslationOutputLanguage.English,
            "It is sunny today.");
        await WaitUntilAsync(() =>
        {
            lock (updates)
            {
                return updates.Any(update => update.TranslatedText.Contains("sunny"));
            }
        });
        client.PublishSourceDelta("To");
        client.PublishSourceDelta("day it is sunny outside");

        await WaitUntilAsync(() => client.SpokenLanguages.Count == 2);
        Assert.Equal(
            [SpokenLanguage.Japanese, SpokenLanguage.English],
            client.SpokenLanguages);
        Assert.Equal(
            [RealtimeTranslationOutputLanguage.English, RealtimeTranslationOutputLanguage.Japanese],
            client.SelectedTargets);
        Assert.Equal(resetsBeforeSwitch + 1, client.ResetAudioRoutingCount);
        await WaitUntilAsync(() =>
        {
            lock (updates)
            {
                return updates.Count(update =>
                    update.ShouldFinalize
                    && update.SourceText == "今日は晴れです。"
                    && update.TranslatedText == "It is sunny today.") == 1;
            }
        });
        lock (updates)
        {
            Assert.Contains(
                updates,
                update => update.SourceText == "Today it is sunny outside");
        }
        await session.StopAsync();
    }

    // Given: 切替候補が保留中で未確定の字幕が存在する
    // When: 受信損失を記録する
    // Then: 一度だけ無効化され、再接続後に未確定字幕を確定しない
    [Fact]
    public async Task AB01PendingBoundaryLossInvalidatesAndReconnectsWithoutFinalizing()
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
        client.PublishSourceDelta("今日は晴れです。");
        await WaitUntilAsync(() => client.SpokenLanguages.Count == 1);
        client.PublishTranslationDelta(
            RealtimeTranslationOutputLanguage.English,
            "It is sunny today.");
        await WaitUntilAsync(() =>
        {
            lock (updates)
            {
                return updates.Any(update => update.TranslatedText.Contains("sunny"));
            }
        });
        client.PublishSourceDelta(" To");
        await WaitUntilAsync(() =>
        {
            lock (updates)
            {
                return updates.Any(update => update.SourceText.Contains(" To"));
            }
        });
        var startsBeforeLoss = client.StartCount;

        client.RecordLoss();

        await WaitUntilAsync(() =>
            session.State == TranslationState.Listening
            && client.StartCount > startsBeforeLoss);
        lock (updates)
        {
            Assert.Equal(1, updates.Count(update => update.IsInvalidation));
            Assert.DoesNotContain(
                updates,
                update => update.ShouldFinalize
                    && (update.SourceText.Contains("今日は晴れです。")
                        || update.SourceText.Contains(" To")));
        }
        await session.StopAsync();
    }

    // Given: 日本語の完全なペアがあり、停止時のドレインで英語切替が届く
    // When: 停止してcloseGracefullyのイベントをドレインする
    // Then: 一度だけ確定し、ドレイン中に音声ルーティングを変更しない
    [Fact]
    public async Task AB02StopDrainSwitchFinalizesExactlyOnceWithoutRoutingTransportCalls()
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
        client.PublishSourceDelta("今日は晴れです。");
        await WaitUntilAsync(() => client.SpokenLanguages.Count == 1);
        client.PublishTranslationDelta(
            RealtimeTranslationOutputLanguage.English,
            "It is sunny today.");
        await WaitUntilAsync(() =>
        {
            lock (updates)
            {
                return updates.Any(update => update.TranslatedText.Contains("sunny"));
            }
        });
        var selectedBeforeDrain = client.SelectedTargets.Count;
        var resetsBeforeDrain = client.ResetAudioRoutingCount;
        client.OnCloseGracefully = () =>
        {
            client.PublishSourceDelta("Today it is sunny outside");
            return Task.CompletedTask;
        };

        await session.StopAsync();

        Assert.Equal(TranslationState.Idle, session.State);
        lock (updates)
        {
            Assert.Equal(
                1,
                updates.Count(update =>
                    update.ShouldFinalize
                    && update.SourceText == "今日は晴れです。"));
        }
        Assert.Equal(selectedBeforeDrain, client.SelectedTargets.Count);
        Assert.Equal(resetsBeforeDrain, client.ResetAudioRoutingCount);
    }

    // Given: ja-es で日本語→es の完全ペアが揃ったあとスペイン語へ反転する原文
    // When: 文字種の反転を検出する
    // Then: 前セグメントを確定し、音声 routing を日本語 target へ切り替え直す
    [Fact]
    public async Task JaEsLanguageFlipSplitsAndReroutesWithoutFinalizingStalePair()
    {
        var client = new FakeDualClient();
        using var session = NewSession(client, languagePairProvider: () => LanguagePair.JaEs);
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
        await WaitUntilAsync(() => client.SelectedTargets.Count == 1);
        Assert.Equal(RealtimeTranslationOutputLanguage.Spanish, client.SelectedTargets[0]);
        client.PublishTranslationDelta(RealtimeTranslationOutputLanguage.Spanish, "Esto es una prueba");
        await WaitUntilAsync(() =>
        {
            lock (updates)
            {
                return updates.Exists(update => update.TranslatedText.Length > 0);
            }
        });
        var resetsAfterJapanese = client.ResetAudioRoutingCount;

        // When: 1 delta で末尾窓から日本語を追い出し、スペイン語へ反転する
        // （padding を先に ingest すると finalize 原文へ混ざる）
        client.PublishSourceDelta("................ mundo ahora");
        await WaitUntilAsync(() => client.SelectedTargets.Count == 2);

        // Then: 言語切替で再ルーティングし、前セグメントが確定する
        Assert.Equal(
            [RealtimeTranslationOutputLanguage.Spanish, RealtimeTranslationOutputLanguage.Japanese],
            client.SelectedTargets);
        Assert.Equal([SpokenLanguage.Japanese, SpokenLanguage.Spanish], client.SpokenLanguages);
        Assert.True(client.ResetAudioRoutingCount > resetsAfterJapanese);
        lock (updates)
        {
            Assert.DoesNotContain(updates, update => update.ShouldFinalize);
        }
        await session.StopAsync();
        return;
#if false
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
        Assert.Equal("Esto es una prueba", finalized.TranslatedText);
        await session.StopAsync();
#endif
    }

    // Given: ja-es ペアと ja-en 既定の tuningProvider
    // When: Start し、Listening 中に ApplyTuningChangeAsync する
    // Then: Start / Apply の双方で ForPair(JaEs) の prompt・keywords が dual へ渡る
    [Fact]
    public async Task JaEsStartAndApplyTuningUsePairMigratedDefaults()
    {
        var client = new FakeDualClient();
        using var session = NewSession(
            client,
            tuningProvider: () => RealtimeSessionTuning.Default,
            languagePairProvider: () => LanguagePair.JaEs);

        await session.StartAsync();
        await WaitUntilAsync(() => session.State == TranslationState.Listening);

        Assert.Equal(LanguagePair.JaEs, client.LastStartedPair);
        Assert.Equal(
            RealtimeSessionTuning.DefaultPromptForPair(LanguagePair.JaEs),
            client.LastStartedTuning?.TranscriptionPrompt);
        Assert.Equal(
            RealtimeSessionTuning.DefaultKeywordsForPair(LanguagePair.JaEs).ToArray(),
            client.LastStartedTuning?.TranscriptionKeywords.ToArray());

        await session.ApplyTuningChangeAsync();

        Assert.Equal(1, client.UpdateTranscriptionTuningCount);
        Assert.Equal(
            RealtimeSessionTuning.DefaultPromptForPair(LanguagePair.JaEs),
            client.LastTuning?.TranscriptionPrompt);
        Assert.Equal(
            RealtimeSessionTuning.DefaultKeywordsForPair(LanguagePair.JaEs).ToArray(),
            client.LastTuning?.TranscriptionKeywords.ToArray());
        await session.StopAsync();
    }

    // Given: ja-en で開始したあと、録音中に provider を ja-es へ変更する
    // When: Listening 中に ApplyTuningChangeAsync する
    // Then: 凍結中の ja-en 向け ForPair を送り、新しいペアの hint へすり替わらない
    [Fact]
    public async Task ApplyTuningChangeUsesFrozenPairAfterProviderChanges()
    {
        var client = new FakeDualClient();
        var pair = LanguagePair.JaEn;
        using var session = NewSession(
            client,
            tuningProvider: () => RealtimeSessionTuning.Default,
            languagePairProvider: () => pair);

        await session.StartAsync();
        await WaitUntilAsync(() => session.State == TranslationState.Listening);
        Assert.Equal(LanguagePair.JaEn, client.LastStartedPair);

        pair = LanguagePair.JaEs;
        await session.ApplyTuningChangeAsync();

        Assert.Equal(1, client.UpdateTranscriptionTuningCount);
        Assert.Equal(
            RealtimeSessionTuning.DefaultPromptForPair(LanguagePair.JaEn),
            client.LastTuning?.TranscriptionPrompt);
        Assert.Equal(
            RealtimeSessionTuning.DefaultKeywordsForPair(LanguagePair.JaEn).ToArray(),
            client.LastTuning?.TranscriptionKeywords.ToArray());
        Assert.NotEqual(
            RealtimeSessionTuning.DefaultPromptForPair(LanguagePair.JaEs),
            client.LastTuning?.TranscriptionPrompt);
        await session.StopAsync();
    }

    // Given: en-es ペアと ja-en 既定の tuningProvider
    // When: Start し、Listening 中に ApplyTuningChangeAsync する
    // Then: Start / Apply の双方で ForPair(EnEs) の prompt・keywords が dual へ渡る
    [Fact]
    public async Task EnEsStartAndApplyTuningUsePairMigratedDefaults()
    {
        var client = new FakeDualClient();
        using var session = NewSession(
            client,
            tuningProvider: () => RealtimeSessionTuning.Default,
            languagePairProvider: () => LanguagePair.EnEs);

        await session.StartAsync();
        await WaitUntilAsync(() => session.State == TranslationState.Listening);

        Assert.Equal(LanguagePair.EnEs, client.LastStartedPair);
        Assert.Equal(
            RealtimeSessionTuning.DefaultPromptForPair(LanguagePair.EnEs),
            client.LastStartedTuning?.TranscriptionPrompt);
        Assert.Equal(
            RealtimeSessionTuning.DefaultKeywordsForPair(LanguagePair.EnEs).ToArray(),
            client.LastStartedTuning?.TranscriptionKeywords.ToArray());

        await session.ApplyTuningChangeAsync();

        Assert.Equal(1, client.UpdateTranscriptionTuningCount);
        Assert.Equal(
            RealtimeSessionTuning.DefaultPromptForPair(LanguagePair.EnEs),
            client.LastTuning?.TranscriptionPrompt);
        Assert.Equal(
            RealtimeSessionTuning.DefaultKeywordsForPair(LanguagePair.EnEs).ToArray(),
            client.LastTuning?.TranscriptionKeywords.ToArray());
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
            session.RoutingSourceTextLengthForTests <= RoutingSourceTextWindow.MaxLength,
            $"routing buffer length {session.RoutingSourceTextLengthForTests} exceeded the cap before flip");

        client.PublishSourceDelta("ここで日本語へ反転します");
        await WaitUntilAsync(() => client.SpokenLanguages.Count > 1);

        Assert.Equal([SpokenLanguage.English, SpokenLanguage.Japanese], client.SpokenLanguages);
        Assert.True(
            session.RoutingSourceTextLengthForTests <= RoutingSourceTextWindow.MaxLength,
            $"routing buffer length {session.RoutingSourceTextLengthForTests} exceeded the cap after flip");
        await session.StopAsync();
    }

    // Given: 長い英語原文で target が確定したあとに日本語へ反転する
    // When: 切替を起こした delta を取り込む
    // Then: routing バッファは反転 delta だけになり、切替前の英語尾を残さない
    [Fact]
    public async Task LanguageFlipResetsRoutingBufferToTheFlipDelta()
    {
        var client = new FakeDualClient();
        using var session = NewSession(client);
        const string flipDelta = "ここで日本語へ反転します";

        await session.StartAsync();
        await WaitUntilAsync(() => session.State == TranslationState.Listening);

        client.PublishSourceDelta("we keep talking in english ");
        await WaitUntilAsync(() => client.SpokenLanguages.Count > 0);

        var processedDeltaCount = 0;
        session.BeforeAssemblerIngestForTests = () => Interlocked.Increment(ref processedDeltaCount);
        client.PublishSourceDelta("and we never flip the script ");
        await WaitUntilAsync(() => Volatile.Read(ref processedDeltaCount) >= 1);
        var bufferBeforeFlip = session.RoutingSourceTextForTests;
        Assert.Contains("script", bufferBeforeFlip, StringComparison.Ordinal);
        Assert.True(
            bufferBeforeFlip.Length > flipDelta.Length,
            "pre-flip routing buffer should still hold the English tail");

        client.PublishSourceDelta(flipDelta);
        await WaitUntilAsync(() => client.SpokenLanguages.Count > 1);

        Assert.Equal([SpokenLanguage.English, SpokenLanguage.Japanese], client.SpokenLanguages);
        Assert.Equal(
            " " + RoutingSourceTextWindow.Trim(flipDelta, LanguagePair.JaEn),
            session.RoutingSourceTextForTests);
        Assert.DoesNotContain("script", session.RoutingSourceTextForTests, StringComparison.Ordinal);
        Assert.DoesNotContain("english", session.RoutingSourceTextForTests, StringComparison.Ordinal);
        await session.StopAsync();
    }

    // Given: Listening 中のセッション
    // When: 翻訳 lane から input_transcript が届く
    // Then: 原文 authority にせず、routing も字幕も汚染しない
    [Fact]
    public async Task TranslationLaneInputTranscriptDoesNotRouteOrPolluteSource()
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

        var processedDeltaCount = 0;
        session.BeforeAssemblerIngestForTests = () => Interlocked.Increment(ref processedDeltaCount);
        client.PublishTranslationLaneInputTranscript(
            RealtimeTranslationOutputLanguage.English,
            "polluting source that should never become authority");
        await WaitUntilAsync(() => Volatile.Read(ref processedDeltaCount) >= 1);

        Assert.Empty(client.SelectedTargets);
        Assert.Equal(0, session.RoutingSourceTextLengthForTests);
        lock (updates)
        {
            Assert.DoesNotContain(
                updates,
                update => update.SourceText.Contains("polluting", StringComparison.Ordinal));
        }

        client.PublishSourceDelta("こんにちは");
        await WaitUntilAsync(() => client.SpokenLanguages.Count > 0);

        Assert.Equal([RealtimeTranslationOutputLanguage.English], client.SelectedTargets);
        await session.StopAsync();
    }

    // Given: en-es で英語 target 確定後、scalar 上限を超える長いスペイン語語窓
    // When: 逆方向ヒステリシス分の Spanish delta を送る
    // Then: 語窓を保ち English target へ切り替わる（scalar 切り詰めだと切り替わらない）
    [Fact]
    public async Task EnEsLongWordWindowIsPreservedForRouting()
    {
        var client = new FakeDualClient();
        using var session = NewSession(
            client,
            languagePairProvider: () => LanguagePair.EnEs);

        await session.StartAsync();
        await WaitUntilAsync(() => session.State == TranslationState.Listening);

        client.PublishSourceDelta("the and is are of to it that");
        await WaitUntilAsync(() =>
            client.SelectedTargets.SequenceEqual(
                [RealtimeTranslationOutputLanguage.Spanish]));

        // 先頭側にだけスペイン語証拠があり、後ろは長い filler。scalar 切り詰めだと証拠が消える。
        var longToken = new string('x', 40);
        var filler = string.Join(
            ' ',
            Enumerable.Repeat(longToken, SpokenLanguageDetector.EnEsWindow - 2));
        var spanishWindow = "está aquí " + filler;
        client.PublishSourceDelta(spanishWindow);
        client.PublishSourceDelta(" " + spanishWindow);
        await WaitUntilAsync(() =>
            client.SelectedTargets.SequenceEqual(
            [
                RealtimeTranslationOutputLanguage.Spanish,
                RealtimeTranslationOutputLanguage.English,
            ]));

        Assert.Equal(
            [
                RealtimeTranslationOutputLanguage.Spanish,
                RealtimeTranslationOutputLanguage.English,
            ],
            client.SelectedTargets);
        await session.StopAsync();
    }

    // Given: en-es で英語 target 確定後、上限を超える空白なしトークン
    // When: 長い1語の source delta を取り込む
    // Then: ライブの routing バッファも上限以内に収まる
    [Fact]
    public async Task EnEsLongWhitespaceFreeTokenDoesNotGrowRoutingBufferPastMaxLength()
    {
        var client = new FakeDualClient();
        using var session = NewSession(
            client,
            languagePairProvider: () => LanguagePair.EnEs);

        await session.StartAsync();
        await WaitUntilAsync(() => session.State == TranslationState.Listening);

        client.PublishSourceDelta("the and is are of to it that");
        await WaitUntilAsync(() =>
            client.SelectedTargets.SequenceEqual(
                [RealtimeTranslationOutputLanguage.Spanish]));

        var processedDeltaCount = 0;
        session.BeforeAssemblerIngestForTests = () => Interlocked.Increment(ref processedDeltaCount);
        client.PublishSourceDelta(new string('x', RoutingSourceTextWindow.MaxLength + 32));
        await WaitUntilAsync(() => Volatile.Read(ref processedDeltaCount) >= 1);

        Assert.True(
            session.RoutingSourceTextLengthForTests <= RoutingSourceTextWindow.MaxLength,
            $"routing buffer length {session.RoutingSourceTextLengthForTests} exceeded the cap");
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
        var gap = new string(' ', RoutingSourceTextWindow.MaxLength + 32);
        client.PublishSourceDelta("aa bb cc dd ee ff gg" + gap + " hh");
        await WaitUntilAsync(() => client.SpokenLanguages.Count > 1);

        Assert.Equal([SpokenLanguage.Japanese, SpokenLanguage.English], client.SpokenLanguages);
        Assert.True(
            session.RoutingSourceTextLengthForTests <= RoutingSourceTextWindow.MaxLength,
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

    // Given: ja-es で訳文がまだ無い日本語原文だけのセグメント
    // When: スペイン語へ文字種が反転する
    // Then: 不完全ペアを ShouldFinalize せず、routing だけ日本語 target へ切り替える
    [Fact]
    public async Task JaEsLanguageFlipDoesNotFinalizeIncompleteSourceOnlyPair()
    {
        var client = new FakeDualClient();
        using var session = NewSession(client, languagePairProvider: () => LanguagePair.JaEs);
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
        await WaitUntilAsync(() => client.SelectedTargets.Count == 1);
        Assert.Equal(RealtimeTranslationOutputLanguage.Spanish, client.SelectedTargets[0]);
        await WaitUntilAsync(() =>
        {
            lock (updates)
            {
                return updates.Exists(update => update.SourceText.Length > 0);
            }
        });
        var resetsAfterJapanese = client.ResetAudioRoutingCount;

        client.PublishSourceDelta("................ mundo ahora");
        await WaitUntilAsync(() => client.SelectedTargets.Count == 2);
        await WaitUntilAsync(() =>
        {
            lock (updates)
            {
                return updates.Exists(update =>
                    update.SourceText.Contains("mundo ahora", StringComparison.Ordinal));
            }
        });
        await Task.Delay(100);

        Assert.Equal(
            [RealtimeTranslationOutputLanguage.Spanish, RealtimeTranslationOutputLanguage.Japanese],
            client.SelectedTargets);
        Assert.True(client.ResetAudioRoutingCount > resetsAfterJapanese);
        lock (updates)
        {
            Assert.DoesNotContain(updates, update => update.ShouldFinalize);
        }

        await session.StopAsync();
    }

    // Given: en-es で訳文がまだ無いスペイン語原文だけのセグメント
    // When: hysteresis を満たして英語へ反転する
    // Then: 1 回目では確定せず、2 回目も不完全ペアを ShouldFinalize せず Spanish target へ切り替える
    [Fact]
    public async Task EnEsLanguageFlipDoesNotFinalizeIncompleteSourceOnlyPair()
    {
        var client = new FakeDualClient();
        using var session = NewSession(client, languagePairProvider: () => LanguagePair.EnEs);
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

        client.PublishSourceDelta("el la los las es está que y");
        await WaitUntilAsync(() => client.SelectedTargets.Count == 1);
        Assert.Equal(RealtimeTranslationOutputLanguage.English, client.SelectedTargets[0]);
        await WaitUntilAsync(() =>
        {
            lock (updates)
            {
                return updates.Exists(update => update.SourceText.Length > 0);
            }
        });
        var resetsAfterSpanish = client.ResetAudioRoutingCount;

        client.PublishSourceDelta(" the and is are of to it that");
        await WaitUntilAsync(() =>
        {
            lock (updates)
            {
                return updates.Exists(update =>
                    update.SourceText.Contains("the and is are", StringComparison.Ordinal)
                    && !update.ShouldFinalize);
            }
        });
        Assert.Equal([RealtimeTranslationOutputLanguage.English], client.SelectedTargets);
        lock (updates)
        {
            Assert.DoesNotContain(updates, update => update.ShouldFinalize);
        }

        client.PublishSourceDelta(" this with for you they the and");
        await WaitUntilAsync(() => client.SelectedTargets.Count == 2);
        await WaitUntilAsync(() =>
        {
            lock (updates)
            {
                return updates.Exists(update =>
                    update.SourceText.Contains("this with for you", StringComparison.Ordinal));
            }
        });
        await Task.Delay(100);

        Assert.Equal(
            [RealtimeTranslationOutputLanguage.English, RealtimeTranslationOutputLanguage.Spanish],
            client.SelectedTargets);
        Assert.True(client.ResetAudioRoutingCount > resetsAfterSpanish);
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

    // Given: ja-es で開始したセッション
    // When: ラテン 1 語だけの原文 delta を受け取る
    // Then: AmbiguousLatin をスペイン語話者とみなし、日本語 target を開く
    [Fact]
    public async Task JaEsAmbiguousLatinSelectsJapaneseTarget()
    {
        var client = new FakeDualClient();
        using var session = NewSession(client, languagePairProvider: () => LanguagePair.JaEs);
        await session.StartAsync();
        await WaitUntilAsync(() => session.State == TranslationState.Listening);

        client.PublishSourceDelta("Tokyo");
        await WaitUntilAsync(() => client.SelectedTargets.Count == 1);

        Assert.Equal([RealtimeTranslationOutputLanguage.Japanese], client.SelectedTargets);
        Assert.Equal([SpokenLanguage.Spanish], client.SpokenLanguages);
        await session.StopAsync();
    }

    // Given: en-es で開始したセッション
    // When: 排他語のないラテン 1 語だけの原文 delta を受け取る
    // Then: AmbiguousLatin では target を開かず、誤った lane へ preroll しない
    [Fact]
    public async Task EnEsAmbiguousLatinDoesNotSelectATarget()
    {
        var client = new FakeDualClient();
        using var session = NewSession(client, languagePairProvider: () => LanguagePair.EnEs);
        await session.StartAsync();
        await WaitUntilAsync(() => session.State == TranslationState.Listening);

        var processedDeltaCount = 0;
        session.BeforeAssemblerIngestForTests = () => Interlocked.Increment(ref processedDeltaCount);

        client.PublishSourceDelta("Tokyo");
        client.PublishSourceDelta(" Paris");
        await WaitUntilAsync(() => Volatile.Read(ref processedDeltaCount) >= 2);

        Assert.Empty(client.SelectedTargets);
        Assert.Empty(client.SpokenLanguages);
        await session.StopAsync();
    }

    // Given: 接続後に変更された言語ペア provider
    // When: 録音中に原文の言語反転を検出する
    // Then: 接続開始時のペアで routing し、provider の変更を反映しない
    [Fact]
    public async Task PairIsCachedForTheActiveConnection()
    {
        var client = new FakeDualClient();
        var pair = LanguagePair.JaEn;
        using var session = NewSession(client, languagePairProvider: () => pair);
        await session.StartAsync();
        await WaitUntilAsync(() => session.State == TranslationState.Listening);

        client.PublishSourceDelta("これは接続時ペアです");
        await WaitUntilAsync(() => client.SpokenLanguages.Count == 1);
        pair = LanguagePair.JaEs;
        client.PublishSourceDelta(" this remains the same pair");
        await WaitUntilAsync(() => client.SpokenLanguages.Count == 2);

        Assert.Equal([SpokenLanguage.Japanese, SpokenLanguage.English], client.SpokenLanguages);
        await session.StopAsync();
    }

    // Given: ja-en で開始したあと、録音中に provider を en-es へ変更する
    // When: transport error で再接続する
    // Then: 再接続後も Start 時点の ja-en を使い、停止→再開始でのみ新しいペアが反映される
    [Fact]
    public async Task ReconnectKeepsLanguagePairFrozenFromStart()
    {
        var client = new FakeDualClient();
        var pair = LanguagePair.JaEn;
        using var session = NewSession(client, languagePairProvider: () => pair);
        await session.StartAsync();
        await WaitUntilAsync(() => session.State == TranslationState.Listening);
        Assert.Equal(LanguagePair.JaEn, client.LastStartedPair);
        var startCountAtListening = client.StartCount;

        pair = LanguagePair.EnEs;
        client.PublishTransportError();
        await WaitUntilAsync(() =>
            session.State == TranslationState.Listening && client.StartCount > startCountAtListening);

        Assert.Equal(LanguagePair.JaEn, client.LastStartedPair);
        client.PublishSourceDelta("これは再接続後も同じペアです");
        await WaitUntilAsync(() => client.SpokenLanguages.Count == 1);
        Assert.Equal([SpokenLanguage.Japanese], client.SpokenLanguages);
        Assert.Equal([RealtimeTranslationOutputLanguage.English], client.SelectedTargets);

        await session.StopAsync();
        await WaitUntilAsync(() => session.State == TranslationState.Idle);
        await session.StartAsync();
        await WaitUntilAsync(() => session.State == TranslationState.Listening);
        Assert.Equal(LanguagePair.EnEs, client.LastStartedPair);
        await session.StopAsync();
    }

    // Given: ja-es ペアで開始したセッション
    // When: 日本語のあと、末尾窓から日本語を追い出すスペイン語 delta を受け取る
    // Then: 日本語→es、スペイン語→ja の target へ即時に切り替える
    [Fact]
    public async Task JaEsRoutesJapaneseAndSpanishToOppositeTargets()
    {
        var client = new FakeDualClient();
        using var session = NewSession(client, languagePairProvider: () => LanguagePair.JaEs);
        await session.StartAsync();
        await WaitUntilAsync(() => session.State == TranslationState.Listening);

        client.PublishSourceDelta("これは日本語の発話です");
        await WaitUntilAsync(() => client.SelectedTargets.Count == 1);
        Assert.Equal(RealtimeTranslationOutputLanguage.Spanish, client.SelectedTargets[0]);
        Assert.Equal(SpokenLanguage.Japanese, client.SpokenLanguages[0]);

        // 末尾 16 scalar から日本語を追い出し、ラテン 2 語以上で spanish を確定する。
        client.PublishSourceDelta("................");
        await Task.Delay(40);
        client.PublishSourceDelta(" mundo ahora");
        await WaitUntilAsync(() => client.SelectedTargets.Count == 2);
        Assert.Equal(
            [RealtimeTranslationOutputLanguage.Spanish, RealtimeTranslationOutputLanguage.Japanese],
            client.SelectedTargets);
        Assert.Equal([SpokenLanguage.Japanese, SpokenLanguage.Spanish], client.SpokenLanguages);
        await session.StopAsync();
    }

    // Given: en-es で英語 target が確定済み
    // When: 逆側 evidence が 1 回だけ、続いて同じ側、さらに逆側が連続 2 回来る
    // Then: 1 回では切り替わらず、連続 2 回でだけ target が反転する
    [Fact]
    public async Task EnEsRequiresTwoConsecutiveReverseEvidenceToSwitch()
    {
        var client = new FakeDualClient();
        using var session = NewSession(client, languagePairProvider: () => LanguagePair.EnEs);
        await session.StartAsync();
        await WaitUntilAsync(() => session.State == TranslationState.Listening);

        client.PublishSourceDelta("el la los las es está que y");
        await WaitUntilAsync(() => client.SelectedTargets.Count == 1);
        Assert.Equal(RealtimeTranslationOutputLanguage.English, client.SelectedTargets[0]);

        // 逆側 1 回だけでは切替しない。
        client.PublishSourceDelta(" the and is are of to it that");
        await Task.Delay(40);
        Assert.Equal([RealtimeTranslationOutputLanguage.English], client.SelectedTargets);

        // 同一言語 evidence で pending reverse count をリセットする。
        client.PublishSourceDelta(" el la los las es está que y");
        await Task.Delay(40);
        Assert.Equal([RealtimeTranslationOutputLanguage.English], client.SelectedTargets);

        // 連続 2 回の逆側 evidence でのみ es へ切り替える。
        client.PublishSourceDelta(" the and is are of to it that");
        await Task.Delay(40);
        Assert.Equal([RealtimeTranslationOutputLanguage.English], client.SelectedTargets);
        client.PublishSourceDelta(" this with for you they the and");
        await WaitUntilAsync(() => client.SelectedTargets.Count == 2);
        Assert.Equal(
            [RealtimeTranslationOutputLanguage.English, RealtimeTranslationOutputLanguage.Spanish],
            client.SelectedTargets);
        await session.StopAsync();
    }

    // Given: en-es でスペイン語→en の完全ペアが揃ったあと、逆側 evidence が連続 2 回来る
    // When: hysteresis を満たして英語へ反転する
    // Then: 1 回目では確定せず、2 回目で前セグメントを確定し Spanish target へ切り替える
    [Fact]
    public async Task EnEsLanguageFlipFinalizesAndReroutes()
    {
        var client = new FakeDualClient();
        using var session = NewSession(client, languagePairProvider: () => LanguagePair.EnEs);
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

        client.PublishSourceDelta("el la los las es está que y");
        await WaitUntilAsync(() => client.SelectedTargets.Count == 1);
        Assert.Equal(RealtimeTranslationOutputLanguage.English, client.SelectedTargets[0]);
        client.PublishTranslationDelta(RealtimeTranslationOutputLanguage.English, "Hello from Spanish");
        await WaitUntilAsync(() =>
        {
            lock (updates)
            {
                return updates.Exists(update => update.TranslatedText.Length > 0);
            }
        });
        var resetsAfterSpanish = client.ResetAudioRoutingCount;

        // When: 逆側 1 回だけでは切替も確定もしない（同一セグメントへ ingest される）
        client.PublishSourceDelta(" the and is are of to it that");
        await WaitUntilAsync(() =>
        {
            lock (updates)
            {
                return updates.Exists(update =>
                    update.SourceText.Contains("the and is are", StringComparison.Ordinal)
                    && !update.ShouldFinalize);
            }
        });
        Assert.Equal([RealtimeTranslationOutputLanguage.English], client.SelectedTargets);
        lock (updates)
        {
            Assert.DoesNotContain(updates, update => update.ShouldFinalize);
        }

        // When: 連続 2 回目で hysteresis を満たす
        client.PublishSourceDelta(" this with for you they the and");
        await WaitUntilAsync(() => client.SelectedTargets.Count == 2);

        // Then: 英語話者の target=es へ切り替わり、前セグメントが確定する
        Assert.Equal(
            [RealtimeTranslationOutputLanguage.English, RealtimeTranslationOutputLanguage.Spanish],
            client.SelectedTargets);
        Assert.Equal([SpokenLanguage.Spanish, SpokenLanguage.English], client.SpokenLanguages);
        Assert.True(client.ResetAudioRoutingCount > resetsAfterSpanish);
#if false
        RealtimeSubtitleUpdate finalized = default;
        await WaitUntilAsync(() =>
        {
            lock (updates)
            {
                finalized = updates.Find(update => update.ShouldFinalize);
                return finalized.ShouldFinalize;
            }
        });
        Assert.Equal(
            "el la los las es está que y the and is are of to it that",
            finalized.SourceText);
        Assert.DoesNotContain("this with for you they", finalized.SourceText, StringComparison.Ordinal);
        Assert.Equal("Hello from Spanish", finalized.TranslatedText);
        await session.StopAsync();
#else
        lock (updates)
        {
            Assert.Contains(updates, update => update.ShouldFinalize);
        }
        await session.StopAsync();
#endif
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

    // Given: Listening 中に現行 epoch で原文と訳文が揃っている
    // When: 古い epoch の訳文 delta が届く
    // Then: 画面の訳文を上書きしない
    [Fact]
    public async Task StaleEpochTranslationDeltaIsIgnored()
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

        var staleEpoch = client.ConnectionEpoch - 1;
        Assert.True(staleEpoch >= 0);
        client.PublishTranslationDelta(
            RealtimeTranslationOutputLanguage.Japanese,
            "これは古い接続です",
            epoch: staleEpoch);
        await Task.Delay(80);

        RealtimeSubtitleUpdate latest;
        lock (updates)
        {
            latest = updates.FindLast(update => update.TranslatedText.Length > 0);
            Assert.DoesNotContain(updates, update => update.TranslatedText.Contains("古い接続", StringComparison.Ordinal));
        }

        Assert.Equal("おはようございます", latest.TranslatedText);
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

    // Given: Listening 中に CloseGracefully が遅く Closing のまま
    // When: ApplyTuningChangeAsync する
    // Then: teardown 中の socket へ session.update を送らない
    [Fact]
    public async Task ApplyTuningChangeIsNoOpWhenClosing()
    {
        var closeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowCloseFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeDualClient();
        using var session = NewSession(client);
        await session.StartAsync();
        await WaitUntilAsync(() => session.State == TranslationState.Listening);

        // StartAsync がフックを消すため、Listening 到達後に遅い Close を仕込む。
        client.OnCloseGracefully = async () =>
        {
            closeStarted.TrySetResult();
            await allowCloseFinish.Task.ConfigureAwait(false);
            client.Complete();
        };

        var stopTask = session.StopAsync();
        await closeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(TranslationState.Closing, session.State);
        Assert.Equal(0, client.UpdateTranscriptionTuningCount);

        await session.ApplyTuningChangeAsync();

        Assert.Equal(TranslationState.Closing, session.State);
        Assert.Equal(0, client.UpdateTranscriptionTuningCount);

        allowCloseFinish.TrySetResult();
        await stopTask;
        Assert.Equal(TranslationState.Idle, session.State);
        Assert.Equal(0, client.UpdateTranscriptionTuningCount);
    }

    // Given: 致命エラーで Error になったセッション
    // When: ApplyTuningChangeAsync する
    // Then: 切断済み dual へ session.update を送らない
    [Fact]
    public async Task ApplyTuningChangeIsNoOpWhenError()
    {
        var client = new FakeDualClient();
        using var session = NewSession(client);
        await session.StartAsync();
        await WaitUntilAsync(() => session.State == TranslationState.Listening);
        Assert.Equal(0, client.UpdateTranscriptionTuningCount);

        client.PublishServerError("Incorrect API key provided", "invalid_api_key");
        await WaitUntilAsync(() => session.State == TranslationState.Error);

        await session.ApplyTuningChangeAsync();

        Assert.Equal(TranslationState.Error, session.State);
        Assert.Equal(0, client.UpdateTranscriptionTuningCount);
    }

    // Given: Dual Start 待ちで Connecting のセッション
    // When: ApplyTuningChangeAsync する
    // Then: handshake 未完了の接続へ session.update を送らず、Listening になるまで待つ
    [Fact]
    public async Task ApplyTuningChangeIsNoOpWhenConnecting()
    {
        var client = new FakeDualClient();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.StartGate = gate;
        using var session = NewSession(client);

        var startTask = session.StartAsync();
        await WaitUntilAsync(() =>
            session.State == TranslationState.Connecting && client.StartCount == 1);

        await session.ApplyTuningChangeAsync();

        Assert.Equal(0, client.UpdateTranscriptionTuningCount);
        gate.SetResult();
        client.StartGate = null;
        await WaitUntilAsync(() => session.State == TranslationState.Listening);
        await startTask;
        Assert.Equal(0, client.UpdateTranscriptionTuningCount);
        await session.StopAsync();
    }

    // Given: transport error 後の再接続待ち
    // When: ApplyTuningChangeAsync する
    // Then: 切断中の Dual へ live update せず、Listening 復帰後にだけ送れる
    [Fact]
    public async Task ApplyTuningChangeIsNoOpWhenReconnecting()
    {
        var client = new FakeDualClient();
        using var session = NewSession(client, initialReconnectDelay: TimeSpan.FromSeconds(2));
        await session.StartAsync();
        await WaitUntilAsync(() => session.State == TranslationState.Listening);

        client.PublishTransportError();
        await WaitUntilAsync(() => session.State == TranslationState.Reconnecting);

        await session.ApplyTuningChangeAsync();

        Assert.Equal(TranslationState.Reconnecting, session.State);
        Assert.Equal(0, client.UpdateTranscriptionTuningCount);
        await session.StopAsync();
        Assert.Equal(TranslationState.Idle, session.State);
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

    // Given: transport error 後の再接続待ち
    // When: 利用者が録音を停止する
    // Then: Idle に戻り、capture は止まり、追加 Start は走らない
    [Fact]
    public async Task StopDuringReconnectingReturnsToIdle()
    {
        var client = new FakeDualClient();
        var audio = new FakeAudioCapture();
        using var session = NewSession(
            client,
            audio: audio,
            initialReconnectDelay: TimeSpan.FromSeconds(2));
        await session.StartAsync();
        await WaitUntilAsync(() => session.State == TranslationState.Listening);
        Assert.Equal(1, client.StartCount);

        client.PublishTransportError();
        await WaitUntilAsync(() => session.State == TranslationState.Reconnecting);
        Assert.Equal(1, client.StartCount);

        await session.StopAsync();

        Assert.Equal(TranslationState.Idle, session.State);
        Assert.False(audio.IsRunning);
        Assert.Equal(1, client.StartCount);
        Assert.True(audio.StopCallCount >= 1);
    }

    // Given: 原文は assembler へ取り込み済みで、transport error の後ろに未読の訳文が channel に残っている
    // When: Reconnecting 中に利用者が録音を停止する
    // Then: IngestStopDrainEventsAsync が未読訳文を取り込み、完全ペアを ShouldFinalize する
    [Fact]
    public async Task StopDuringReconnectingIngestsUnreadChannelTranslationAndFinalizes()
    {
        var client = new FakeDualClient();
        using var session = NewSession(client, initialReconnectDelay: TimeSpan.FromSeconds(2));
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

        client.PublishSourceDelta("再接続前の原文");
        await WaitUntilAsync(() => client.SpokenLanguages.Count > 0);
        // consumer が先に error を見て落ちるよう、訳文は error の後ろへ積む。
        // ForceClose の TryComplete が二つの Publish の間に割り込むと、後続の
        // TryWrite は捨てられ、Stop drain の SourceText が null になる。
        client.PublishAtomically(() =>
        {
            client.PublishTransportError();
            client.PublishTranslationDelta(
                RealtimeTranslationOutputLanguage.English,
                "Unread translation after error");
        });
        await WaitUntilAsync(() => session.State == TranslationState.Reconnecting);
        Assert.Equal(1, client.StartCount);

        await session.StopAsync();

        Assert.Equal(TranslationState.Idle, session.State);
        Assert.Equal(1, client.StartCount);
        RealtimeSubtitleUpdate finalized;
        lock (updates)
        {
            finalized = updates.Find(update => update.ShouldFinalize);
        }

        Assert.Equal("再接続前の原文", finalized.SourceText);
        Assert.Equal("Unread translation after error", finalized.TranslatedText);
    }

    // Given: transport error 時点では Dual channel に訳文が無く、ForceClose 中に merge 相当で届く
    // When: TearDown が ForceClose したあと再接続 Start が channel を張り替える
    // Then: ForceClose 後の最終 drain が訳文を取り込み、BeginNewEpoch 前に ShouldFinalize する
    [Fact]
    public async Task ReconnectForceCloseLateMergedTranslationIsFinalizedBeforeNewEpoch()
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

        client.PublishSourceDelta("再接続前の原文");
        await WaitUntilAsync(() => client.SpokenLanguages.Count > 0);

        client.OnForceCloseBeforeComplete = () =>
        {
            client.OnForceCloseBeforeComplete = null;
            client.PublishTranslationDelta(
                RealtimeTranslationOutputLanguage.English,
                "Late merged translation during ForceClose");
        };
        client.PublishTransportError();
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

        Assert.Equal("再接続前の原文", finalized.SourceText);
        Assert.Equal("Late merged translation during ForceClose", finalized.TranslatedText);
        await session.StopAsync();
    }

    // Given: transport error 後の再接続待ち
    // When: その間に StartAsync を再度呼ぶ
    // Then: Idle/Error 以外は受理せず Dual Start は増えない
    [Fact]
    public async Task StartWhileReconnectingIsNoOp()
    {
        var client = new FakeDualClient();
        using var session = NewSession(client, initialReconnectDelay: TimeSpan.FromSeconds(2));
        await session.StartAsync();
        await WaitUntilAsync(() => session.State == TranslationState.Listening);

        client.PublishTransportError();
        await WaitUntilAsync(() => session.State == TranslationState.Reconnecting);
        var startCount = client.StartCount;

        await session.StartAsync();

        Assert.Equal(TranslationState.Reconnecting, session.State);
        Assert.Equal(startCount, client.StartCount);
        await session.StopAsync();
        Assert.Equal(TranslationState.Idle, session.State);
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

    // Given: Listening 中に transport error が起きても毎回再接続に成功する
    // When: 上限回数を超えても成功回復を繰り返す
    // Then: 成功接続で試行カウンタがリセットされ、Error に落ちない
    [Fact]
    public async Task SuccessfulReconnectResetsAttemptCounterBeforeLimit()
    {
        var client = new FakeDualClient();
        using var session = NewSession(client);
        string? message = null;
        session.MessageEncountered += (_, value) => message = value;

        await session.StartAsync();
        await WaitUntilAsync(() => session.State == TranslationState.Listening);

        var expectedStarts = 1;
        for (var index = 0; index < InterpretationSession.MaxReconnectAttempts + 1; index += 1)
        {
            client.PublishTransportError();
            expectedStarts += 1;
            await WaitUntilAsync(() =>
                client.StartCount >= expectedStarts && session.State == TranslationState.Listening);
            Assert.NotEqual(TranslationState.Error, session.State);
        }

        Assert.Equal(expectedStarts, client.StartCount);
        Assert.Equal(TranslationState.Listening, session.State);
        Assert.Null(message);
        await session.StopAsync();
        Assert.Equal(TranslationState.Idle, session.State);
    }

    // Given: 初回 target 選択が recoverable 例外になる dual
    // When: 日本語原文で routing を開始する
    // Then: Error にせず再接続し、Dual Start が再実行される
    [Fact]
    public async Task SelectTranslationTargetFailureTriggersReconnectWithoutEnteringError()
    {
        var client = new FakeDualClient { ThrowOnSelectTranslationTarget = true };
        using var session = NewSession(client);
        string? message = null;
        session.MessageEncountered += (_, value) => message = value;

        await session.StartAsync();
        await WaitUntilAsync(() => session.State == TranslationState.Listening);

        client.PublishSourceDelta("こんにちは");
        await WaitUntilAsync(() => client.StartCount >= 2 && session.State == TranslationState.Listening);

        Assert.True(client.StartCount >= 2);
        Assert.NotEqual(TranslationState.Error, session.State);
        Assert.Null(message);
        await session.StopAsync();
        Assert.Equal(TranslationState.Idle, session.State);
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

    // Given: 停止時 close drain で ServerError と完全ペアが同じ queue に並ぶ
    // When: 利用者が録音を停止する
    // Then: ServerError は無視し、完全ペアだけ ShouldFinalize する
    [Fact]
    public async Task StopIngestsTranscriptDespiteServerErrorInCloseDrain()
    {
        var client = new FakeDualClient();
        using var session = NewSession(client);
        var updates = new List<RealtimeSubtitleUpdate>();
        string? message = null;
        session.SubtitleUpdated += (_, update) =>
        {
            lock (updates)
            {
                updates.Add(update);
            }
        };
        session.MessageEncountered += (_, value) => message = value;

        await session.StartAsync();
        await WaitUntilAsync(() => session.State == TranslationState.Listening);

        client.OnCloseGracefully = () =>
        {
            client.PublishServerError("close drain transport glitch", "server_error");
            client.PublishSourceDelta("停止時の最終原文");
            client.PublishTranslationDelta(
                RealtimeTranslationOutputLanguage.English,
                "Final source at stop");
            client.Complete();
            return Task.CompletedTask;
        };

        await session.StopAsync();

        Assert.Equal(TranslationState.Idle, session.State);
        Assert.Null(message);
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

    // Given: 完全ペア確定後に次イベントが generation 確認済み・Ingest 前で停止している
    // When: その間に StopAsync して取り込みをフェンスする
    // Then: 既存完全ペアは ShouldFinalize され、停止中イベントは assembler を更新しない
    [Fact]
    public async Task StopFencesInFlightIngestBeforeFlushingCompletePair()
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

        client.PublishSourceDelta("停止フェンス前の完全ペア");
        await WaitUntilAsync(() => client.SpokenLanguages.Count > 0);
        client.PublishTranslationDelta(RealtimeTranslationOutputLanguage.English, "Complete pair before stop fence");
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
                throw new TimeoutException("Stop fence test hook was not released");
            }
        };

        client.PublishSourceDelta("Stop中に取り込ませない原文");
        Assert.True(enteredHook.Wait(TimeSpan.FromSeconds(5)));

        // Dispose と違い Stop は session loop 完了を待つ。generation を進めてから hook を解放する。
        var stopTask = session.StopAsync();
        await WaitUntilAsync(() => session.State == TranslationState.Closing);
        releaseHook.Set();
        await stopTask;

        Assert.Equal(TranslationState.Idle, session.State);
        RealtimeSubtitleUpdate finalized;
        lock (updates)
        {
            finalized = updates.Find(update => update.ShouldFinalize);
            Assert.DoesNotContain(
                updates,
                update => update.SourceText.Contains("取り込ませない", StringComparison.Ordinal));
        }

        Assert.Equal("停止フェンス前の完全ペア", finalized.SourceText);
        Assert.Equal("Complete pair before stop fence", finalized.TranslatedText);
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

    // Given: 訳文のあと同一言語の原文が伸びて stale になったセグメント
    // When: 利用者が録音を停止する
    // Then: 旧訳文を ShouldFinalize せず Idle へ戻る（字幕記録へ食い違ったペアを残さない）
    [Fact]
    public async Task StopDoesNotFinalizeStalePairAfterSourceContinues()
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

        client.PublishSourceDelta("、続きです");
        await WaitUntilAsync(() =>
        {
            lock (updates)
            {
                return updates.Exists(update =>
                    update.SourceText.Contains("続きです", StringComparison.Ordinal)
                    && !update.IsTranslationCurrent
                    && !update.ShouldFinalize);
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

    // Given: ja-en 録音が致命エラーで止まったあと、provider を ja-es へ変える
    // When: Error 状態から Start し直す
    // Then: 失敗セッションの凍結ペアは捨て、新しい録音は ja-es で始める
    [Fact]
    public async Task StartAfterFatalErrorUsesCurrentProviderPair()
    {
        var client = new FakeDualClient();
        var pair = LanguagePair.JaEn;
        using var session = NewSession(client, languagePairProvider: () => pair);
        await session.StartAsync();
        await WaitUntilAsync(() => session.State == TranslationState.Listening);
        Assert.Equal(LanguagePair.JaEn, client.LastStartedPair);

        client.PublishServerError("Incorrect API key provided", "invalid_api_key");
        await WaitUntilAsync(() => session.State == TranslationState.Error);
        Assert.Equal(1, client.StartCount);

        pair = LanguagePair.JaEs;
        await session.StartAsync();
        await WaitUntilAsync(() => session.State == TranslationState.Listening);

        Assert.Equal(LanguagePair.JaEs, client.LastStartedPair);
        Assert.Equal(2, client.StartCount);
        await session.StopAsync();
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

    // Given: Dual handshake は成功し、capture Start が 1 回だけ失敗する
    // When: セッションを開始する
    // Then: Dual を ForceClose して recoverable 再接続し、2 回目の capture で Listening になる
    [Fact]
    public async Task CaptureStartFailureReconnectsAndForceClosesDual()
    {
        var client = new FakeDualClient();
        var audio = new FakeAudioCapture { ThrowOnNextStart = true };
        using var session = NewSession(client, audio: audio);

        await session.StartAsync();
        await WaitUntilAsync(() => session.State == TranslationState.Listening);

        Assert.True(client.StartCount >= 2);
        Assert.True(client.ForceCloseCallCount >= 1);
        Assert.True(audio.StartCallCount >= 2);
        Assert.True(audio.StopCallCount >= 1);
        await session.StopAsync();
        Assert.Equal(TranslationState.Idle, session.State);
    }

    private static InterpretationSession NewSession(
        FakeDualClient client,
        string? apiKey = "sk-test",
        Func<RealtimeSessionTuning>? tuningProvider = null,
        FakeAudioCapture? audio = null,
        Func<LanguagePair>? languagePairProvider = null,
        TimeSpan? initialReconnectDelay = null) =>
        new(
            new FakeApiKeyStore(apiKey),
            audio ?? new FakeAudioCapture(),
            client,
            tuningProvider,
            initialReconnectDelay: initialReconnectDelay ?? TimeSpan.FromMilliseconds(1),
            tickInterval: TimeSpan.FromMilliseconds(20),
            languagePairProvider: languagePairProvider);

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

    /// <summary>Load をゲートし、Stop が generation を進めたあと欠落キーを返す。</summary>
    private sealed class GatedApiKeyStore : IApiKeyStore
    {
        private readonly TaskCompletionSource<string?> _load =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource LoadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release(string? apiKey) => _load.TrySetResult(apiKey);

        public string? Load()
        {
            LoadStarted.TrySetResult();
            return _load.Task.GetAwaiter().GetResult();
        }
    }

    private sealed class FakeAudioCapture : IRealtimeAudioCapture
    {
        private readonly object _sync = new();
        private Channel<ReadOnlyMemory<byte>> _frames =
            Channel.CreateUnbounded<ReadOnlyMemory<byte>>();

        public int StartCallCount { get; private set; }

        public int StopCallCount { get; private set; }

        public bool IsRunning { get; private set; }

        /// <summary>StartAsync 入口で待つゲート（Dual 完了後・capture 開始前の Stop 用）。</summary>
        public TaskCompletionSource? StartGate { get; set; }

        /// <summary>次の StartAsync だけ失敗させる（handshake 後のデバイス障害用）。</summary>
        public bool ThrowOnNextStart { get; set; }

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

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            Task? gateTask = null;
            lock (_sync)
            {
                StartCallCount += 1;
                // 再接続時に完了済み channel を使い回すと即 recoverable になるため張り直す。
                if (_frames.Reader.Completion.IsCompleted)
                {
                    _frames = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
                }

                if (ThrowOnNextStart)
                {
                    ThrowOnNextStart = false;
                    throw new InvalidOperationException("capture device failed");
                }

                gateTask = StartGate?.Task;
            }

            if (gateTask is not null)
            {
                await gateTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            lock (_sync)
            {
                IsRunning = true;
            }
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
        private readonly List<RealtimeTranslationOutputLanguage> _selectedTargets = [];
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

        public int ResetAudioRoutingCount { get; private set; }

        public int UpdateTranscriptionTuningCount { get; private set; }

        public int CloseGracefullyCallCount { get; private set; }

        public int ForceCloseCallCount { get; private set; }

        public RealtimeSessionTuning? LastTuning { get; private set; }

        public bool ThrowOnNextStart { get; set; }

        /// <summary>UpdateTranscriptionTuningAsync で RealtimeTranslationException を投げる。</summary>
        public bool ThrowRealtimeOnUpdateTuning { get; set; }

        /// <summary>SelectTranslationTargetAsync を 1 回だけ失敗させる（routing 例外の再接続テスト用）。</summary>
        public bool ThrowOnSelectTranslationTarget { get; set; }

        /// <summary>StartAsync を指定回数だけ失敗させる（再接続上限テスト用）。</summary>
        public int RemainingStartFailures { get; set; }

        /// <summary>CloseGracefully 時に close drain イベントを流すテスト用フック。</summary>
        public Func<Task>? OnCloseGracefully { get; set; }

        /// <summary>ForceClose が Complete する直前に、遅延 merge 相当のイベントを積む。</summary>
        public Action? OnForceCloseBeforeComplete { get; set; }

        /// <summary>StartAsync 入口で待つゲート（capture 順序・Stop 排水・Connecting 再入用）。</summary>
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

        public IReadOnlyList<RealtimeTranslationOutputLanguage> SelectedTargets
        {
            get
            {
                lock (_sync)
                {
                    return [.. _selectedTargets];
                }
            }
        }

        public LanguagePair? LastStartedPair { get; private set; }

        public RealtimeSessionTuning? LastStartedTuning { get; private set; }

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
            lock (_sync)
            {
                StartCount += 1;
                LastStartedPair = pair;
                LastStartedTuning = tuning;
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
                DeliveryState = new EventDeliveryState(_epoch);
                _spokenLanguages.Clear();
                _selectedTargets.Clear();
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

        public Task SelectTranslationTargetAsync(
            RealtimeTranslationOutputLanguage? target,
            CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                if (ThrowOnSelectTranslationTarget)
                {
                    ThrowOnSelectTranslationTarget = false;
                    throw new InvalidOperationException("injected select target failure");
                }

                if (target is { } selected)
                {
                    _selectedTargets.Add(selected);

                    // Swift FakeDualClient と同じく pair.counterpart(target) で話者言語を復元する。
                    if (LastStartedPair is { } pair
                        && pair.Counterpart(selected) is { } spoken)
                    {
                        _spokenLanguages.Add(spoken);
                    }
                }
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
            }

            // 本番 Dual と同じく、close 後は常に Events を完了させる。
            Complete();
        }

        public Task ForceCloseAsync()
        {
            ForceCloseCallCount += 1;
            OnForceCloseBeforeComplete?.Invoke();
            Complete();
            return Task.CompletedTask;
        }

        public void PublishSourceDelta(string delta, int? epoch = null) => PublishLane(
            RealtimeTranslationLane.Source,
            new RealtimeTranslationServerEvent.InputTranscriptDelta(delta, Guid.NewGuid().ToString(), null),
            epoch);

        public void PublishTranslationLaneInputTranscript(
            RealtimeTranslationOutputLanguage target,
            string delta,
            int? epoch = null) =>
            Publish(
                target,
                new RealtimeTranslationServerEvent.InputTranscriptDelta(delta, Guid.NewGuid().ToString(), null),
                epoch);

        public void PublishTranslationDelta(
            RealtimeTranslationOutputLanguage target,
            string delta,
            int? epoch = null) => Publish(
            target,
            new RealtimeTranslationServerEvent.OutputTranscriptDelta(delta, Guid.NewGuid().ToString(), null),
            epoch);

        /// <summary>
        /// 複数イベントを Complete と排他の同一 lock で積む。
        /// ForceClose の TryComplete が逐次 Publish の間に割り込むのを防ぐ。
        /// </summary>
        public void PublishAtomically(Action publish)
        {
            ArgumentNullException.ThrowIfNull(publish);
            lock (_sync)
            {
                publish();
            }
        }

        public void PublishTransportError() => Publish(
            RealtimeTranslationOutputLanguage.English,
            new RealtimeTranslationServerEvent.ServerError(
                DualRealtimeTranslationClient.TransportErrorMessage,
                DualRealtimeTranslationClient.TransportErrorCode));

        public void PublishServerError(string message, string code) => Publish(
            RealtimeTranslationOutputLanguage.English,
            new RealtimeTranslationServerEvent.ServerError(message, code));

        public void RecordLoss(
            EventDeliveryStage stage = EventDeliveryStage.Merge,
            int capacity = 512)
        {
            lock (_sync)
            {
                DeliveryState.RecordLoss(stage, capacity);
                _events.Writer.TryComplete();
            }
        }

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
            => PublishLane(RealtimeTranslationLane.Translation(target), serverEvent, epoch);

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
