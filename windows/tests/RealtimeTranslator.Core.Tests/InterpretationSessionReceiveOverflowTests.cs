using System;
using System.Collections.Generic;
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
            RealtimeTranslationSessionConfig.EnglishTargetWithoutSourceTranscription(),
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
            RealtimeTranslationSessionConfig.EnglishTargetWithoutSourceTranscription(),
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
        Assert.True(state.DidLoseEvents);
        Assert.Equal(EventDeliveryTermination.AuthenticationFailed, state.Termination);
        await connection.ForceCloseAsync();
    }

    private static InterpretationSession CreateSession(FakeOverflowDualClient client) =>
        new(
            new FakeApiKeyStore(),
            new FakeAudioCapture(),
            client,
            initialReconnectDelay: TimeSpan.FromMilliseconds(1),
            tickInterval: TimeSpan.FromHours(1));

    private static TaskCompletionSource NewGate() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

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
        private readonly Channel<ReadOnlyMemory<byte>> _frames =
            Channel.CreateUnbounded<ReadOnlyMemory<byte>>();

        public ChannelReader<ReadOnlyMemory<byte>> Frames => _frames.Reader;

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
