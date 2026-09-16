using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using RealtimeTranslator.Core.Audio;
using RealtimeTranslator.Core.OpenAI;
using RealtimeTranslator.Core.Realtime;
using Xunit;

namespace RealtimeTranslator.Core.Tests;

/// <summary>
/// shared/fixtures/v1/health.json を正本として、SessionHealthMonitor の
/// 位相・検知系列を単調時計上で検証する。
/// </summary>
public sealed class SessionHealthMonitorTests
{
    // Given: health.json の thresholds セクション
    // When: SessionHealthThresholds の既定値を見る
    // Then: fixture の thresholds と一致する
    [Fact]
    public void DefaultThresholdsMatchFixture()
    {
        var thresholds = SharedFixtures.Load("health")["thresholds"]!.AsObject();

        var defaults = new SessionHealthThresholds();
        Assert.Equal(Ms(thresholds, "captureStallMs"), defaults.CaptureStall);
        Assert.Equal(Ms(thresholds, "sendStallMs"), defaults.SendStall);
        Assert.Equal(Ms(thresholds, "receiveStallMs"), defaults.ReceiveStall);
        Assert.Equal(Ms(thresholds, "sourceStallMs"), defaults.SourceStall);
        Assert.Equal(Ms(thresholds, "translationStallMs"), defaults.TranslationStall);
        Assert.Equal(Ms(thresholds, "silenceMs"), defaults.Silence);
        Assert.Equal(Ms(thresholds, "connectGraceMs"), defaults.ConnectGrace);
        Assert.Equal(Ms(thresholds, "expiryNearMs"), defaults.ExpiryNear);
        Assert.Equal(
            thresholds["audioActivityPeakFloor"]!.GetValue<double>(),
            defaults.AudioActivityPeakFloor,
            9);
    }

    // Given: health.json の各 scenario（単調時刻 ms の op 列）
    // When: 時刻順に op を SessionHealthMonitor へ適用する
    // Then: 各 evaluate の phase と新規検知（kind 定義順）が期待と一致する
    [Theory]
    [MemberData(nameof(ScenarioNames))]
    public void ScenarioMatchesFixture(string name)
    {
        var scenario = SharedFixtures.Case("health", "scenarios", name);
        var monitor = new SessionHealthMonitor();

        // steps は op ごとにまとまって並ぶため、単調時刻で安定ソートする。
        var steps = scenario["steps"]!.AsArray()
            .Select((step, index) => (step: step!.AsObject(), index))
            .OrderBy(entry => At(entry.step))
            .ThenBy(entry => entry.index)
            .ToList();

        foreach (var (step, _) in steps)
        {
            var at = TimeSpan.FromMilliseconds(At(step));
            switch (SharedFixtures.Text(step["op"]))
            {
                case "begin":
                    monitor.BeginGeneration(
                        At(step, "generation"),
                        At(step, "epoch"),
                        step["isRecovery"]?.GetValue<bool>() ?? false,
                        at);
                    break;
                case "capture":
                    monitor.RecordCapture(at, step["active"]?.GetValue<bool>() ?? false);
                    break;
                case "send":
                    monitor.RecordSendSuccess(at);
                    break;
                case "receive":
                    monitor.RecordReceive(Lane(step["lane"]), at);
                    break;
                case "source":
                    monitor.RecordSourceProgress(at);
                    break;
                case "translation":
                    monitor.RecordTranslationProgress(Lane(step["lane"]), at);
                    break;
                case "select":
                    monitor.SetSelectedLane(
                        step["lane"] is null ? null : Lane(step["lane"]),
                        at);
                    break;
                case "expiry":
                    monitor.RecordSessionExpiry(
                        Lane(step["lane"]),
                        step["remainingMs"] is { } remaining
                            ? TimeSpan.FromMilliseconds(remaining.GetValue<int>())
                            : null,
                        at);
                    break;
                case "evaluate":
                {
                    var (snapshot, detections) = monitor.Evaluate(at);
                    var expect = step["expect"]!.AsObject();
                    Assert.Equal(
                        SharedFixtures.Text(expect["phase"]),
                        PhaseName(snapshot.Phase));
                    var expected = expect["detections"]!.AsArray()
                        .Select(entry => entry is JsonValue
                            ? (Kind: entry!.GetValue<string>(), Lane: (string?)null)
                            : (
                                Kind: SharedFixtures.Text(entry!["kind"]),
                                Lane: (string?)entry!["lane"]?.GetValue<string>()))
                        .ToList();
                    Assert.Equal(
                        expected.Select(e => e.Kind).ToList(),
                        detections.Select(d => DetectionName(d.Kind)).ToList());
                    // lane が fixture 側で指定されている検知だけ lane を照合する。
                    foreach (var (detection, expectedLane) in detections.Zip(expected))
                    {
                        if (expectedLane.Lane is not null)
                        {
                            Assert.Equal(expectedLane.Lane, detection.Lane?.ToLogString());
                        }
                    }

                    break;
                }
                default:
                    Assert.Fail($"unknown op in {name}: {step}");
                    break;
            }
        }
    }

    // Given: 無音の PCM16 frame
    // When: ピーク振幅を計算する
    // Then: 0 を返す
    [Fact]
    public void Pcm16PeakOfSilenceIsZero()
    {
        Assert.Equal(0, Pcm16AudioActivity.NormalizedPeakAmplitude(new byte[4800]));
    }

    // Given: 1 sample だけ 32767 を含む frame
    // When: ピーク振幅を計算する
    // Then: 1.0 を返す
    [Fact]
    public void Pcm16PeakOfMaxSampleIsOne()
    {
        var frame = new byte[4800];
        frame[100] = 0xFF;
        frame[101] = 0x7F;
        Assert.Equal(1.0, Pcm16AudioActivity.NormalizedPeakAmplitude(frame), 9);
    }

    // Given: Int16 最小値（-32768）だけを含む frame
    // When: ピーク振幅を計算する
    // Then: 飽和して 1.0 を返す
    [Fact]
    public void Pcm16PeakOfInt16MinClampsToOne()
    {
        var frame = new byte[4800];
        frame[0] = 0x00;
        frame[1] = 0x80;
        Assert.Equal(1.0, Pcm16AudioActivity.NormalizedPeakAmplitude(frame), 9);
    }

    // Given: 活動閾値 0.005 前後の振幅を持つ frame
    // When: ピーク振幅を計算する
    // Then: 閾値超過のみが活動とみなされる
    [Fact]
    public void Pcm16PeakAroundActivityFloor()
    {
        var above = new byte[4800];
        above[0] = 0xA4; // 164/32767 ≈ 0.005005
        Assert.True(Pcm16AudioActivity.NormalizedPeakAmplitude(above) > 0.005);

        var below = new byte[4800];
        below[0] = 0x0A; // 10/32767 ≈ 0.0003
        Assert.True(Pcm16AudioActivity.NormalizedPeakAmplitude(below) <= 0.005);
    }

    public static TheoryData<string> ScenarioNames => SharedFixtures.CaseNames("health", "scenarios");

    private static int At(JsonObject step, string field = "at") => step[field]!.GetValue<int>();

    private static TimeSpan Ms(JsonObject node, string field) =>
        TimeSpan.FromMilliseconds(node[field]!.GetValue<int>());

    private static RealtimeTranslationLane Lane(JsonNode? node) =>
        SharedFixtures.Text(node) switch
        {
            "source" => RealtimeTranslationLane.Source,
            "en" => RealtimeTranslationLane.Translation(RealtimeTranslationOutputLanguage.English),
            "ja" => RealtimeTranslationLane.Translation(RealtimeTranslationOutputLanguage.Japanese),
            "es" => RealtimeTranslationLane.Translation(RealtimeTranslationOutputLanguage.Spanish),
            var other => throw new InvalidOperationException($"unknown lane {other}"),
        };

    private static string PhaseName(SessionHealthPhase phase) => phase switch
    {
        SessionHealthPhase.Idle => "idle",
        SessionHealthPhase.CatchingUp => "catchingUp",
        SessionHealthPhase.Silence => "silence",
        SessionHealthPhase.AwaitingLanguageDetection => "awaitingLanguageDetection",
        SessionHealthPhase.Active => "active",
        _ => throw new ArgumentOutOfRangeException(nameof(phase)),
    };

    private static string DetectionName(SessionHealthDetectionKind kind) => kind switch
    {
        SessionHealthDetectionKind.CaptureStalled => "captureStalled",
        SessionHealthDetectionKind.SendStalled => "sendStalled",
        SessionHealthDetectionKind.ReceiveStalled => "receiveStalled",
        SessionHealthDetectionKind.SourceStalled => "sourceStalled",
        SessionHealthDetectionKind.TranslationStalled => "translationStalled",
        SessionHealthDetectionKind.ExpiryNear => "expiryNear",
        SessionHealthDetectionKind.Expired => "expired",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
