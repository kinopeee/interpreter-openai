import Foundation

/// 再接続の待ち時間と打ち切り条件。shared/fixtures/v1/reconnect.json の policy と同じ値を持つ。
struct ReconnectPolicy: Sendable, Equatable {
    var initialBackoff: Duration
    var backoffMultiplier: Int
    var maxBackoff: Duration
    var jitterMax: Duration
    var maxAttempts: Int
    var totalBudget: Duration
    var stablePeriod: Duration

    static let `default` = ReconnectPolicy(
        initialBackoff: .milliseconds(500),
        backoffMultiplier: 2,
        maxBackoff: .seconds(8),
        jitterMax: .milliseconds(250),
        maxAttempts: 5,
        totalBudget: .seconds(120),
        stablePeriod: .seconds(30)
    )

    /// attempt 回目（1 始まり）の指数バックオフ。maxBackoff で頭打ち。
    func backoff(forAttempt attempt: Int) -> Duration {
        precondition(attempt >= 1, "attempt must be 1-based")
        var value = initialBackoff
        for _ in 1..<attempt {
            value *= backoffMultiplier
            if value >= maxBackoff {
                return maxBackoff
            }
        }
        return min(value, maxBackoff)
    }
}

enum ReconnectDecisionKind: Sendable, Equatable {
    case wait
    case attemptLimit
    case budgetExhausted
}

struct ReconnectDecision: Sendable, Equatable {
    let kind: ReconnectDecisionKind
    let attempt: Int
    let backoff: Duration
    let jitter: Duration

    var delay: Duration { backoff + jitter }
}

/// 連続障害の経過を単調クロックで追い、試行回数と総予算の両方で再接続を打ち切る。
///
/// - Listening に入っただけでは試行回数をリセットしない。
/// - Listening が `stablePeriod` 以上続いた後の失敗で、試行回数と予算を新しく始める。
/// - 総予算は試行回数と独立に判定し、先に尽きた方で停止する。
struct ReconnectBudget: Sendable {
    typealias Now = @Sendable () -> Duration
    typealias Jitter = @Sendable (Duration) -> Duration

    /// 単調クロック（ContinuousClock）。壁時計の補正やスリープ復帰に影響されない。
    static let continuousNow: Now = {
        let origin = ContinuousClock.now
        return { origin.duration(to: .now) }
    }()

    static let randomJitter: Jitter = { max in
        let maxNanoseconds = Self.nanoseconds(max)
        guard maxNanoseconds > 0 else { return .zero }
        return .nanoseconds(Int64.random(in: 0...maxNanoseconds))
    }

    let policy: ReconnectPolicy
    private let now: Now
    private let jitter: Jitter

    private var attempt = 0
    private var outageStart: Duration?
    private var listeningSince: Duration?

    init(
        policy: ReconnectPolicy = .default,
        now: @escaping Now = ReconnectBudget.continuousNow,
        jitter: @escaping Jitter = ReconnectBudget.randomJitter
    ) {
        self.policy = policy
        self.now = now
        self.jitter = jitter
    }

    /// 現在の試行回数（バナー表示用）。
    var currentAttempt: Int { attempt }

    /// 新しい録音世代の開始で呼ぶ。過去の障害履歴を捨てる。
    mutating func reset() {
        attempt = 0
        outageStart = nil
        listeningSince = nil
    }

    /// Listening に入った時刻を記録する。これだけでは試行回数をリセットしない。
    mutating func recordListening() {
        listeningSince = now()
    }

    /// 失敗を記録し、待つか打ち切るかを返す。
    mutating func recordFailure() -> ReconnectDecision {
        let current = now()

        if let listeningSince {
            self.listeningSince = nil
            if current - listeningSince >= policy.stablePeriod {
                attempt = 0
                outageStart = nil
            }
        }

        let start = outageStart ?? current
        outageStart = start
        if current - start >= policy.totalBudget {
            return ReconnectDecision(kind: .budgetExhausted, attempt: attempt, backoff: .zero, jitter: .zero)
        }

        if attempt >= policy.maxAttempts {
            return ReconnectDecision(kind: .attemptLimit, attempt: attempt, backoff: .zero, jitter: .zero)
        }

        attempt += 1
        let backoff = policy.backoff(forAttempt: attempt)
        var jitterValue = jitter(policy.jitterMax)
        if jitterValue < .zero || jitterValue > policy.jitterMax {
            jitterValue = .zero
        }
        return ReconnectDecision(kind: .wait, attempt: attempt, backoff: backoff, jitter: jitterValue)
    }

    static func nanoseconds(_ duration: Duration) -> Int64 {
        let components = duration.components
        return components.seconds * 1_000_000_000 + components.attoseconds / 1_000_000_000
    }
}
