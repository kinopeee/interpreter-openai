using System;
using System.Linq;
using System.Text.Json.Nodes;
using RealtimeTranslator.Core.Audio;
using RealtimeTranslator.Core.Realtime;
using Xunit;

namespace RealtimeTranslator.Core.Tests;

public sealed class AudioLossTrackerFixtureTests
{
    public static TheoryData<string> CaseNames
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var node in SharedFixtures.Load("audio")["loss"]!["cases"]!.AsArray())
            {
                data.Add(SharedFixtures.Text(node!["name"]));
            }

            return data;
        }
    }

    // Given: shared fixture の音声欠落ポリシー
    // When: Core の既定値と比較する
    // Then: フレーム長・再接続閾値・窓・キュー容量が一致する
    [Fact]
    public void PolicyAndConstantsMatchFixture()
    {
        var loss = SharedFixtures.Load("audio")["loss"]!.AsObject();
        var reconnect = loss["reconnect"]!.AsObject();
        var policy = AudioLossPolicy.Default;

        Assert.Equal(SharedFixtures.Number(loss["frameDurationMs"]), policy.FrameDurationMilliseconds);
        Assert.Equal(
            SharedFixtures.Number(reconnect["lostMsThreshold"]),
            policy.ReconnectLostMillisecondsThreshold);
        Assert.Equal(
            SharedFixtures.Number(reconnect["windowMs"]),
            policy.ReconnectWindowMilliseconds);
        Assert.Equal(
            SharedFixtures.Number(loss["sendQueueFrameCapacity"]),
            AudioLossPolicy.SendQueueFrameCapacity);
        Assert.Equal(
            SharedFixtures.Number(loss["taintedSegmentWindowMs"]),
            RealtimeSubtitleAssembler.AudioLossTaintWindow.TotalMilliseconds);
    }

    // Given: fixture のフレーム列と独立した期待値
    // When: AudioLossTracker へ順に再生する
    // Then: 累積 metrics と再接続観測が fixture と一致する
    [Theory]
    [MemberData(nameof(CaseNames))]
    public void ReplayMatchesFixture(string name)
    {
        var item = SharedFixtures.Load("audio")["loss"]!["cases"]!
            .AsArray()
            .OfType<JsonObject>()
            .Single(item => SharedFixtures.Text(item["name"]) == name);
        var tracker = new AudioLossTracker();
        var reconnectAt = SharedFixtures.OptionalNumber(item["expected"]!["reconnectAt"]);
        var observedReconnectAt = (int?)null;

        foreach (var node in item["frames"]!.AsArray())
        {
            var frame = node!.AsObject();
            var atMilliseconds = SharedFixtures.Number(frame["atMs"]);
            var observation = tracker.Observe(
                SharedFixtures.Number(frame["generation"]),
                frame["sequence"]!.GetValue<long>(),
                SharedFixtures.Number(frame["discardedMs"]),
                SharedFixtures.Number(frame["queueWaitMs"]),
                atMilliseconds);

            if (observation.ShouldReconnect)
            {
                observedReconnectAt = atMilliseconds;
            }
        }

        var expected = item["expected"]!.AsObject();
        Assert.Equal(SharedFixtures.Number(expected["droppedFrames"]), tracker.Metrics.DroppedFrames);
        Assert.Equal(SharedFixtures.Number(expected["lostMs"]), tracker.Metrics.LostMilliseconds);
        Assert.Equal(SharedFixtures.Number(expected["lossEvents"]), tracker.Metrics.LossEvents);
        Assert.Equal(
            SharedFixtures.Number(expected["maxQueueWaitMs"]),
            tracker.Metrics.MaxQueueWaitMilliseconds);
        Assert.Equal(reconnectAt, observedReconnectAt);
    }
}
