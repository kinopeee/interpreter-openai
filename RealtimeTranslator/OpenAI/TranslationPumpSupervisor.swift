import Foundation

/// 翻訳送信ポンプの task ハンドル・世代番号・transport halt・連続失敗数を管理する。
/// ポンプ本体のループはオーケストレーターが持ち、ここは帳簿だけを担う。
/// `DualRealtimeTranslationClient` の actor 隔離下だけで触る値型コンポーネント。
struct TranslationPumpSupervisor {
    private(set) var task: Task<Void, Never>?
    /// 現在登録中のポンプ世代。古いポンプの終了処理が新ポンプの参照を消さない。
    private(set) var generation = 0
    /// transport failure後、再接続まで翻訳ポンプを再開しない。
    private(set) var haltedForTransportFailure = false
    private(set) var consecutiveFailures = 0
    private let failureLimit: Int

    init(failureLimit: Int) {
        self.failureLimit = failureLimit
    }

    var isTracked: Bool {
        task != nil
    }

    /// 新しい世代としてポンプを登録する。task が既にある場合のガードは呼び出し側。
    mutating func start(body: @escaping @Sendable (Int) async -> Void) {
        generation += 1
        let current = generation
        task = Task {
            await body(current)
        }
    }

    /// 現在世代なら task を手放す。再開可否の判定は呼び出し側が行う。
    mutating func finishIfCurrent(generation expected: Int) -> Bool {
        guard generation == expected else { return false }
        task = nil
        return true
    }

    mutating func invalidate() {
        generation += 1
        task?.cancel()
        task = nil
    }

    mutating func haltForTransportFailure() {
        haltedForTransportFailure = true
    }

    @discardableResult
    mutating func recordFailure() -> Int {
        consecutiveFailures += 1
        return consecutiveFailures
    }

    var reachedFailureLimit: Bool {
        consecutiveFailures >= failureLimit
    }

    mutating func resetFailures() {
        consecutiveFailures = 0
    }

    mutating func reset() {
        haltedForTransportFailure = false
        consecutiveFailures = 0
    }
}
