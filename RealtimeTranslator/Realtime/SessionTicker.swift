import Foundation

/// 接続中に定期実行する字幕 tick・イベント欠落確認・ヘルス診断のタスクを管理する。
/// 接続試行をまたいで生存し、tearDownStreaming / performStop で止める。
/// context はタスク側で weak 参照し、context→ticker の所有で循環参照にしない。
@MainActor
final class SessionTicker {
    private var tickerTask: Task<Void, Never>?

    func start(context: SessionRuntimeContext, intervalNanoseconds: UInt64) {
        stop()
        tickerTask = Task { @MainActor [weak self, weak context] in
            while !Task.isCancelled {
                try? await Task.sleep(nanoseconds: intervalNanoseconds)
                guard !Task.isCancelled else { return }
                // sleep 中は強参照を持たず、context/ticker が解放されていればここで抜ける。
                guard let self, let context else { return }
                if let feed = context.activeFeed,
                    context.subtitlePipeline.checkEventLoss(
                        feed,
                        generation: context.lifecycleGeneration
                    )
                {
                    continue
                }
                if let update = context.subtitlePipeline.tickProcessor(now: Date()) {
                    context.subtitlePipeline.enqueueRender(update)
                    if update.shouldFinalize || update.isInvalidation {
                        await context.subtitlePipeline.resetAudioRoutingForNextSegment()
                    }
                }
                for detection in context.healthBookkeeper.tick(feed: context.activeFeed) {
                    context.onHealthDetection?(detection)
                }
                let snapshot = context.subtitlePipeline.aggregator.tick()
                context.onSubtitleUpdate?(snapshot)
                if context.state == .idle || context.state == .error {
                    self.tickerTask = nil
                    return
                }
            }
        }
    }

    func stop() {
        tickerTask?.cancel()
        tickerTask = nil
    }
}
