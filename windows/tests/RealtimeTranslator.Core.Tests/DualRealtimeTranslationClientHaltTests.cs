using System;
using System.Text;
using System.Threading.Tasks;
using RealtimeTranslator.Core.Audio;
using RealtimeTranslator.Core.OpenAI;
using RealtimeTranslator.Core.Realtime;
using Xunit;

namespace RealtimeTranslator.Core.Tests;

/// <summary>
/// ja-en の日本語 lane halt と、セグメント境界の ResetAudioRouting が連続失敗カウンタを捨てること。
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
