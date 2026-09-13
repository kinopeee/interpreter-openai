using System;

namespace RealtimeTranslator.Core.Realtime;

/// <summary>再接続の backoff・回数・総予算・安定期間。<c>shared/fixtures/v1/reconnect.json</c> が正本。</summary>
public sealed record ReconnectPolicy(
    TimeSpan InitialBackoff,
    int BackoffMultiplier,
    TimeSpan MaxBackoff,
    TimeSpan JitterMax,
    int MaxAttempts,
    TimeSpan TotalBudget,
    TimeSpan StablePeriod)
{
    public static ReconnectPolicy Default { get; } = new(
        InitialBackoff: TimeSpan.FromMilliseconds(500),
        BackoffMultiplier: 2,
        MaxBackoff: TimeSpan.FromSeconds(8),
        JitterMax: TimeSpan.FromMilliseconds(250),
        MaxAttempts: 5,
        TotalBudget: TimeSpan.FromSeconds(120),
        StablePeriod: TimeSpan.FromSeconds(30));

    /// <summary>jitter を含まない attempt 回目（1 始まり）の backoff。</summary>
    public TimeSpan BackoffFor(int attempt)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);
        var factor = Math.Pow(BackoffMultiplier, attempt - 1);
        var scaled = InitialBackoff.Ticks * factor;
        if (scaled >= MaxBackoff.Ticks)
        {
            return MaxBackoff;
        }

        return TimeSpan.FromTicks((long)scaled);
    }
}

public enum ReconnectDecisionKind
{
    /// <summary>backoff だけ待ってから再接続する。</summary>
    Wait,
    /// <summary>attempt が上限を超えた。<c>error.reconnectLimit</c>。</summary>
    AttemptLimit,
    /// <summary>連続障害の開始から総予算を使い切った。<c>error.reconnectBudgetExhausted</c>。</summary>
    BudgetExhausted,
}

public readonly record struct ReconnectDecision(
    ReconnectDecisionKind Kind,
    int Attempt,
    TimeSpan Backoff,
    TimeSpan Jitter)
{
    public TimeSpan Delay => Backoff + Jitter;
}

/// <summary>
/// 連続障害の経過を単調クロック（<see cref="TimeProvider.GetTimestamp"/>）で追う。
/// Listening に入っただけではリセットせず、<see cref="ReconnectPolicy.StablePeriod"/> 以上維持したあとの失敗だけが
/// attempt と予算を作り直す。壁時計の変化は影響しない。スレッド安全ではないので呼び出し側で直列化する。
/// </summary>
public sealed class ReconnectBudget
{
    private readonly ReconnectPolicy _policy;
    private readonly TimeProvider _timeProvider;
    private readonly Func<TimeSpan, TimeSpan> _jitter;

    private int _attempt;
    private long? _outageStartTimestamp;
    private long? _listeningSinceTimestamp;

    public ReconnectBudget(
        ReconnectPolicy? policy = null,
        TimeProvider? timeProvider = null,
        Func<TimeSpan, TimeSpan>? jitter = null)
    {
        _policy = policy ?? ReconnectPolicy.Default;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _jitter = jitter ?? DefaultJitter;
    }

    public ReconnectPolicy Policy => _policy;

    /// <summary>現在の連続障害内で消費した attempt。</summary>
    public int Attempt => _attempt;

    /// <summary>録音開始時。すべて忘れる。</summary>
    public void Reset()
    {
        _attempt = 0;
        _outageStartTimestamp = null;
        _listeningSinceTimestamp = null;
    }

    /// <summary>Listening へ入った。安定期間の起点だけを記録し、attempt / 予算は触らない。</summary>
    public void RecordListening()
    {
        _listeningSinceTimestamp = _timeProvider.GetTimestamp();
    }

    /// <summary>接続が失われた。次に待つ時間、または停止理由を返す。</summary>
    public ReconnectDecision RecordFailure()
    {
        var now = _timeProvider.GetTimestamp();

        if (_listeningSinceTimestamp is { } listeningSince)
        {
            var listened = _timeProvider.GetElapsedTime(listeningSince, now);
            _listeningSinceTimestamp = null;
            if (listened >= _policy.StablePeriod)
            {
                _attempt = 0;
                _outageStartTimestamp = null;
            }
        }

        _outageStartTimestamp ??= now;
        var elapsed = _timeProvider.GetElapsedTime(_outageStartTimestamp.Value, now);
        if (elapsed >= _policy.TotalBudget)
        {
            return new ReconnectDecision(ReconnectDecisionKind.BudgetExhausted, _attempt, TimeSpan.Zero, TimeSpan.Zero);
        }

        if (_attempt >= _policy.MaxAttempts)
        {
            return new ReconnectDecision(ReconnectDecisionKind.AttemptLimit, _attempt, TimeSpan.Zero, TimeSpan.Zero);
        }

        _attempt += 1;
        var backoff = _policy.BackoffFor(_attempt);
        var jitter = _jitter(_policy.JitterMax);
        if (jitter < TimeSpan.Zero || jitter > _policy.JitterMax)
        {
            jitter = TimeSpan.Zero;
        }

        return new ReconnectDecision(ReconnectDecisionKind.Wait, _attempt, backoff, jitter);
    }

    private static TimeSpan DefaultJitter(TimeSpan max) =>
        max <= TimeSpan.Zero
            ? TimeSpan.Zero
            : TimeSpan.FromTicks(Random.Shared.NextInt64(0, max.Ticks + 1));
}
