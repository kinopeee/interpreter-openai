using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using RealtimeTranslator.Core.Audio;
using RealtimeTranslator.Core.Localization;
using RealtimeTranslator.Core.OpenAI;
using RealtimeTranslator.Core.Realtime;
using Xunit;

namespace RealtimeTranslator.Core.Tests;

public sealed class InterpretationSessionReceiveOverflowTests
{
    [Fact]
    public async Task LossWhileListeningInvalidatesAndReconnects()
    {
        // Given: Listening 中に未確定の翻訳ペアが取り込まれている
        var client = new FakeOverflowDualClient();
        using var session = CreateSession(client);
        var updates = new List<RealtimeSubtitleUpdate>();
        var pairReady = NewGate();
        var invalidated = NewGate();
        session.SubtitleUpdated += (_, update) =>
        {
            lock (updates)
            {
                updates.Add(update);
            }

            if (update.TranslatedText == "hello" && !update.ShouldFinalize)
            {
                pairReady.TrySetResult();
            }

            if (update.IsInvalidation)
            {
                invalidated.TrySetResult();
            }
        };

        await session.StartAsync();
        await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForStateAsync(session, TranslationState.Listening);
        client.PublishSourceDelta("こんにちは");
        client.PublishTranslationDelta("hello");
        await pairReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var oldFeed = client.Feed;

        // When: 現在エポックで受信ロスを記録し、古いキューへイベントを追加する
        client.RecordLoss(EventDeliveryStage.Merge);
        client.PublishTranslationDelta("stale", oldFeed.Epoch);

        // Then: 無効化を一度だけ発行し、再接続して古いイベントを取り込まない
        await invalidated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await client.SecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForStateAsync(session, TranslationState.Listening);
        Assert.Equal(2, client.StartCount);
        lock (updates)
        {
            Assert.Single(updates, update => update.IsInvalidation);
            Assert.DoesNotContain(
                updates,
                update => update.ShouldFinalize && update.TranslatedText == "stale");
            Assert.DoesNotContain(
                updates,
                update => update.ShouldFinalize && update.SourceText == "こんにちは");
        }

        await session.StopAsync();
    }

    [Fact]
    public async Task LossDuringAssemblerIngestInvalidatesAndReconnects()
    {
        // Given: Listening 中の session が assembler ingest 直前で止められる
        var client = new FakeOverflowDualClient();
        using var session = CreateSession(client);
        var invalidated = NewGate();
        session.SubtitleUpdated += (_, update) =>
        {
            if (update.IsInvalidation)
            {
                invalidated.TrySetResult();
            }
        };

        await session.StartAsync();
        await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForStateAsync(session, TranslationState.Listening);

        // When: routing 後・ingest 前に現 epoch の受信ロスを記録する
        session.BeforeAssemblerIngestForTests = () => client.RecordLoss(EventDeliveryStage.Merge);
        client.PublishSourceDelta("こんにちは");

        // Then: 無効化して再接続し、ConsumeEvents が黙って return しない
        await invalidated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await client.SecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForStateAsync(session, TranslationState.Listening);
        Assert.Equal(2, client.StartCount);
        await session.StopAsync();
    }

    [Fact]
    public async Task AuthenticationTerminationWinsOverOverflowWithoutReconnect()
    {
        // Given: Listening 中のイベント配送状態が認証失敗で終了している
        var client = new FakeOverflowDualClient();
        using var session = CreateSession(client);
        var error = NewGate();
        string? message = null;
        session.MessageEncountered += (_, value) =>
        {
            message = value;
            error.TrySetResult();
        };

        await session.StartAsync();
        await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForStateAsync(session, TranslationState.Listening);

        // When: その後に受信ロスを記録する
        client.RecordTermination(EventDeliveryTermination.AuthenticationFailed);
        client.RecordLoss(EventDeliveryStage.Merge);

        // Then: 認証エラーで停止し、オーバーフロー再接続を行わない
        await error.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForStateAsync(session, TranslationState.Error);
        Assert.Equal(UserCopy.Current.Text("error.authenticationFailed"), message);
        Assert.Equal(1, client.StartCount);
        await session.StopAsync();
    }

    [Fact]
    public async Task AuthenticationTerminationWinsWhenTransportServerErrorIsReadFirst()
    {
        // Given: authentication failure is already recorded on the active feed.
        var client = new FakeOverflowDualClient();
        using var session = CreateSession(client);
        var error = NewGate();
        string? message = null;
        session.MessageEncountered += (_, value) =>
        {
            message = value;
            error.TrySetResult();
        };

        await session.StartAsync();
        await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForStateAsync(session, TranslationState.Listening);
        client.RecordTermination(EventDeliveryTermination.AuthenticationFailed);

        // When: a lower-precedence transport error is the first queued event.
        client.PublishServerError(
            "transport disconnected",
            DualRealtimeTranslationClient.TransportErrorCode);

        // Then: the recorded authentication failure wins without reconnecting.
        await error.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForStateAsync(session, TranslationState.Error);
        Assert.Equal(UserCopy.Current.Text("error.authenticationFailed"), message);
        Assert.Equal(1, client.StartCount);
        await session.StopAsync();
    }

    [Fact]
    public async Task FatalTerminationMessageWinsOverOverflowWithoutReconnect()
    {
        // Given: Listening 中のイベント配送状態が致命的サーバーエラーで終了している
        var client = new FakeOverflowDualClient();
        using var session = CreateSession(client);
        var error = NewGate();
        string? message = null;
        session.MessageEncountered += (_, value) =>
        {
            message = value;
            error.TrySetResult();
        };
        const string serverMessage = "rate limit exceeded for test";

        await session.StartAsync();
        await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForStateAsync(session, TranslationState.Listening);

        // When: 致命的エラーの後に受信ロスを記録する
        client.RecordTermination(EventDeliveryTermination.FatalServerError, serverMessage);
        client.RecordLoss(EventDeliveryStage.Merge);

        // Then: サニタイズ済みサーバーエラーで停止し、再接続しない
        await error.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForStateAsync(session, TranslationState.Error);
        Assert.Equal(serverMessage, message);
        Assert.Equal(1, client.StartCount);
        await session.StopAsync();
    }

    [Fact]
    public async Task LossDuringStopDrainReturnsIdleWithoutFinalizingQueuedPair()
    {
        // Given: 停止前に未確定のソースが取り込まれている
        var client = new FakeOverflowDualClient();
        using var session = CreateSession(client);
        var sourceReady = NewGate();
        var finalized = new List<RealtimeSubtitleUpdate>();
        session.SubtitleUpdated += (_, update) =>
        {
            if (update.SourceText == "停止中" && !update.IsInvalidation)
            {
                sourceReady.TrySetResult();
            }

            if (update.ShouldFinalize)
            {
                lock (finalized)
                {
                    finalized.Add(update);
                }
            }
        };

        await session.StartAsync();
        await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForStateAsync(session, TranslationState.Listening);
        client.PublishSourceDelta("停止中");
        await sourceReady.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // When: close drain 中に受信ロスと遅延イベントが発生する
        client.OnCloseGracefully = () =>
        {
            client.RecordLoss(EventDeliveryStage.StopDrain);
            client.PublishTranslationDelta("遅延翻訳");
            return Task.CompletedTask;
        };

        // Then: 停止は Idle で完了し、ロス後に確定更新を発行しない
        await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(TranslationState.Idle, session.State);
        lock (finalized)
        {
            Assert.Empty(finalized);
        }
    }

    [Fact]
    public async Task PreviousEpochLossDoesNotAffectCurrentListeningSession()
    {
        // Given: 受信ロスから再接続して新しいエポックが Listening になっている
        var client = new FakeOverflowDualClient();
        using var session = CreateSession(client);
        var invalidationCount = 0;
        var invalidated = NewGate();
        session.SubtitleUpdated += (_, update) =>
        {
            if (update.IsInvalidation)
            {
                Interlocked.Increment(ref invalidationCount);
                invalidated.TrySetResult();
            }
        };

        await session.StartAsync();
        await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForStateAsync(session, TranslationState.Listening);
        var previousState = client.DeliveryState;
        client.RecordLoss(EventDeliveryStage.Merge);
        await invalidated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await client.SecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForStateAsync(session, TranslationState.Listening);

        // When: 以前のエポックだけにロスを記録する
        previousState.RecordLoss(EventDeliveryStage.Merge, RealtimeEventChannelCapacity());
        previousState.TryRecordTermination(
            EventDeliveryTermination.FatalServerError,
            "stale fatal error");

        // Then: 現在エポックは Listening のままで再接続しない
        Assert.Equal(1, invalidationCount);
        Assert.Equal(2, client.StartCount);
        Assert.Equal(TranslationState.Listening, session.State);
        await session.StopAsync();
    }

    [Fact]
    public async Task TranslationConnectionDeliversCapacityThenRecordsOverflow()
    {
        // Given: Translation 接続が読み手なしで開始されている
        var transport = new FakeRealtimeServerTransport();
        var state = new EventDeliveryState(1);
        using var connection = new RealtimeTranslationConnection(
            RealtimeTranslationOutputLanguage.English,
            transport,
            "test-safety");
        await connection.StartAsync(
            "sk-test",
            SessionConfigs.EnglishTargetWithoutSourceTranscription(),
            state);
        var read512 = NewGate();
        var release512 = NewGate();
        var read513 = NewGate();
        var release513 = NewGate();
        var readCount = 0;
        transport.AfterInboundRead = () =>
        {
            var count = Interlocked.Increment(ref readCount);
            if (count == RealtimeEventChannelCapacity())
            {
                read512.TrySetResult();
                release512.Task.GetAwaiter().GetResult();
            }
            else if (count == RealtimeEventChannelCapacity() + 1)
            {
                read513.TrySetResult();
                release513.Task.GetAwaiter().GetResult();
            }
        };

        // When: 512 件、続けて 513 件のイベントを送る
        for (var index = 0; index < RealtimeEventChannelCapacity(); index++)
        {
            transport.EnqueueJson(TranslationDeltaJson(index));
        }
        await read512.Task.WaitAsync(TimeSpan.FromSeconds(5));
        release512.TrySetResult();
        Assert.False(state.DidLoseEvents);

        transport.EnqueueJson(TranslationDeltaJson(RealtimeEventChannelCapacity()));
        await read513.Task.WaitAsync(TimeSpan.FromSeconds(5));
        release513.TrySetResult();
        await state.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(state.DidLoseEvents);
        Assert.Equal(EventDeliveryStage.Translation, state.LossStage);
        Assert.Equal(EventDeliveryTermination.ReceiveOverflow, state.Termination);
        var delivered = await ReadEventsAsync(connection.Events, RealtimeEventChannelCapacity());
        Assert.Equal(RealtimeEventChannelCapacity(), delivered.Count);
        Assert.Equal(
            EnumerableRange(RealtimeEventChannelCapacity()),
            delivered.ConvertAll(streamEvent => ((RealtimeTranslationServerEvent.OutputTranscriptDelta)streamEvent.Event).Delta));
        await connection.ForceCloseAsync();
    }

    [Fact]
    public async Task TranslationConnectionAuthenticationWinsWhenQueueIsFull()
    {
        // Given: Translation キューが容量いっぱいになっている
        var transport = new FakeRealtimeServerTransport();
        var state = new EventDeliveryState(1);
        using var connection = new RealtimeTranslationConnection(
            RealtimeTranslationOutputLanguage.English,
            transport,
            "test-safety");
        await connection.StartAsync(
            "sk-test",
            SessionConfigs.EnglishTargetWithoutSourceTranscription(),
            state);
        var read512 = NewGate();
        var readAuth = NewGate();
        var releaseAuth = NewGate();
        var readCount = 0;
        transport.AfterInboundRead = () =>
        {
            var count = Interlocked.Increment(ref readCount);
            if (count == RealtimeEventChannelCapacity())
            {
                read512.TrySetResult();
            }
            else if (count == RealtimeEventChannelCapacity() + 1)
            {
                readAuth.TrySetResult();
                releaseAuth.Task.GetAwaiter().GetResult();
            }
        };
        for (var index = 0; index < RealtimeEventChannelCapacity(); index++)
        {
            transport.EnqueueJson(TranslationDeltaJson(index));
        }
        await read512.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // When: 満杯のキューへ認証エラーを到着させる
        transport.EnqueueJson(
            """{"type":"error","error":{"message":"Incorrect API key","code":"invalid_api_key"}}""");
        await readAuth.Task.WaitAsync(TimeSpan.FromSeconds(5));
        releaseAuth.TrySetResult();
        await state.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        // Then: 損失が記録されても認証失敗の優先順位を保持する
        // Completion は termination 記録時点で完了し、損失記録はその直後なので記録完了を待つ。
        await WaitUntilAsync(() => state.DidLoseEvents);
        Assert.True(state.DidLoseEvents);
        Assert.Equal(EventDeliveryTermination.AuthenticationFailed, state.Termination);
        await connection.ForceCloseAsync();
    }

    [Fact]
    public async Task UnknownTranscriptionFailureInvalidatesPendingPairWithoutReconnect()
    {
        // Given: Listening 中に未確定の翻訳ペアが取り込まれている
        var client = new FakeOverflowDualClient();
        using var session = CreateSession(client);
        var invalidated = NewGate();
        var pairReady = NewGate();
        var updates = new List<RealtimeSubtitleUpdate>();
        session.SubtitleUpdated += (_, update) =>
        {
            lock (updates)
            {
                updates.Add(update);
            }

            if (update.TranslatedText == "hello" && !update.ShouldFinalize)
            {
                pairReady.TrySetResult();
            }

            if (update.IsInvalidation)
            {
                invalidated.TrySetResult();
            }
        };

        await session.StartAsync();
        await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForStateAsync(session, TranslationState.Listening);
        client.PublishSourceDelta("こんにちは");
        client.PublishTranslationDelta("hello");
        await pairReady.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // When: unknown code の transcription failed を受信する
        client.PublishSourceFailure("item-1", null, "unknown", null);

        // Then: 未確定字幕だけを無効化し、接続を維持する
        await invalidated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, client.StartCount);
        lock (updates)
        {
            Assert.Single(updates, update => update.IsInvalidation);
            Assert.DoesNotContain(updates, update => update.ShouldFinalize && update.SourceText == "こんにちは");
        }

        await session.StopAsync();
    }

    // Given: failed を受信した時点では字幕内容がない session
    // When: 同じ item の failed 後に字幕ペアを受信し、もう一度 failed を受信する
    // Then: 後続の failed で未確定字幕を無効化する
    [Fact]
    public async Task TranscriptionFailureBeforeContentCanInvalidateLaterContent()
    {
        var client = new FakeOverflowDualClient();
        using var session = CreateSession(client);
        var updates = new List<RealtimeSubtitleUpdate>();
        session.SubtitleUpdated += (_, update) =>
        {
            lock (updates)
            {
                updates.Add(update);
            }
        };

        await session.StartAsync();
        await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForStateAsync(session, TranslationState.Listening);

        // When: 内容がない状態で failed を受信する
        client.PublishSourceFailure("late-item", null, "unknown", null);
        await Task.Delay(100);
        lock (updates)
        {
            Assert.Empty(updates);
        }

        client.PublishSourceDelta("後続字幕");
        client.PublishTranslationDelta("Later subtitle");
        await WaitUntilAsync(() =>
        {
            lock (updates)
            {
                return updates.Any(update =>
                    update.SourceText == "後続字幕"
                    && update.TranslatedText == "Later subtitle");
            }
        });

        // Then: 同じ item の failed を再受信すると無効化する
        client.PublishSourceFailure("late-item", "second-event", "unknown", null);
        await WaitUntilAsync(() =>
        {
            lock (updates)
            {
                return updates.Any(update => update.IsInvalidation);
            }
        });

        lock (updates)
        {
            Assert.Single(updates, update => update.IsInvalidation);
        }

        await session.StopAsync();
    }

    // Given: Listening 中に新しい epoch の未確定字幕を表示している
    // When: 古い epoch の transcription failed を受信する
    // Then: 無効化せず現在の字幕を保持する
    [Fact]
    public async Task StaleEpochTranscriptionFailureDoesNotInvalidateCurrentSubtitle()
    {
        // Given: 再接続後に現在 epoch の字幕を表示する
        var client = new FakeOverflowDualClient();
        using var session = CreateSession(client);
        var updates = new List<RealtimeSubtitleUpdate>();
        session.SubtitleUpdated += (_, update) =>
        {
            lock (updates)
            {
                updates.Add(update);
            }
        };

        await session.StartAsync();
        await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForStateAsync(session, TranslationState.Listening);
        var oldEpoch = client.ConnectionEpoch;
        client.PublishServerError(
            DualRealtimeTranslationClient.TransportErrorMessage,
            DualRealtimeTranslationClient.TransportErrorCode);
        await client.SecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForStateAsync(session, TranslationState.Listening);
        client.PublishSourceDelta("現在の字幕");
        client.PublishTranslationDelta("Current subtitle");
        await WaitUntilAsync(() =>
        {
            lock (updates)
            {
                return updates.Any(update =>
                    update.SourceText == "現在の字幕"
                    && update.TranslatedText == "Current subtitle");
            }
        });
        var invalidationsBefore = updates.Count(update => update.IsInvalidation);

        // When: 古い epoch の failed を投入する
        client.PublishSourceFailure(
            "stale-item",
            "stale-event",
            "audio_unintelligible",
            null,
            oldEpoch);
        await Task.Delay(100);

        // Then: 現在の字幕と Listening 状態を保つ
        Assert.Equal(TranslationState.Listening, session.State);
        Assert.Equal(2, client.StartCount);
        lock (updates)
        {
            Assert.Equal(invalidationsBefore, updates.Count(update => update.IsInvalidation));
            Assert.Contains(
                updates,
                update => update.SourceText == "現在の字幕"
                    && update.TranslatedText == "Current subtitle");
        }

        await session.StopAsync();
    }

    // Given: Listening 中に未確定の字幕ペアを表示している
    // When: recover 分類の transcription failed を受信する
    // Then: 無効化して再接続し、未確定ペアを確定しない
    [Fact]
    public async Task RecoverTranscriptionFailureReconnectsWithoutFinalizingPendingPair()
    {
        // Given: 未確定の原文と訳文を表示する
        var client = new FakeOverflowDualClient();
        using var session = CreateSession(client);
        var updates = new List<RealtimeSubtitleUpdate>();
        var invalidated = NewGate();
        session.SubtitleUpdated += (_, update) =>
        {
            lock (updates)
            {
                updates.Add(update);
            }

            if (update.IsInvalidation)
            {
                invalidated.TrySetResult();
            }
        };

        await session.StartAsync();
        await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForStateAsync(session, TranslationState.Listening);
        client.PublishSourceDelta("失敗する字幕");
        client.PublishTranslationDelta("Recover subtitle");
        await WaitUntilAsync(() =>
        {
            lock (updates)
            {
                return updates.Any(update =>
                    update.SourceText == "失敗する字幕"
                    && update.TranslatedText == "Recover subtitle");
            }
        });

        // When: recover 対象の failed を投入する
        client.PublishSourceFailure("recover-item", null, null, "server_error");

        // Then: 無効化後に再接続し、未確定ペアを確定しない
        await invalidated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await client.SecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForStateAsync(session, TranslationState.Listening);
        lock (updates)
        {
            Assert.DoesNotContain(
                updates,
                update => update.ShouldFinalize
                    && (update.SourceText == "失敗する字幕"
                        || update.TranslatedText == "Recover subtitle"));
        }

        await session.StopAsync();
    }

    // Given: 未確定の字幕ペアを表示中で failed の termination が先に完了する session
    // When: recover 分類の failed イベントを後から受信する
    // Then: 未確定ペアを確定せず無効化して再接続する
    [Fact]
    public async Task RecoverTranscriptionFailureCompletionBeforeEventDoesNotFinalizePendingPair()
    {
        var client = new FakeOverflowDualClient();
        using var session = CreateSession(client);
        var updates = new List<RealtimeSubtitleUpdate>();
        var invalidated = NewGate();
        session.SubtitleUpdated += (_, update) =>
        {
            lock (updates)
            {
                updates.Add(update);
            }

            if (update.IsInvalidation)
            {
                invalidated.TrySetResult();
            }
        };

        await session.StartAsync();
        await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForStateAsync(session, TranslationState.Listening);
        client.PublishSourceDelta("順序競合字幕");
        client.PublishTranslationDelta("Ordering race");
        await WaitUntilAsync(() =>
        {
            lock (updates)
            {
                return updates.Any(update =>
                    update.SourceText == "順序競合字幕"
                    && update.TranslatedText == "Ordering race");
            }
        });

        // When: termination が完了してから failed イベントを配送する
        await client.PublishSourceFailureAfterTerminationAsync(
            "ordering-item",
            null,
            null,
            "server_error");

        // Then: 無効化して再接続し、失敗したペアを確定しない
        await invalidated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await client.SecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForStateAsync(session, TranslationState.Listening);
        lock (updates)
        {
            Assert.DoesNotContain(
                updates,
                update => update.ShouldFinalize
                    && (update.SourceText == "順序競合字幕"
                        || update.TranslatedText == "Ordering race"));
        }

        await session.StopAsync();
    }

    // Given: Listening 中に未確定の字幕ペアを表示している
    // When: halt 分類の transcription failed を受信する
    // Then: 無効化して Error になり、未確定ペアを確定しない
    [Fact]
    public async Task HaltTranscriptionFailureDoesNotFinalizePendingPair()
    {
        var client = new FakeOverflowDualClient();
        using var session = CreateSession(client);
        var updates = new List<RealtimeSubtitleUpdate>();
        var invalidated = NewGate();
        session.SubtitleUpdated += (_, update) =>
        {
            lock (updates)
            {
                updates.Add(update);
            }

            if (update.IsInvalidation)
            {
                invalidated.TrySetResult();
            }
        };

        await session.StartAsync();
        await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForStateAsync(session, TranslationState.Listening);
        client.PublishSourceDelta("失敗する字幕");
        client.PublishTranslationDelta("Halt subtitle");
        await WaitUntilAsync(() =>
        {
            lock (updates)
            {
                return updates.Any(update =>
                    update.SourceText == "失敗する字幕"
                    && update.TranslatedText == "Halt subtitle");
            }
        });

        client.PublishSourceFailure("halt-item", null, "insufficient_quota", "server_error");

        await invalidated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForStateAsync(session, TranslationState.Error);
        Assert.Equal(1, client.StartCount);
        lock (updates)
        {
            Assert.DoesNotContain(
                updates,
                update => update.ShouldFinalize
                    && (update.SourceText == "失敗する字幕"
                        || update.TranslatedText == "Halt subtitle"));
        }

        await session.StopAsync();
    }

    // Given: idle finalize で確定済みの字幕ペアを保持している
    // When: transcription failed を受信する
    // Then: 確定済み字幕と通知数を保持する
    [Fact]
    public async Task TranscriptionFailurePreservesFinalizedSubtitle()
    {
        // Given: 短い idle tick 間隔で完全ペアを表示する
        var client = new FakeOverflowDualClient();
        var clock = new MonotonicClock();
        using var session = CreateSession(client, TimeSpan.FromMilliseconds(15), clock);
        var updates = new List<RealtimeSubtitleUpdate>();
        session.SubtitleUpdated += (_, update) =>
        {
            lock (updates)
            {
                updates.Add(update);
            }
        };

        await session.StartAsync();
        await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForStateAsync(session, TranslationState.Listening);
        client.PublishSourceDelta("確定済み字幕");
        client.PublishTranslationDelta("Finalized subtitle");
        await WaitUntilAsync(() =>
        {
            lock (updates)
            {
                return updates.Any(update =>
                    update.SourceText == "確定済み字幕"
                    && update.TranslatedText == "Finalized subtitle");
            }
        });
        clock.Advance(RealtimeSubtitleAssembler.IdleFinalizeInterval + TimeSpan.FromMilliseconds(50));
        await WaitUntilAsync(() =>
        {
            lock (updates)
            {
                return updates.Any(update =>
                    update.ShouldFinalize
                    && update.SourceText == "確定済み字幕"
                    && update.TranslatedText == "Finalized subtitle");
            }
        });
        int finalizedCount;
        lock (updates)
        {
            finalizedCount = updates.Count(update =>
                update.ShouldFinalize
                && update.SourceText == "確定済み字幕"
                && update.TranslatedText == "Finalized subtitle");
        }

        Assert.True(finalizedCount > 0);

        // When: 確定済み字幕に紐づく failed を投入する
        client.PublishSourceFailure("finalized-item", null, "audio_unintelligible", null);
        await Task.Delay(100);

        // Then: 確定済み字幕の通知数と内容を保持する
        lock (updates)
        {
            Assert.Equal(
                finalizedCount,
                updates.Count(update =>
                    update.ShouldFinalize
                    && update.SourceText == "確定済み字幕"
                    && update.TranslatedText == "Finalized subtitle"));
        }

        await session.StopAsync();
    }

    // Given: 停止前に未確定の字幕ペアを表示している
    // When: stop drain 中に transcription failed を受信する
    // Then: 停止を完了し、未確定ペアを確定しない
    [Fact]
    public async Task StopDrainTranscriptionFailureDoesNotFinalizePendingPair()
    {
        // Given: 未確定の原文と訳文を表示する
        var client = new FakeOverflowDualClient();
        using var session = CreateSession(client);
        var updates = new List<RealtimeSubtitleUpdate>();
        session.SubtitleUpdated += (_, update) =>
        {
            lock (updates)
            {
                updates.Add(update);
            }
        };

        await session.StartAsync();
        await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForStateAsync(session, TranslationState.Listening);
        client.PublishSourceDelta("停止中の字幕");
        client.PublishTranslationDelta("Stopping subtitle");
        await WaitUntilAsync(() =>
        {
            lock (updates)
            {
                return updates.Any(update =>
                    update.SourceText == "停止中の字幕"
                    && update.TranslatedText == "Stopping subtitle");
            }
        });
        client.CloseGracefullyEvents =
        [
            new RealtimeTranslationStreamEvent(
                RealtimeTranslationLane.Source,
                new RealtimeTranslationServerEvent.InputTranscriptFailed(
                    "stop-item",
                    "stop-event",
                    "audio_unintelligible",
                    null),
                client.ConnectionEpoch)
        ];

        // When: 停止する
        await session.StopAsync();

        // Then: stop は完了し、未確定ペアを確定しない
        Assert.Equal(TranslationState.Idle, session.State);
        lock (updates)
        {
            Assert.DoesNotContain(
                updates,
                update => update.ShouldFinalize
                    && (update.SourceText == "停止中の字幕"
                        || update.TranslatedText == "Stopping subtitle"));
        }
    }

    // Given: 停止前に未確定の字幕ペアを表示している
    // When: stop drain 中に古い epoch の transcription failed を受信する
    // Then: 現在の未確定ペアを無効化せず、停止時に確定する
    [Fact]
    public async Task StopDrainStaleEpochTranscriptionFailureDoesNotInvalidateCurrentPair()
    {
        var client = new FakeOverflowDualClient();
        using var session = CreateSession(client);
        var updates = new List<RealtimeSubtitleUpdate>();
        session.SubtitleUpdated += (_, update) =>
        {
            lock (updates)
            {
                updates.Add(update);
            }
        };

        await session.StartAsync();
        await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForStateAsync(session, TranslationState.Listening);
        client.PublishSourceDelta("停止中の字幕");
        client.PublishTranslationDelta("Stopping subtitle");
        await WaitUntilAsync(() =>
        {
            lock (updates)
            {
                return updates.Any(update =>
                    update.SourceText == "停止中の字幕"
                    && update.TranslatedText == "Stopping subtitle");
            }
        });
        client.CloseGracefullyEvents =
        [
            new RealtimeTranslationStreamEvent(
                RealtimeTranslationLane.Source,
                new RealtimeTranslationServerEvent.InputTranscriptFailed(
                    "stale-stop-item",
                    "stale-stop-event",
                    "audio_unintelligible",
                    null),
                client.ConnectionEpoch - 1)
        ];

        await session.StopAsync();

        Assert.Equal(TranslationState.Idle, session.State);
        lock (updates)
        {
            Assert.DoesNotContain(updates, update => update.IsInvalidation);
            Assert.Contains(
                updates,
                update => update.ShouldFinalize
                    && update.SourceText == "停止中の字幕"
                    && update.TranslatedText == "Stopping subtitle");
        }
    }

    // Given: failed により秘密情報を含む未確定字幕を無効化する
    // When: session が keepAlive の failed を処理する
    // Then: MessageEncountered と無効化後の字幕へ秘密情報を出さない
    [Fact]
    public async Task TranscriptionFailureDoesNotExposeMessage()
    {
        // Given: 秘密情報を含む原文ペアを表示する
        var client = new FakeOverflowDualClient();
        using var session = CreateSession(client);
        var updates = new List<RealtimeSubtitleUpdate>();
        var invalidated = NewGate();
        var messages = new List<string>();
        session.SubtitleUpdated += (_, update) =>
        {
            lock (updates)
            {
                updates.Add(update);
            }

            if (update.IsInvalidation)
            {
                invalidated.TrySetResult();
            }
        };
        session.MessageEncountered += (_, message) =>
        {
            lock (messages)
            {
                messages.Add(message);
            }
        };

        await session.StartAsync();
        await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForStateAsync(session, TranslationState.Listening);
        client.PublishSourceDelta("こんにちは sk-leak-1234");
        client.PublishTranslationDelta("Secret subtitle");
        await WaitUntilAsync(() =>
        {
            lock (updates)
            {
                return updates.Any(update => update.SourceText.Contains("sk-leak-1234"));
            }
        });

        // When: keepAlive の failed を投入する
        client.PublishSourceFailure("privacy-item", "privacy-event", "audio_unintelligible", null);

        // Then: 秘密情報を通知せず、無効化後の字幕にも残さない
        await invalidated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        lock (messages)
        {
            Assert.Empty(messages);
        }

        lock (updates)
        {
            var invalidationIndex = updates.FindIndex(update => update.IsInvalidation);
            Assert.True(invalidationIndex >= 0);
            Assert.DoesNotContain(
                updates.Skip(invalidationIndex),
                update => update.SourceText.Contains("sk-")
                    || update.TranslatedText.Contains("sk-"));
        }

        await session.StopAsync();
    }

    private static InterpretationSession CreateSession(
        FakeOverflowDualClient client,
        TimeSpan? tickInterval = null,
        TimeProvider? timeProvider = null) =>
        new(
            new FakeApiKeyStore(),
            new FakeAudioCapture(),
            client,
            timeProvider: timeProvider,
            initialReconnectDelay: TimeSpan.FromMilliseconds(1),
            tickInterval: tickInterval ?? TimeSpan.FromHours(1));

    private static TaskCompletionSource NewGate() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

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
    }

    private static async Task WaitForStateAsync(
        InterpretationSession session,
        TranslationState state)
    {
        if (session.State == state)
        {
            return;
        }

        var gate = NewGate();
        void Handler(object? _, TranslationState value)
        {
            if (value == state)
            {
                gate.TrySetResult();
            }
        }

        session.StateChanged += Handler;
        try
        {
            if (session.State == state)
            {
                return;
            }

            await gate.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            session.StateChanged -= Handler;
        }
    }

    private static async Task<List<RealtimeTranslationStreamEvent>> ReadEventsAsync(
        ChannelReader<RealtimeTranslationStreamEvent> reader,
        int count)
    {
        var result = new List<RealtimeTranslationStreamEvent>(count);
        while (result.Count < count)
        {
            Assert.True(await reader.WaitToReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            while (reader.TryRead(out var streamEvent))
            {
                result.Add(streamEvent);
                if (result.Count == count)
                {
                    break;
                }
            }
        }

        return result;
    }

    private static int RealtimeEventChannelCapacity() => 512;

    private static List<string> EnumerableRange(int count)
    {
        var values = new List<string>(count);
        for (var index = 0; index < count; index++)
        {
            values.Add(index.ToString());
        }

        return values;
    }

    private static string TranslationDeltaJson(int index) =>
        $$"""{"type":"session.output_transcript.delta","delta":"{{index}}","event_id":"event-{{index}}"}""";

    private sealed class FakeApiKeyStore : IApiKeyStore
    {
        public string? Load() => "sk-test";
    }

    private sealed class FakeAudioCapture : IRealtimeAudioCapture
    {
        private readonly Channel<CapturedAudioFrame> _frames =
            Channel.CreateUnbounded<CapturedAudioFrame>();

        public ChannelReader<CapturedAudioFrame> Frames => _frames.Reader;

        public Task StartAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task StopAsync() => Task.CompletedTask;
    }

    private sealed class FakeOverflowDualClient : IDualRealtimeTranslationClient
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

        public TaskCompletionSource Started { get; } =
            NewGate();

        public TaskCompletionSource SecondStarted { get; } =
            NewGate();

        public Func<Task>? OnCloseGracefully { get; set; }

        public IReadOnlyList<RealtimeTranslationStreamEvent> CloseGracefullyEvents { get; set; } = [];

        public Task StartAsync(
            string apiKey,
            RealtimeSessionTuning tuning,
            LanguagePair pair = LanguagePair.JaEn,
            CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                StartCount++;
                _epoch++;
                DeliveryState = new EventDeliveryState(_epoch);
                _events = Channel.CreateUnbounded<RealtimeTranslationStreamEvent>();
                if (StartCount == 1)
                {
                    Started.TrySetResult();
                }
                else if (StartCount == 2)
                {
                    SecondStarted.TrySetResult();
                }
            }

            return Task.CompletedTask;
        }

        public Task AppendAudioFrameAsync(
            ReadOnlyMemory<byte> pcm16LittleEndian,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SelectTranslationTargetAsync(
            RealtimeTranslationOutputLanguage? target,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task UpdateTranscriptionTuningAsync(
            RealtimeSessionTuning tuning,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task ResetAudioRoutingAsync() => Task.CompletedTask;

        public async Task CloseGracefullyAsync(CancellationToken cancellationToken = default)
        {
            if (OnCloseGracefully is { } hook)
            {
                await hook().ConfigureAwait(false);
            }

            lock (_sync)
            {
                foreach (var streamEvent in CloseGracefullyEvents)
                {
                    _events.Writer.TryWrite(streamEvent);
                }

                CloseGracefullyEvents = [];
            }
            Complete();
        }

        public Task ForceCloseAsync()
        {
            Complete();
            return Task.CompletedTask;
        }

        public void RecordLoss(EventDeliveryStage stage) =>
            DeliveryState.RecordLoss(stage, RealtimeEventChannelCapacity());

        public void RecordTermination(
            EventDeliveryTermination termination,
            string? message = null) =>
            DeliveryState.TryRecordTermination(termination, message);

        public void PublishSourceDelta(string delta, int? epoch = null) =>
            Publish(
                RealtimeTranslationLane.Source,
                new RealtimeTranslationServerEvent.InputTranscriptDelta(
                    delta,
                    Guid.NewGuid().ToString(),
                    null),
                epoch);

        public void PublishSourceFailure(
            string? itemId,
            string? eventId,
            string? code,
            string? errorType,
            int? epoch = null) =>
            Publish(
                RealtimeTranslationLane.Source,
                new RealtimeTranslationServerEvent.InputTranscriptFailed(itemId, eventId, code, errorType),
                epoch);

        public async Task PublishSourceFailureAfterTerminationAsync(
            string? itemId,
            string? eventId,
            string? code,
            string? errorType)
        {
            DeliveryState.MarkSourceItemFailed();
            DeliveryState.TryRecordTermination(EventDeliveryTermination.RecoverableServerError);
            await Task.Yield();
            PublishSourceFailure(itemId, eventId, code, errorType);
        }

        public void PublishTranslationDelta(string delta, int? epoch = null) =>
            Publish(
                RealtimeTranslationLane.Translation(RealtimeTranslationOutputLanguage.English),
                new RealtimeTranslationServerEvent.OutputTranscriptDelta(
                    delta,
                    Guid.NewGuid().ToString(),
                    null),
                epoch);

        public void PublishServerError(string message, string code, int? epoch = null) =>
            Publish(
                RealtimeTranslationLane.Source,
                new RealtimeTranslationServerEvent.ServerError(message, code),
                epoch);

        private void Publish(
            RealtimeTranslationLane lane,
            RealtimeTranslationServerEvent serverEvent,
            int? epoch)
        {
            lock (_sync)
            {
                _events.Writer.TryWrite(
                    new RealtimeTranslationStreamEvent(
                        lane,
                        serverEvent,
                        epoch ?? _epoch));
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
