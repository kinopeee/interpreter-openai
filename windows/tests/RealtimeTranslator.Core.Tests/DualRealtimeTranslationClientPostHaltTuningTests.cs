using System;
using System.Collections.Generic;
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

/// <summary>
/// 翻訳ポンプ halt 後も原文 tuning は生きること、および
/// 未開始 / ForceClose 後の ResetAudioRouting が throw しないこと。
/// HaltTests 本体を触る開いている coverage PR とは交差しない。
/// </summary>
public sealed class DualRealtimeTranslationClientPostHaltTuningTests
{
    // Given: 翻訳送信が 3 回連続失敗してポンプが halt した Dual
    // When: prompt/delay を live update してから原文 frame を送る
    // Then: source へ session.update が届き原文は継続する。halt した翻訳 lane へは流れない
    [Fact]
    public async Task UpdateTranscriptionTuningAfterHaltStillUpdatesSource()
    {
        await using var harness = await HaltHarness.StartAsync();
        await harness.SelectAsync(RealtimeTranslationOutputLanguage.English);
        await harness.AppendAsync("ok1");

        harness.English.FailNextSend();
        await harness.AppendAsync("fail1");
        harness.English.FailNextSend();
        await harness.AppendAsync("fail2");
        harness.English.FailNextSend();
        await harness.AppendAsync("fail3");
        Assert.Equal(1, harness.DrainTransportErrorCount());

        var sentBefore = harness.Source.Sent.Count;
        await harness.Dual.UpdateTranscriptionTuningAsync(
            new RealtimeSessionTuning(
                RealtimeTranslationNoiseReduction.NearField,
                RealtimeTranscriptionDelay.High,
                "Post-halt glossary",
                ["Acme"]));
        await WaitUntilSentAsync(harness.Source, sentBefore + 1);

        var transcription = SessionUpdates(harness.Source)[^1]
            ["session"]!["audio"]!["input"]!["transcription"]!.AsObject();
        Assert.Equal("Post-halt glossary", transcription["prompt"]!.GetValue<string>());
        Assert.Equal("high", transcription["delay"]!.GetValue<string>());

        await harness.AppendAsync("afterTuning");

        Assert.Contains("afterTuning", harness.Source.AppendedFrameTexts());
        Assert.Equal(["ok1"], harness.English.AppendedFrameTexts());
        Assert.Empty(harness.Japanese.AppendedFrameTexts());
        Assert.Equal(0, harness.DrainTransportErrorCount());
    }

    // Given: Start していない Dual
    // When: ResetAudioRouting する
    // Then: throw せず未接続のまま。Select はこれまで通り NotConnected
    [Fact]
    public async Task ResetAudioRoutingBeforeStartIsNoOp()
    {
        var source = new FakeRealtimeServerTransport();
        var english = new FakeRealtimeServerTransport();
        var japanese = new FakeRealtimeServerTransport();
        using var dual = CreateDual(source, english, japanese);

        await dual.ResetAudioRoutingAsync();

        Assert.Equal(0, source.ConnectCount);
        var selectError = await Assert.ThrowsAsync<RealtimeTranslationException>(
            () => dual.SelectTranslationTargetAsync(RealtimeTranslationOutputLanguage.English));
        Assert.Equal(RealtimeTranslationErrorKind.NotConnected, selectError.Kind);
    }

    // Given: ForceClose 済みの Dual
    // When: ResetAudioRouting する
    // Then: throw せず、その後の Select は NotConnected のまま
    [Fact]
    public async Task ResetAudioRoutingAfterForceCloseIsNoOp()
    {
        var source = new FakeRealtimeServerTransport();
        var english = new FakeRealtimeServerTransport();
        var japanese = new FakeRealtimeServerTransport();
        using var dual = CreateDual(source, english, japanese);

        await dual.StartAsync("sk-test", RealtimeSessionTuning.Default);
        await dual.ForceCloseAsync();

        await dual.ResetAudioRoutingAsync();

        var selectError = await Assert.ThrowsAsync<RealtimeTranslationException>(
            () => dual.SelectTranslationTargetAsync(RealtimeTranslationOutputLanguage.English));
        Assert.Equal(RealtimeTranslationErrorKind.NotConnected, selectError.Kind);
    }

    private static DualRealtimeTranslationClient CreateDual(
        FakeRealtimeServerTransport source,
        FakeRealtimeServerTransport english,
        FakeRealtimeServerTransport japanese) =>
        new(
            new RealtimeSourceTranscriptionConnection(source, "test-safety"),
            new RealtimeTranslationConnection(
                RealtimeTranslationOutputLanguage.English,
                english,
                "test-safety"),
            new RealtimeTranslationConnection(
                RealtimeTranslationOutputLanguage.Japanese,
                japanese,
                "test-safety"));

    private static List<JsonObject> SessionUpdates(FakeRealtimeServerTransport transport) =>
        transport.Sent
            .Select(payload => JsonNode.Parse(payload)?.AsObject())
            .Where(node => node?["type"]?.GetValue<string>() == "session.update")
            .Select(node => node!)
            .ToList();

    private static async Task WaitUntilSentAsync(FakeRealtimeServerTransport transport, int minimum)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (transport.Sent.Count < minimum)
        {
            timeout.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class HaltHarness : IAsyncDisposable
    {
        private HaltHarness(
            FakeRealtimeServerTransport source,
            FakeRealtimeServerTransport english,
            FakeRealtimeServerTransport japanese,
            DualRealtimeTranslationClient dual)
        {
            Source = source;
            English = english;
            Japanese = japanese;
            Dual = dual;
        }

        public FakeRealtimeServerTransport Source { get; }

        public FakeRealtimeServerTransport English { get; }

        public FakeRealtimeServerTransport Japanese { get; }

        public DualRealtimeTranslationClient Dual { get; }

        public static async Task<HaltHarness> StartAsync()
        {
            var source = new FakeRealtimeServerTransport();
            var english = new FakeRealtimeServerTransport();
            var japanese = new FakeRealtimeServerTransport();
            var dual = new DualRealtimeTranslationClient(
                new RealtimeSourceTranscriptionConnection(source, "test-safety"),
                new RealtimeTranslationConnection(
                    RealtimeTranslationOutputLanguage.English,
                    english,
                    "test-safety"),
                new RealtimeTranslationConnection(
                    RealtimeTranslationOutputLanguage.Japanese,
                    japanese,
                    "test-safety"));

            await dual.StartAsync("sk-test", RealtimeSessionTuning.Default, LanguagePair.JaEn);
            return new HaltHarness(source, english, japanese, dual);
        }

        public async Task AppendAsync(string frameName)
        {
            await Dual.AppendAudioFrameAsync(Encoding.UTF8.GetBytes(frameName));
            await Dual.WaitForTranslationDrainAsync();
        }

        public async Task SelectAsync(RealtimeTranslationOutputLanguage target)
        {
            await Dual.SelectTranslationTargetAsync(target);
            await Dual.WaitForTranslationDrainAsync();
        }

        public int DrainTransportErrorCount()
        {
            var count = 0;
            while (Dual.Events.TryRead(out var streamEvent))
            {
                if (streamEvent.Event is RealtimeTranslationServerEvent.ServerError error
                    && error.Code == DualRealtimeTranslationClient.TransportErrorCode)
                {
                    count += 1;
                }
            }

            return count;
        }

        public async ValueTask DisposeAsync() => await Dual.ForceCloseAsync();
    }
}
