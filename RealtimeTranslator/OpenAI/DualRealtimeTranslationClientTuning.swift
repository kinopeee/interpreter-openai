import Foundation

/// `DualRealtimeTranslationClient` の配送・送信キューのチューニング正本。
/// 容量値は `shared/fixtures/v1/routing.json`（preroll / 連続失敗上限）、
/// `shared/fixtures/v1/receive-queue.json`（merge / unacknowledged / stopDrain）、
/// `shared/fixtures/v1/translation-queue.json`（pending frame 上限）と対応する。
struct DualRealtimeTranslationClientTuning: Sendable {
    var mergedEventBufferLimit = 512
    var unacknowledgedRetentionLimit = 513
    var stopDrainRetentionLimit = 1024
    /// 100 ms frame × 40 = 直近4秒。言語判定遅延でも発話冒頭を翻訳へ届ける。
    var translationPrerollFrameLimit = 40
    var translationPendingFrameLimit = 80
    var consecutiveTranslationFailureLimit = 3
    /// 停止時 drain で未送信 frame 1 枚あたりに足す予算。preroll flush 後の短い停滞で訳文を落とさない。
    var translationDrainTimeoutNanosecondsPerPendingFrame: UInt64 = 250_000_000
    /// 停止時 drain の上限。Send 停滞でも Stop が無期限待ちしない。
    var translationDrainTimeoutCapNanoseconds: UInt64 = 30_000_000_000
    var defaultTranslationDrainTimeoutNanoseconds: UInt64 = 5_000_000_000

    static let `default` = DualRealtimeTranslationClientTuning()

    /// 停止時 drain 予算。base（既定5秒）に未送信 frame 分を足し、cap（30秒）で打ち切る。
    func resolveTranslationDrainTimeoutNanoseconds(
        baseNanoseconds: UInt64,
        pendingFrameCount: Int
    ) -> UInt64 {
        let pending = UInt64(max(0, pendingFrameCount))
        let scaled =
            baseNanoseconds
            &+ (pending &* translationDrainTimeoutNanosecondsPerPendingFrame)
        let cap = max(baseNanoseconds, translationDrainTimeoutCapNanoseconds)
        return min(max(scaled, baseNanoseconds), cap)
    }
}
