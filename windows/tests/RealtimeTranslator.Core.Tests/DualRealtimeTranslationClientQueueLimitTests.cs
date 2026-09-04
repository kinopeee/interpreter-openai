using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using RealtimeTranslator.Core.Audio;
using RealtimeTranslator.Core.OpenAI;
using RealtimeTranslator.Core.Realtime;
using Xunit;

namespace RealtimeTranslator.Core.Tests;

public sealed class DualRealtimeTranslationClientQueueLimitTests
{
    public static TheoryData<string> Boundaries =>
        new(SharedFixtures.Load("translation-queue")["boundaries"]!.AsArray()
            .Select(node => SharedFixtures.Text(node!["name"])));

    // Given: translation-queue.json and the localized backlog error
    // When: the shared constants and copy are read
    // Then: the Windows implementation matches the shared contract
    [Fact]
    public void ConstantsMatchFixture()
    {
        var fixture = SharedFixtures.Load("translation-queue");
        Assert.Equal(SharedFixtures.Number(fixture["pendingFrameLimit"]), DualRealtimeTranslationClient.TranslationPendingFrameLimit);
        Assert.Equal(SharedFixtures.Number(fixture["prerollFrameLimit"]), DualRealtimeTranslationClient.TranslationPrerollFrameLimit);
        var overflow = fixture["overflow"]!.AsObject();
        Assert.Equal(SharedFixtures.Text(overflow["errorCode"]), DualRealtimeTranslationClient.TransportErrorCode);
        Assert.Equal("翻訳音声の送信待ちが上限に達しました。", DualRealtimeTranslationClient.TranslationBacklogErrorMessage);
    }

    // Given: a running client without a selected translation target
    // When: no audio frames are appended
    // Then: no translation sends or transport errors occur
    [Fact]
    public async Task Q01NoTargetHasNoTranslationSends()
    {
        await using var harness = await QueueHarness.StartAsync();
        await Task.Delay(20);
        Assert.Empty(harness.English.AppendedFrameTexts());
        Assert.Empty(harness.DrainErrors());
        Assert.Equal(0, harness.Dual.PendingTranslationFrameCountForTests);
    }

    // Given: an in-flight frame and a fixture boundary
    // When: the boundary is crossed with one additional frame
    // Then: pending frames and the halt/error state match the fixture
    [Theory]
    [MemberData(nameof(Boundaries))]
    public async Task Q02ToQ04BoundariesMatchFixture(string name)
    {
        var boundary = SharedFixtures.Load("translation-queue")["boundaries"]!.AsArray()
            .Select(node => node!.AsObject())
            .Single(node => SharedFixtures.Text(node["name"]) == name);
        var pendingBefore = SharedFixtures.Number(boundary["pendingBefore"]);
        await using var harness = await QueueHarness.StartAsync();
        await harness.SelectAsync(RealtimeTranslationOutputLanguage.English);
        harness.English.HoldAudioAppends = true;
        await harness.AppendAsync("in-flight");
        await WaitUntilAsync(() => harness.English.HeldAudioAppendCount == 1);
        for (var index = 0; index < pendingBefore; index += 1)
        {
            await harness.AppendAsync($"pending-{index}");
        }

        await harness.AppendAsync("boundary");
        var expectedPending = SharedFixtures.Number(boundary["expectedPending"]);
        var expectedHalted = SharedFixtures.Flag(boundary["expectedHalted"]!);
        var expectedErrors = SharedFixtures.Number(boundary["expectedTransportErrorCount"]);
        Assert.Equal(expectedPending, harness.Dual.PendingTranslationFrameCountForTests);
        Assert.Equal(expectedHalted, harness.Dual.IsTranslationPumpHaltedForTests);
        var errors = harness.DrainErrors();
        Assert.Equal(expectedErrors, errors.Count);
        if (expectedHalted)
        {
            var error = Assert.Single(errors);
            Assert.Equal(DualRealtimeTranslationClient.TranslationBacklogErrorMessage, error.Message);
            Assert.Equal(DualRealtimeTranslationClient.TransportErrorCode, error.Code);
            Assert.Equal(harness.Dual.ConnectionEpoch, error.Epoch);
        }

        Assert.Equal(pendingBefore + 2, harness.Source.AppendedFrameTexts().Count);
        harness.English.HoldAudioAppends = false;
        harness.English.ReleaseAllAudioAppends();
        await harness.Dual.WaitForTranslationDrainAsync();
        var expectedSends = expectedHalted ? 1 : pendingBefore + 2;
        Assert.Equal(expectedSends, harness.English.AppendedFrameTexts().Count);
    }

    // Given: the rolling preroll is full and no target is selected
    // When: a target is selected
    // Then: all preroll frames flush in order without an error
    [Fact]
    public async Task Q05PrerollFlushesToSelectedTarget()
    {
        var fixture = SharedFixtures.Load("translation-queue")["prerollFlush"]!.AsObject();
        await using var harness = await QueueHarness.StartAsync();
        var count = SharedFixtures.Number(fixture["frameCount"]);
        for (var index = 0; index < count; index += 1)
        {
            await harness.AppendAsync($"frame-{index}");
        }

        await harness.SelectAsync(RealtimeTranslationOutputLanguage.English);
        Assert.Equal(Enumerable.Range(0, count).Select(index => $"frame-{index}"), harness.English.AppendedFrameTexts());
        Assert.Empty(harness.Japanese.AppendedFrameTexts());
        Assert.Empty(harness.Spanish.AppendedFrameTexts());
        Assert.Empty(harness.DrainErrors());
        Assert.Equal(0, harness.Dual.PendingTranslationFrameCountForTests);
    }

    // Given: an overflow has halted the translation pump
    // When: source frames continue to arrive
    // Then: source sends grow while translation sends and errors stay unchanged
    [Fact]
    public async Task Q06AfterOverflowSourceContinues()
    {
        await using var harness = await QueueHarness.StartAsync();
        await harness.OverflowAsync();
        var errors = harness.DrainErrors();
        var sends = harness.English.AppendedFrameTexts().Count;
        for (var index = 0; index < 5; index += 1)
        {
            await harness.AppendAsync($"after-{index}");
        }

        Assert.Equal(sends, harness.English.AppendedFrameTexts().Count);
        Assert.Single(errors);
        Assert.Equal(87, harness.Source.AppendedFrameTexts().Count);
        Assert.Equal(0, harness.Dual.PendingTranslationFrameCountForTests);
    }

    // Given: overflow has halted one target
    // When: a sibling target is selected
    // Then: the halted pump does not resume
    [Fact]
    public async Task Q07TargetChangeDoesNotResumeAfterOverflow()
    {
        await using var harness = await QueueHarness.StartAsync();
        await harness.OverflowAsync();
        var errors = harness.DrainErrors();
        await harness.SelectAsync(RealtimeTranslationOutputLanguage.Japanese);
        Assert.Empty(harness.Japanese.AppendedFrameTexts());
        Assert.True(harness.Dual.IsTranslationPumpHaltedForTests);
        Assert.Single(errors);
    }

    // Given: overflow has halted translation
    // When: audio routing is reset and the target is selected again
    // Then: the pump remains halted
    [Fact]
    public async Task Q08RoutingResetDoesNotResumeAfterOverflow()
    {
        await using var harness = await QueueHarness.StartAsync();
        await harness.OverflowAsync();
        var errors = harness.DrainErrors();
        await harness.Dual.ResetAudioRoutingAsync();
        await harness.SelectAsync(RealtimeTranslationOutputLanguage.English);
        Assert.Empty(harness.English.AppendedFrameTexts().Skip(1));
        Assert.True(harness.Dual.IsTranslationPumpHaltedForTests);
        Assert.Single(errors);
    }

    // Given: overflow occurs while the in-flight append is held
    // When: that append fails
    // Then: only the backlog error is published
    [Fact]
    public async Task Q09InFlightFailureDoesNotDuplicateOverflowError()
    {
        await using var harness = await QueueHarness.StartAsync();
        harness.English.HoldAudioAppends = true;
        await harness.SelectAsync(RealtimeTranslationOutputLanguage.English);
        await harness.AppendAsync("in-flight");
        await WaitUntilAsync(() => harness.English.HeldAudioAppendCount == 1);
        for (var index = 0; index < 81; index += 1)
        {
            await harness.AppendAsync($"queued-{index}");
        }

        await WaitUntilAsync(() => harness.TransportErrorCount() == 1);
        harness.English.FailOneHeldAudioAppend();
        harness.English.ReleaseAllAudioAppends();
        await WaitUntilAsync(() => harness.English.HeldAudioAppendCount == 0);
        var errors = harness.DrainErrors();
        Assert.Empty(errors);
        Assert.True(harness.Dual.IsTranslationPumpHaltedForTests);
    }

    // Given: overflow has halted translation with one in-flight append
    // When: the in-flight append completes
    // Then: no later frame is sent
    [Fact]
    public async Task Q10CompletedInFlightFrameDoesNotResumePump()
    {
        await using var harness = await QueueHarness.StartAsync();
        await harness.OverflowAsync();
        harness.English.ReleaseOneAudioAppend();
        await harness.Dual.WaitForTranslationDrainAsync();
        var sent = harness.English.AppendedFrameTexts().Count;
        await harness.AppendAsync("after");
        Assert.Equal(1, sent);
        Assert.Equal(sent, harness.English.AppendedFrameTexts().Count);
    }

    // Given: overflow has halted translation
    // When: graceful close drains and closes the lanes
    // Then: close completes without a second translation append
    [Fact]
    public async Task Q11GracefulCloseCompletesAfterOverflow()
    {
        await using var harness = await QueueHarness.StartAsync(autoCloseResponses: true);
        await harness.OverflowAsync();
        harness.English.HoldAudioAppends = false;
        harness.English.ReleaseAllAudioAppends();
        await harness.Dual.WaitForTranslationDrainAsync();
        var before = harness.English.AppendedFrameTexts().Count;
        var stopwatch = Stopwatch.StartNew();
        await harness.Dual.CloseGracefullyAsync();
        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5));
        Assert.Equal(before, harness.English.AppendedFrameTexts().Count);
    }

    // Given: translation has halted due to overflow
    // When: the client is started again
    // Then: a new epoch resumes translation
    [Fact]
    public async Task Q12StartAgainRecoversTranslation()
    {
        await using var harness = await QueueHarness.StartAsync();
        await harness.OverflowAsync();
        harness.English.HoldAudioAppends = false;
        harness.English.ReleaseAllAudioAppends();
        await harness.Dual.StartAsync("sk-test", RealtimeSessionTuning.Default, LanguagePair.JaEn);
        await harness.SelectAsync(RealtimeTranslationOutputLanguage.English);
        await harness.AppendAsync("recovered");
        Assert.Contains("recovered", harness.English.AppendedFrameTexts());
        Assert.False(harness.Dual.IsTranslationPumpHaltedForTests);
        Assert.Equal(0, harness.Dual.PendingTranslationFrameCountForTests);
    }

    // Given: a held translation append
    // When: appends are released periodically until overflow
    // Then: the bounded queue halts without timing out and restart recovers
    [Fact]
    public async Task Q14IntermittentReleaseStillBoundsQueue()
    {
        await using var harness = await QueueHarness.StartAsync();
        await harness.SelectAsync(RealtimeTranslationOutputLanguage.English);
        harness.English.HoldAudioAppends = true;
        var appended = 0;
        while (harness.DrainErrors().Count == 0 && appended < 400)
        {
            await harness.AppendAsync($"loop-{appended}");
            appended += 1;
            if (appended % 2 == 0)
            {
                harness.English.ReleaseOneAudioAppend();
            }
        }

        Assert.True(harness.Dual.IsTranslationPumpHaltedForTests);
        Assert.Equal(0, harness.Dual.PendingTranslationFrameCountForTests);
        Assert.True(harness.English.AppendedFrameTexts().Count < appended);
        harness.English.ReleaseAllAudioAppends();
        harness.English.HoldAudioAppends = false;
        await harness.Dual.StartAsync("sk-test", RealtimeSessionTuning.Default, LanguagePair.JaEn);
        await harness.SelectAsync(RealtimeTranslationOutputLanguage.English);
        await harness.AppendAsync("recovered");
        Assert.Contains("recovered", harness.English.AppendedFrameTexts());
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            timeout.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class QueueHarness : IAsyncDisposable
    {
        private QueueHarness(
            FakeRealtimeServerTransport source,
            FakeRealtimeServerTransport english,
            FakeRealtimeServerTransport japanese,
            FakeRealtimeServerTransport spanish,
            DualRealtimeTranslationClient dual)
        {
            Source = source;
            English = english;
            Japanese = japanese;
            Spanish = spanish;
            Dual = dual;
        }

        public FakeRealtimeServerTransport Source { get; }
        public FakeRealtimeServerTransport English { get; }
        public FakeRealtimeServerTransport Japanese { get; }
        public FakeRealtimeServerTransport Spanish { get; }
        public DualRealtimeTranslationClient Dual { get; }

        public static async Task<QueueHarness> StartAsync(bool autoCloseResponses = false)
        {
            var source = new FakeRealtimeServerTransport { AutoCloseResponses = autoCloseResponses };
            var english = new FakeRealtimeServerTransport { AutoCloseResponses = autoCloseResponses };
            var japanese = new FakeRealtimeServerTransport { AutoCloseResponses = autoCloseResponses };
            var spanish = new FakeRealtimeServerTransport { AutoCloseResponses = autoCloseResponses };
            var dual = new DualRealtimeTranslationClient(
                new RealtimeSourceTranscriptionConnection(source, "test-safety"),
                new RealtimeTranslationConnection(RealtimeTranslationOutputLanguage.English, english, "test-safety"),
                new RealtimeTranslationConnection(RealtimeTranslationOutputLanguage.Japanese, japanese, "test-safety"),
                spanishConnection: new RealtimeTranslationConnection(
                    RealtimeTranslationOutputLanguage.Spanish, spanish, "test-safety"));
            await dual.StartAsync("sk-test", RealtimeSessionTuning.Default, LanguagePair.JaEn);
            return new QueueHarness(source, english, japanese, spanish, dual);
        }

        public async Task SelectAsync(RealtimeTranslationOutputLanguage target)
        {
            await Dual.SelectTranslationTargetAsync(target);
            if (!English.HoldAudioAppends)
            {
                await Dual.WaitForTranslationDrainAsync();
            }
        }

        public async Task AppendAsync(string text)
        {
            await Dual.AppendAudioFrameAsync(Encoding.UTF8.GetBytes(text));
            if (!English.HoldAudioAppends)
            {
                await Dual.WaitForTranslationDrainAsync();
            }
        }

        public async Task OverflowAsync()
        {
            await SelectAsync(RealtimeTranslationOutputLanguage.English);
            English.HoldAudioAppends = true;
            await AppendAsync("in-flight");
            await WaitUntilAsync(() => English.HeldAudioAppendCount == 1);
            for (var index = 0; index < 81; index += 1)
            {
                await AppendAsync($"queued-{index}");
            }
            await WaitUntilAsync(() => Dual.IsTranslationPumpHaltedForTests);
        }

        public List<TransportError> DrainErrors()
        {
            var errors = new List<TransportError>();
            while (Dual.Events.TryRead(out var streamEvent))
            {
                if (streamEvent.Event is RealtimeTranslationServerEvent.ServerError error
                    && error.Code == DualRealtimeTranslationClient.TransportErrorCode)
                {
                    errors.Add(new TransportError(error.Message, error.Code, streamEvent.Epoch));
                }
            }

            return errors;
        }

        public int TransportErrorCount()
        {
            var count = 0;
            while (Dual.Events.TryRead(out var streamEvent))
            {
                if (streamEvent.Event is RealtimeTranslationServerEvent.ServerError { Code: "transport" })
                {
                    count += 1;
                }
            }

            return count;
        }

        public async ValueTask DisposeAsync() => await Dual.ForceCloseAsync();
    }

    private sealed record TransportError(string Message, string Code, int Epoch);
}
