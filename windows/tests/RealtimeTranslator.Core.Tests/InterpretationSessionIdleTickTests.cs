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
/// assembler の idle Tick がセッションの ResetAudioRouting に繋がること。
/// InterpretationSessionTests の開いている PR とはファイルを分けて衝突を避ける。
/// </summary>
public sealed class InterpretationSessionIdleTickTests
{
    // Given: Listening 中に原文と訳文が揃った完全ペア
    // When: idle finalize 間隔を超えて Tick する
    // Then: ShouldFinalize し、セグメント境界として ResetAudioRouting する
    [Fact]
    public async Task IdleTickFinalizesCompletePairAndResetsAudioRouting()
    {
        var client = new FakeDualClient();
        var clock = new ControllableTimeProvider(DateTimeOffset.Parse("2026-08-16T00:00:00Z"));
        using var session = NewSession(client, clock);
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
        var resetsAfterPair = client.ResetAudioRoutingCount;

        clock.Advance(RealtimeSubtitleAssembler.IdleFinalizeInterval + TimeSpan.FromMilliseconds(50));

        await WaitUntilAsync(() =>
        {
            lock (updates)
            {
                return updates.Exists(update => update.ShouldFinalize)
                    && client.ResetAudioRoutingCount > resetsAfterPair;
            }
        });

        RealtimeSubtitleUpdate finalized;
        lock (updates)
        {
            finalized = updates.Find(update => update.ShouldFinalize);
        }

        Assert.True(finalized.ShouldFinalize);
        Assert.Equal("これはテストです", finalized.SourceText);
        Assert.Equal("This is a test", finalized.TranslatedText);
        Assert.True(client.ResetAudioRoutingCount > resetsAfterPair);
        await session.StopAsync();
    }

    // Given: 原文だけ届いて訳文が無い Listening セッション
    // When: idle finalize 間隔を超えて Tick する
    // Then: 不完全ペアは確定せず、ResetAudioRouting も増えない
    [Fact]
    public async Task IdleTickDoesNotResetRoutingForIncompletePair()
    {
        var client = new FakeDualClient();
        var clock = new ControllableTimeProvider(DateTimeOffset.Parse("2026-08-16T00:00:00Z"));
        using var session = NewSession(client, clock);
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
        var resetsAfterSource = client.ResetAudioRoutingCount;

        clock.Advance(RealtimeSubtitleAssembler.IdleFinalizeInterval + TimeSpan.FromSeconds(1));
        await Task.Delay(80);

        lock (updates)
        {
            Assert.DoesNotContain(updates, update => update.ShouldFinalize);
        }

        Assert.Equal(resetsAfterSource, client.ResetAudioRoutingCount);
        await session.StopAsync();
    }

    // Given: 日本語のあと曖昧なラテン1語と全文の訳文がある Listening セッション
    // When: idle finalize 間隔を超えて Tick する
    // Then: 境界候補が残っていても完全ペアを確定する
    [Fact]
    public async Task IdleTickFinalizesCompletePairWithAmbiguousLatinTail()
    {
        var client = new FakeDualClient();
        var clock = new ControllableTimeProvider(DateTimeOffset.Parse("2026-08-16T00:00:00Z"));
        using var session = NewSession(client, clock);
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
        client.PublishSourceDelta("今日は");
        await WaitUntilAsync(() => client.SpokenLanguages.Count > 0);
        client.PublishSourceDelta(" OpenAI");
        await WaitUntilAsync(() =>
        {
            lock (updates)
            {
                return updates.Exists(update =>
                    update.SourceText.Contains("OpenAI", StringComparison.Ordinal));
            }
        });
        client.PublishTranslationDelta(
            RealtimeTranslationOutputLanguage.English,
            "Today it is OpenAI");
        await WaitUntilAsync(() =>
        {
            lock (updates)
            {
                return updates.Exists(update =>
                    update.TranslatedText.Contains("OpenAI", StringComparison.Ordinal)
                    && !update.ShouldFinalize);
            }
        });

        clock.Advance(RealtimeSubtitleAssembler.IdleFinalizeInterval + TimeSpan.FromMilliseconds(50));
        await WaitUntilAsync(() =>
        {
            lock (updates)
            {
                return updates.Exists(update =>
                    update.ShouldFinalize
                    && update.SourceText == "今日は OpenAI"
                    && update.TranslatedText == "Today it is OpenAI");
            }
        });
        await session.StopAsync();
    }

    private static InterpretationSession NewSession(FakeDualClient client, TimeProvider clock) =>
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

        public int ResetAudioRoutingCount { get; private set; }

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
                LastStartedPair = pair;
                _epoch += 1;
                DeliveryState = new EventDeliveryState(_epoch);
                _spokenLanguages.Clear();
                ResetAudioRoutingCount = 0;
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
