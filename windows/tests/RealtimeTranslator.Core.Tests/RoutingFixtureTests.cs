using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using RealtimeTranslator.Core.Audio;
using RealtimeTranslator.Core.OpenAI;
using RealtimeTranslator.Core.Realtime;
using Xunit;

namespace RealtimeTranslator.Core.Tests;

/// <summary>`shared/fixtures/v1/routing.json` の routing 契約を DualRealtimeTranslationClient で検証する。</summary>
public sealed class RoutingFixtureTests
{
    public static TheoryData<string> Cases => SharedFixtures.CaseNames("routing", "cases");

    // Given: fixture の routing シナリオ（言語切替 / preroll / 送信失敗を含む）
    // When: DualRealtimeTranslationClient に手順どおり適用する
    // Then: 原文・英語・日本語それぞれの lane へ届く frame 列が期待どおりになる
    [Theory]
    [MemberData(nameof(Cases))]
    public async Task RoutingCasesMatchFixture(string name)
    {
        var fixtureCase = SharedFixtures.Case("routing", "cases", name);
        await using var harness = await RoutingHarness.StartAsync();

        foreach (var step in fixtureCase["steps"]!.AsArray())
        {
            await harness.ApplyAsync(step!.AsObject());
        }

        var expected = fixtureCase["expected"]!.AsObject();
        Assert.Equal(FrameNames(expected["sourceFrames"]), harness.Source.AppendedFrameTexts());
        Assert.Equal(FrameNames(expected["englishFrames"]), harness.English.AppendedFrameTexts());
        Assert.Equal(FrameNames(expected["japaneseFrames"]), harness.Japanese.AppendedFrameTexts());

        var expectedTransportErrors = SharedFixtures.OptionalNumber(expected["transportErrorCount"]) ?? 0;
        Assert.Equal(expectedTransportErrors, harness.DrainTransportErrorCount());

        if (expected["translationPumpHalted"] is { } halted && SharedFixtures.Flag(halted))
        {
            // halt 後は enqueue 自体が止まるので、追加 frame を送っても翻訳 lane は増えない。
            var englishBefore = harness.English.AppendedFrameTexts();
            var japaneseBefore = harness.Japanese.AppendedFrameTexts();
            await harness.AppendFrameAsync("probeAfterHalt");
            Assert.Equal(englishBefore, harness.English.AppendedFrameTexts());
            Assert.Equal(japaneseBefore, harness.Japanese.AppendedFrameTexts());
        }

        if (expected["signalsSessionReconnect"] is { } reconnect)
        {
            // 再接続の合図は transport error の emit そのもの。
            Assert.Equal(SharedFixtures.Flag(reconnect), expectedTransportErrors > 0);
        }
    }

    // Given: preroll 上限を超える連続 frame
    // When: 上限超過後に発話言語を確定させる
    // Then: 直近 40 frame だけが新しい target へ flush される
    [Fact]
    public async Task RollingPrerollKeepsOnlyTheMostRecentFrames()
    {
        var window = SharedFixtures.Load("routing")["prerollWindow"]!.AsObject();
        var appendCount = SharedFixtures.Number(window["appendFrameCount"]);
        var expectedCount = SharedFixtures.Number(window["expectedFlushedFrameCount"]);
        var firstIndex = SharedFixtures.Number(window["expectedFirstFlushedFrameIndex"]);
        var lastIndex = SharedFixtures.Number(window["expectedLastFlushedFrameIndex"]);

        await using var harness = await RoutingHarness.StartAsync();
        for (var index = 0; index < appendCount; index += 1)
        {
            await harness.AppendFrameAsync("frame-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        await harness.SetSpokenLanguageAsync(SharedFixtures.Text(window["thenSetSpokenLanguage"]));

        var flushed = harness.English.AppendedFrameTexts();
        Assert.Equal(expectedCount, flushed.Count);
        Assert.Equal("frame-" + firstIndex.ToString(System.Globalization.CultureInfo.InvariantCulture), flushed[0]);
        Assert.Equal(
            "frame-" + lastIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
            flushed[^1]);
        Assert.Equal(DualRealtimeTranslationClient.TranslationPrerollFrameLimit, expectedCount);
    }

    // Given: 呼び出し側が同じバッファを再利用する
    // When: Append 後にバッファを上書きしてから言語を確定する
    // Then: preroll flush は上書き前の内容を翻訳 lane へ届ける
    [Fact]
    public async Task PrerollRetainsOwnedCopiesWhenCallerReusesBuffer()
    {
        await using var harness = await RoutingHarness.StartAsync();
        var buffer = Encoding.UTF8.GetBytes("frame-original");
        await harness.Dual.AppendAudioFrameAsync(buffer);
        Encoding.UTF8.GetBytes("frame-mutated!").CopyTo(buffer.AsSpan());

        await harness.SetSpokenLanguageAsync("japanese");

        var flushed = harness.English.AppendedFrameTexts();
        Assert.Single(flushed);
        Assert.Equal("frame-original", flushed[0]);
    }

    // Given: fixture の preroll / 連続失敗上限
    // When: 実装定数と突き合わせる
    // Then: 契約値と一致する
    [Fact]
    public void ConstantsMatchFixture()
    {
        var routing = SharedFixtures.Load("routing");

        Assert.Equal(
            SharedFixtures.Number(routing["prerollFrameLimit"]),
            DualRealtimeTranslationClient.TranslationPrerollFrameLimit);
        Assert.Equal(
            SharedFixtures.Number(routing["consecutiveTranslationFailureLimit"]),
            DualRealtimeTranslationClient.ConsecutiveTranslationFailureLimit);
    }

    private static List<string> FrameNames(JsonNode? node)
    {
        var names = new List<string>();
        foreach (var item in node!.AsArray())
        {
            names.Add(SharedFixtures.Text(item));
        }

        return names;
    }

    private sealed class RoutingHarness : IAsyncDisposable
    {
        private RoutingHarness(
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

        private RealtimeTranslationOutputLanguage? SelectedTarget { get; set; }

        public static async Task<RoutingHarness> StartAsync()
        {
            var source = new FakeRealtimeServerTransport();
            var english = new FakeRealtimeServerTransport();
            var japanese = new FakeRealtimeServerTransport();
            var dual = new DualRealtimeTranslationClient(
                new RealtimeSourceTranscriptionConnection(source, "test-safety"),
                new RealtimeTranslationConnection(RealtimeTranslationOutputLanguage.English, english, "test-safety"),
                new RealtimeTranslationConnection(RealtimeTranslationOutputLanguage.Japanese, japanese, "test-safety"));

            await dual.StartAsync("sk-test", RealtimeSessionTuning.Default);
            return new RoutingHarness(source, english, japanese, dual);
        }

        public async Task ApplyAsync(JsonObject step)
        {
            switch (SharedFixtures.Text(step["kind"]))
            {
                case "appendFrame":
                    await AppendFrameAsync(SharedFixtures.Text(step["frame"]));
                    break;

                case "setSpokenLanguage":
                    await SetSpokenLanguageAsync(SharedFixtures.Text(step["language"]));
                    break;

                case "resetAudioRouting":
                    await Dual.ResetAudioRoutingAsync();
                    break;

                case "translationSendFailure":
                    // 次に選択中 target へ送る 1 frame だけを失敗させる。
                    TargetTransport().FailNextSend();
                    break;

                default:
                    throw new InvalidOperationException("unknown routing step");
            }
        }

        public async Task AppendFrameAsync(string frameName)
        {
            await Dual.AppendAudioFrameAsync(Encoding.UTF8.GetBytes(frameName));
            await Dual.WaitForTranslationDrainAsync();
        }

        public async Task SetSpokenLanguageAsync(string language)
        {
            var spoken = language switch
            {
                "japanese" => SpokenLanguage.Japanese,
                "english" => SpokenLanguage.English,
                "unknown" => SpokenLanguage.Unknown,
                _ => throw new InvalidOperationException("unknown spoken language " + language),
            };

            SelectedTarget = spoken switch
            {
                SpokenLanguage.Japanese => RealtimeTranslationOutputLanguage.English,
                SpokenLanguage.English => RealtimeTranslationOutputLanguage.Japanese,
                _ => SelectedTarget,
            };

            await Dual.SetSpokenLanguageAsync(spoken);
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

        private FakeRealtimeServerTransport TargetTransport() =>
            SelectedTarget == RealtimeTranslationOutputLanguage.Japanese ? Japanese : English;
    }
}
