using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using RealtimeTranslator.Core.Audio;
using RealtimeTranslator.Core.OpenAI;
using RealtimeTranslator.Core.Realtime;
using Xunit;

namespace RealtimeTranslator.Core.Tests;

/// <summary>
/// ja-en の翻訳 halt、成功による連続失敗カウンタのリセット、
/// セグメント境界の ResetAudioRouting、halt 後の兄弟 lane 選択、
/// Start（再接続）だけが再開する契約。
/// routing.json の英語 lane / スペイン語ペア halt とは交差しない。
/// </summary>
public sealed class DualRealtimeTranslationClientHaltTests
{
    // Given: ja-en で英語話者向け target=ja に切替済み
    // When: 翻訳送信が 3 回連続で失敗する
    // Then: transport error を 1 回出し、以降の frame は日本語 lane へ流さない（原文は継続）
    [Fact]
    public async Task JaEnJapaneseLaneThreeConsecutiveSendFailuresHaltTranslation()
    {
        await using var harness = await HaltHarness.StartAsync();
        await harness.SelectAsync(RealtimeTranslationOutputLanguage.Japanese);
        await harness.AppendAsync("ok1");

        harness.Japanese.FailNextSend();
        await harness.AppendAsync("fail1");
        harness.Japanese.FailNextSend();
        await harness.AppendAsync("fail2");
        harness.Japanese.FailNextSend();
        await harness.AppendAsync("fail3");
        await harness.AppendAsync("afterHalt");

        Assert.Equal(["ok1", "fail1", "fail2", "fail3", "afterHalt"], harness.Source.AppendedFrameTexts());
        Assert.Equal(["ok1"], harness.Japanese.AppendedFrameTexts());
        Assert.Empty(harness.English.AppendedFrameTexts());
        Assert.Equal(1, harness.DrainTransportErrorCount());

        var japaneseBeforeProbe = harness.Japanese.AppendedFrameTexts();
        await harness.AppendAsync("probeAfterHalt");
        Assert.Equal(japaneseBeforeProbe, harness.Japanese.AppendedFrameTexts());
        Assert.Contains("probeAfterHalt", harness.Source.AppendedFrameTexts());
    }

    // Given: 翻訳送信が 2 回連続失敗した直後
    // When: ResetAudioRouting したあと preroll flush の最初の 1 回だけ失敗させる
    // Then: 連続失敗カウンタは 0 から数え直し、3 回目相当では halt しない
    [Fact]
    public async Task ResetAudioRoutingClearsConsecutiveTranslationFailureCounter()
    {
        await using var harness = await HaltHarness.StartAsync();
        await harness.SelectAsync(RealtimeTranslationOutputLanguage.English);
        await harness.AppendAsync("ok1");

        harness.English.FailNextSend();
        await harness.AppendAsync("fail1");
        harness.English.FailNextSend();
        await harness.AppendAsync("fail2");

        await harness.Dual.ResetAudioRoutingAsync();
        harness.English.FailNextSend();
        await harness.SelectAsync(RealtimeTranslationOutputLanguage.English);
        await harness.AppendAsync("afterReset");

        Assert.Equal(0, harness.DrainTransportErrorCount());
        Assert.Contains("afterReset", harness.English.AppendedFrameTexts());

        var englishBeforeProbe = harness.English.AppendedFrameTexts();
        await harness.AppendAsync("stillFlowing");
        Assert.NotEqual(englishBeforeProbe, harness.English.AppendedFrameTexts());
        Assert.Contains("stillFlowing", harness.English.AppendedFrameTexts());
    }

    // Given: 翻訳送信が 2 回連続失敗した Dual
    // When: 1 回成功したあと、さらに 2 回失敗させる
    // Then: 成功で連続失敗カウンタが 0 に戻り、3 回目相当では halt しない
    [Fact]
    public async Task SuccessfulSendResetsConsecutiveTranslationFailureCounter()
    {
        await using var harness = await HaltHarness.StartAsync();
        await harness.SelectAsync(RealtimeTranslationOutputLanguage.English);
        await harness.AppendAsync("ok1");

        harness.English.FailNextSend();
        await harness.AppendAsync("fail1");
        harness.English.FailNextSend();
        await harness.AppendAsync("fail2");

        await harness.AppendAsync("recovered");
        Assert.Equal(0, harness.DrainTransportErrorCount());
        Assert.Contains("recovered", harness.English.AppendedFrameTexts());

        harness.English.FailNextSend();
        await harness.AppendAsync("fail3");
        harness.English.FailNextSend();
        await harness.AppendAsync("fail4");

        Assert.Equal(0, harness.DrainTransportErrorCount());
        var englishBeforeProbe = harness.English.AppendedFrameTexts();
        await harness.AppendAsync("stillFlowing");
        Assert.Contains("stillFlowing", harness.English.AppendedFrameTexts());
        Assert.NotEqual(englishBeforeProbe, harness.English.AppendedFrameTexts());
    }

    // Given: 翻訳送信が 3 回連続失敗してポンプが halt した Dual
    // When: セグメント境界相当の ResetAudioRouting のあと、同じ target を選び直して frame を送る
    // Then: halt は残り翻訳 lane へは流れない（死にかけ socket へ再開しない）。原文は継続する
    [Fact]
    public async Task ResetAudioRoutingAfterHaltDoesNotResumeTranslation()
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

        await harness.Dual.ResetAudioRoutingAsync();
        await harness.SelectAsync(RealtimeTranslationOutputLanguage.English);
        await harness.AppendAsync("afterReset");

        Assert.Equal(["ok1"], harness.English.AppendedFrameTexts());
        Assert.Contains("afterReset", harness.Source.AppendedFrameTexts());
        Assert.Empty(harness.Japanese.AppendedFrameTexts());
        Assert.Equal(0, harness.DrainTransportErrorCount());
    }

    // Given: 英語 lane の翻訳送信が 3 回連続失敗してポンプが halt した Dual
    // When: Reset せずに日本語 lane を選んで frame を送る
    // Then: halt は Dual 全体なので日本語 lane にも流れない。原文は継続する
    [Fact]
    public async Task HaltDoesNotResumeWhenSelectingSiblingLane()
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

        await harness.SelectAsync(RealtimeTranslationOutputLanguage.Japanese);
        await harness.AppendAsync("afterSiblingSelect");

        Assert.Equal(["ok1"], harness.English.AppendedFrameTexts());
        Assert.Empty(harness.Japanese.AppendedFrameTexts());
        Assert.Contains("afterSiblingSelect", harness.Source.AppendedFrameTexts());
        Assert.Equal(0, harness.DrainTransportErrorCount());
    }

    // Given: 翻訳ポンプが halt した Dual
    // When: StartAsync で再接続し、同じ target へ frame を送る
    // Then: halt が解け、新しい接続の翻訳 lane へ届く（再接続後の字幕欠落を防ぐ）
    [Fact]
    public async Task StartAfterHaltResumesTranslationPump()
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
        var connectCountAfterHalt = harness.English.ConnectCount;

        await harness.Dual.StartAsync("sk-test", RealtimeSessionTuning.Default, LanguagePair.JaEn);
        await harness.SelectAsync(RealtimeTranslationOutputLanguage.English);
        await harness.AppendAsync("afterReconnect");

        Assert.True(harness.English.ConnectCount > connectCountAfterHalt);
        Assert.Contains("afterReconnect", harness.English.AppendedFrameTexts());
        Assert.Contains("afterReconnect", harness.Source.AppendedFrameTexts());
        Assert.Equal(0, harness.DrainTransportErrorCount());
    }

    // Given: 翻訳ポンプが halt した Dual（graceful close 応答あり）
    // When: CloseGracefullyAsync する
    // Then: halt していても原文は commit、翻訳 lane は session.close を送り、Events を完了する
    [Fact]
    public async Task CloseGracefullyAfterHaltStillClosesAllLanes()
    {
        await using var harness = await HaltHarness.StartAsync(autoCloseResponses: true);
        await harness.SelectAsync(RealtimeTranslationOutputLanguage.English);
        await harness.AppendAsync("ok1");

        harness.English.FailNextSend();
        await harness.AppendAsync("fail1");
        harness.English.FailNextSend();
        await harness.AppendAsync("fail2");
        harness.English.FailNextSend();
        await harness.AppendAsync("fail3");
        Assert.Equal(1, harness.DrainTransportErrorCount());

        await harness.Dual.CloseGracefullyAsync();

        Assert.Contains("input_audio_buffer.commit", SentTypes(harness.Source));
        Assert.Contains("session.close", SentTypes(harness.English));
        Assert.Contains("session.close", SentTypes(harness.Japanese));
        while (harness.Dual.Events.TryRead(out _))
        {
        }

        Assert.False(await harness.Dual.Events.WaitToReadAsync());
    }

    private static List<string> SentTypes(FakeRealtimeServerTransport transport)
    {
        var types = new List<string>();
        foreach (var payload in transport.Sent)
        {
            var type = System.Text.Json.Nodes.JsonNode.Parse(payload)?.AsObject()["type"]?.GetValue<string>();
            if (type is { } value)
            {
                types.Add(value);
            }
        }

        return types;
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

        public static async Task<HaltHarness> StartAsync(bool autoCloseResponses = false)
        {
            var source = new FakeRealtimeServerTransport { AutoCloseResponses = autoCloseResponses };
            var english = new FakeRealtimeServerTransport { AutoCloseResponses = autoCloseResponses };
            var japanese = new FakeRealtimeServerTransport { AutoCloseResponses = autoCloseResponses };
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
                if (streamEvent.Event is RealtimeTranslationServerEvent.ServerError { Code: "transport" })
                {
                    count += 1;
                }
            }

            return count;
        }

        public async ValueTask DisposeAsync() => await Dual.ForceCloseAsync();
    }
}
