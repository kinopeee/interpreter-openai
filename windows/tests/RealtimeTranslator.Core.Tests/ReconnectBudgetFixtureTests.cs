using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using RealtimeTranslator.Core.Localization;
using RealtimeTranslator.Core.Realtime;
using Xunit;

namespace RealtimeTranslator.Core.Tests;

/// <summary>
/// shared/fixtures/v1/reconnect.json を正本として、再接続予算が単調クロック上の
/// 失敗 / Listening の列だけで決まることを確認する。
/// </summary>
public sealed class ReconnectBudgetFixtureTests
{
    public static TheoryData<string> CaseNames => SharedFixtures.CaseNames("reconnect", "cases");

    // Given: fixture の policy
    // When: Windows の既定値と比較する
    // Then: backoff・回数・予算・安定期間・文言キーが一致する
    [Fact]
    public void PolicyMatchesFixture()
    {
        var fixture = SharedFixtures.Load("reconnect");
        var policy = fixture["policy"]!;
        var actual = ReconnectPolicy.Default;

        Assert.Equal(SharedFixtures.Number(policy["initialBackoffMs"]), actual.InitialBackoff.TotalMilliseconds);
        Assert.Equal(SharedFixtures.Number(policy["backoffMultiplier"]), actual.BackoffMultiplier);
        Assert.Equal(SharedFixtures.Number(policy["maxBackoffMs"]), actual.MaxBackoff.TotalMilliseconds);
        Assert.Equal(SharedFixtures.Number(policy["jitterMaxMs"]), actual.JitterMax.TotalMilliseconds);
        Assert.Equal(SharedFixtures.Number(policy["maxAttempts"]), actual.MaxAttempts);
        Assert.Equal(InterpretationSession.MaxReconnectAttempts, actual.MaxAttempts);
        Assert.Equal(SharedFixtures.Number(policy["totalBudgetMs"]), actual.TotalBudget.TotalMilliseconds);
        Assert.Equal(SharedFixtures.Number(policy["stablePeriodMs"]), actual.StablePeriod.TotalMilliseconds);
        Assert.Equal(
            SharedFixtures.Number(policy["attemptTimeoutMs"]),
            RealtimeTranslationConnection.DefaultHandshakeTimeout.TotalMilliseconds);

        Assert.Equal(
            "error.reconnectBudgetExhausted",
            SharedFixtures.Text(fixture["budgetExhausted"]!["errorMessageKey"]));
        Assert.Equal("error.reconnectLimit", SharedFixtures.Text(fixture["attemptLimit"]!["errorMessageKey"]));
        Assert.NotEqual(
            UserCopy.Current.Text("error.reconnectLimit"),
            UserCopy.Current.Text("error.reconnectBudgetExhausted"));
    }

    // Given: fixture の失敗 / Listening の時系列
    // When: jitter 0 の ReconnectBudget へ単調クロックで再生する
    // Then: 各失敗の判定・attempt・backoff が fixture と一致する
    [Theory]
    [MemberData(nameof(CaseNames))]
    public void ReplayMatchesFixture(string name)
    {
        var item = SharedFixtures.Case("reconnect", "cases", name);
        var clock = new MonotonicClock();
        var budget = new ReconnectBudget(ReconnectPolicy.Default, clock, _ => TimeSpan.Zero);
        budget.Reset();

        foreach (var node in item["events"]!.AsArray())
        {
            var evt = node!;
            clock.SetElapsed(TimeSpan.FromMilliseconds(SharedFixtures.Number(evt["atMs"])));
            switch (SharedFixtures.Text(evt["kind"]))
            {
                case "listening":
                    budget.RecordListening();
                    break;
                case "failure":
                    var expected = evt["expected"]!;
                    var decision = budget.RecordFailure();
                    var step = $"{name} @ {SharedFixtures.Number(evt["atMs"])}ms";
                    switch (SharedFixtures.Text(expected["decision"]))
                    {
                        case "wait":
                            Assert.True(decision.Kind == ReconnectDecisionKind.Wait, step);
                            Assert.True(SharedFixtures.Number(expected["attempt"]) == decision.Attempt, step);
                            Assert.True(
                                SharedFixtures.Number(expected["backoffMs"]) == decision.Backoff.TotalMilliseconds,
                                step);
                            Assert.Equal(TimeSpan.Zero, decision.Jitter);
                            break;
                        case "attemptLimit":
                            Assert.True(decision.Kind == ReconnectDecisionKind.AttemptLimit, step);
                            break;
                        case "budgetExhausted":
                            Assert.True(decision.Kind == ReconnectDecisionKind.BudgetExhausted, step);
                            break;
                        default:
                            Assert.Fail($"unknown decision in {step}");
                            break;
                    }

                    break;
                default:
                    Assert.Fail($"unknown event kind in {name}");
                    break;
            }
        }
    }

    // Given: 壁時計だけが大きく進む（NTP 補正やスリープ復帰）
    // When: 単調クロックは進めずに失敗を記録する
    // Then: 予算も安定期間も壁時計に影響されない
    [Fact]
    public void WallClockChangesDoNotAffectBudget()
    {
        var clock = new MonotonicClock();
        var budget = new ReconnectBudget(ReconnectPolicy.Default, clock, _ => TimeSpan.Zero);
        budget.Reset();

        Assert.Equal(ReconnectDecisionKind.Wait, budget.RecordFailure().Kind);
        budget.RecordListening();
        clock.AdvanceWallClockOnly(TimeSpan.FromHours(3));

        var decision = budget.RecordFailure();
        Assert.Equal(ReconnectDecisionKind.Wait, decision.Kind);
        Assert.Equal(2, decision.Attempt);

        clock.AdvanceWallClockOnly(TimeSpan.FromDays(1));
        for (var index = 0; index < 3; index += 1)
        {
            Assert.Equal(ReconnectDecisionKind.Wait, budget.RecordFailure().Kind);
        }

        Assert.Equal(ReconnectDecisionKind.AttemptLimit, budget.RecordFailure().Kind);
    }

    // Given: 既定の jitter
    // When: 多数回サンプルする
    // Then: 0 以上 jitterMax 以下に収まる
    [Fact]
    public void DefaultJitterStaysWithinPolicyRange()
    {
        var budget = new ReconnectBudget(ReconnectPolicy.Default, new MonotonicClock());
        for (var index = 0; index < 200; index += 1)
        {
            budget.Reset();
            var decision = budget.RecordFailure();
            Assert.InRange(decision.Jitter, TimeSpan.Zero, ReconnectPolicy.Default.JitterMax);
            Assert.Equal(decision.Backoff + decision.Jitter, decision.Delay);
        }
    }

    // Given: backoff の計算
    // When: 上限を超える attempt を渡す
    // Then: maxBackoff で頭打ちになる
    [Fact]
    public void BackoffIsCappedAtMaxBackoff()
    {
        var policy = ReconnectPolicy.Default;
        Assert.Equal(TimeSpan.FromMilliseconds(500), policy.BackoffFor(1));
        Assert.Equal(TimeSpan.FromSeconds(8), policy.BackoffFor(5));
        Assert.Equal(TimeSpan.FromSeconds(8), policy.BackoffFor(40));
    }
}
