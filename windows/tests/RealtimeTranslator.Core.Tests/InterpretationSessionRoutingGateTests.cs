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
/// Dispose が routing gate 保持中でも SemaphoreSlim を破棄せず、
/// 確定済みペアを残し、ゲート解除後の原文を ingest しないこと。
/// InterpretationSessionTests の開いている PR とはファイルを分けて衝突を避ける。
/// </summary>
public sealed class InterpretationSessionRoutingGateTests
{
    // Given: 完全ペア表示後、言語切替の SelectTranslationTarget が routing gate 内で止まっている
    // When: Stop を経ずに Dispose する（OnExit 相当）
    // Then: 切替前ペアは ShouldFinalize され、切替原文は assembler に入らず、破棄例外にならない
    [Fact]
    public async Task DisposeWhileSelectTargetBlockedStillFinalizesCompletePair()
    {
        var selectGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var enteredSelect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
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

        // 初回 Select のあとだけゲートを仕込み、切替 Select だけを止める。
        client.EnteredSelectTarget = enteredSelect;
        client.SelectTargetGate = selectGate;
        client.PublishSourceDelta(" now we continue in english for a while");
        await enteredSelect.Task.WaitAsync(TimeSpan.FromSeconds(5));

        session.Dispose();
        selectGate.TrySetResult();
        await Task.Delay(80);

        RealtimeSubtitleUpdate finalized;
        lock (updates)
        {
            finalized = updates.Find(update => update.ShouldFinalize);
            Assert.Contains(
                updates,
                update => update.SourceText.Contains("continue in english", StringComparison.Ordinal));
        }

        Assert.Equal("フェンス前の完全ペア", finalized.SourceText);
        Assert.Equal("Complete pair before fence", finalized.TranslatedText);
    }

    // Given: idle Tick が完全ペアを確定した直後、ResetAudioRouting が routing gate 内で止まっている
    // When: その間に Dispose する
    // Then: 確定済みペアは残し、ObjectDisposedException にならない
    [Fact]
    public async Task DisposeWhileResetAudioRoutingBlockedStillKeepsFinalizedPair()
    {
        var resetGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var enteredReset = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeDualClient();
        var clock = new ControllableTimeProvider(DateTimeOffset.Parse("2026-08-27T00:00:00Z"));
        var session = NewSession(client, clock);
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

        client.PublishSourceDelta("Tick確定前の完全ペア");
        await WaitUntilAsync(() => client.SpokenLanguages.Count > 0);
        client.PublishTranslationDelta(RealtimeTranslationOutputLanguage.English, "Complete pair before idle tick");
        await WaitUntilAsync(() =>
        {
            lock (updates)
            {
                return updates.Exists(update =>
                    update.TranslatedText.Length > 0 && !update.ShouldFinalize);
            }
        });

        // Start 時の ResetAudioRouting は通したあと、idle finalize の Reset だけ止める。
        client.EnteredResetAudioRouting = enteredReset;
        client.ResetAudioRoutingGate = resetGate;
        clock.Advance(RealtimeSubtitleAssembler.IdleFinalizeInterval + TimeSpan.FromMilliseconds(50));
        await enteredReset.Task.WaitAsync(TimeSpan.FromSeconds(5));

        session.Dispose();
        resetGate.TrySetResult();
        await Task.Delay(80);

        RealtimeSubtitleUpdate finalized;
        lock (updates)
        {
            finalized = updates.Find(update => update.ShouldFinalize);
        }

        Assert.Equal("Tick確定前の完全ペア", finalized.SourceText);
        Assert.Equal("Complete pair before idle tick", finalized.TranslatedText);
    }

    // Given: Dual.Start 待ちで Connecting のセッション
    // When: Stop を経ずに Dispose する
    // Then: Start は cancel で終わり、呼び出し側もセッションも例外にしない
    [Fact]
    public async Task DisposeDuringConnectingCancelsStartWithoutThrowing()
    {
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeDualClient { StartGate = startGate };
        var session = NewSession(client);

        var startTask = session.StartAsync();
        await WaitUntilAsync(() =>
            client.StartCount == 1 && session.State == TranslationState.Connecting);

        session.Dispose();
        startGate.TrySetResult();
        await startTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotEqual(TranslationState.Error, session.State);
        Assert.Equal(1, client.StartCount);
    }

    private static InterpretationSession NewSession(
        FakeDualClient client,
        TimeProvider? clock = null) =>
        new(
            new FakeApiKeyStore("sk-test"),
            new FakeAudioCapture(),
            client,
            timeProvider: clock,
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

    private sealed class ControllableTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private long _utcTicks = start.UtcTicks;

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _utcTicks), TimeSpan.Zero);

        public void Advance(TimeSpan delta) => Interlocked.Add(ref _utcTicks, delta.Ticks);
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

        public TaskCompletionSource? StartGate { get; set; }

        public TaskCompletionSource? SelectTargetGate { get; set; }

        public TaskCompletionSource? EnteredSelectTarget { get; set; }

        public TaskCompletionSource? ResetAudioRoutingGate { get; set; }

        public TaskCompletionSource? EnteredResetAudioRouting { get; set; }

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
                _events = Channel.CreateUnbounded<RealtimeTranslationStreamEvent>();
            }
        }

        public Task AppendAudioFrameAsync(
            ReadOnlyMemory<byte> pcm16LittleEndian,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async Task SelectTranslationTargetAsync(
            RealtimeTranslationOutputLanguage? target,
            CancellationToken cancellationToken = default)
        {
            EnteredSelectTarget?.TrySetResult();
            var gateTask = SelectTargetGate?.Task;
            if (gateTask is not null)
            {
                await gateTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            lock (_sync)
            {
                if (target is { } selected
                    && LastStartedPair is { } pair
                    && pair.Counterpart(selected) is { } spoken)
                {
                    _spokenLanguages.Add(spoken);
                }
            }
        }

        public Task UpdateTranscriptionTuningAsync(
            RealtimeSessionTuning tuning,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async Task ResetAudioRoutingAsync()
        {
            EnteredResetAudioRouting?.TrySetResult();
            var gateTask = ResetAudioRoutingGate?.Task;
            if (gateTask is not null)
            {
                await gateTask.ConfigureAwait(false);
            }
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

        public void PublishSourceDelta(string delta) => PublishLane(
            RealtimeTranslationLane.Source,
            new RealtimeTranslationServerEvent.InputTranscriptDelta(delta, Guid.NewGuid().ToString(), null));

        public void PublishTranslationDelta(RealtimeTranslationOutputLanguage target, string delta) => PublishLane(
            RealtimeTranslationLane.Translation(target),
            new RealtimeTranslationServerEvent.OutputTranscriptDelta(delta, Guid.NewGuid().ToString(), null));

        private void Complete()
        {
            lock (_sync)
            {
                _events.Writer.TryComplete();
            }
        }

        private void PublishLane(
            RealtimeTranslationLane lane,
            RealtimeTranslationServerEvent serverEvent)
        {
            lock (_sync)
            {
                _events.Writer.TryWrite(
                    new RealtimeTranslationStreamEvent(lane, serverEvent, _epoch));
            }
        }
    }
}
