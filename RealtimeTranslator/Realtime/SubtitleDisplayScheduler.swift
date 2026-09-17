import Foundation

/// 字幕更新の表示予約・間引き・停止後消去を調停するコンポーネント。
/// 「いつ画面へ出すか」の帳簿だけを持ち、実際の画面反映は delegate へ委譲する。
@MainActor
protocol SubtitleDisplaySchedulerDelegate: AnyObject {
    func subtitleDisplayScheduler(
        _ scheduler: SubtitleDisplayScheduler,
        requestsRenderOf update: RealtimeSubtitleUpdate
    )
}

@MainActor
final class SubtitleDisplayScheduler {
    /// 発話途中の原文表示を約160ms間隔へ間引く既定値（字幕UI不変条件）。
    static let defaultRenderIntervalNanoseconds: UInt64 = 160_000_000

    weak var delegate: SubtitleDisplaySchedulerDelegate?

    private let renderIntervalNanoseconds: UInt64
    private let nowProvider: @MainActor () -> Date
    private let sleeper: @MainActor (UInt64) async -> Void

    private var renderTask: Task<Void, Never>?
    private var pendingUpdate: RealtimeSubtitleUpdate?
    private var lastRenderedAt = Date.distantPast
    private var postStopClearTask: Task<Void, Never>?

    init(
        renderIntervalNanoseconds: UInt64 = SubtitleDisplayScheduler.defaultRenderIntervalNanoseconds,
        nowProvider: @escaping @MainActor () -> Date = { Date() },
        sleeper: @escaping @MainActor (UInt64) async -> Void = { nanoseconds in
            try? await Task.sleep(nanoseconds: nanoseconds)
        }
    ) {
        self.renderIntervalNanoseconds = renderIntervalNanoseconds
        self.nowProvider = nowProvider
        self.sleeper = sleeper
    }

    /// 通常の描画入口。確定更新は保留を破棄して即時描画し、
    /// それ以外は直近描画からの経過に応じて間引き・統合する。
    func enqueue(_ update: RealtimeSubtitleUpdate) {
        if update.shouldFinalize {
            discardPending()
            renderNow(update)
            return
        }

        pendingUpdate = update
        guard renderTask == nil else { return }

        // 遅延は TimeInterval で先に求め、UInt64 へは短い残り時間だけ変換する。
        // lastRenderedAt 初期値は distantPast のため、経過ナノ秒を先に UInt64 化すると溢れて trap する。
        // 時計が巻き戻った場合は経過 0 として扱い、遅延が描画間隔を超えないようにする。
        let elapsed = max(0, nowProvider().timeIntervalSince(lastRenderedAt))
        let intervalSeconds = Double(renderIntervalNanoseconds) / 1_000_000_000
        let delaySeconds = max(0, intervalSeconds - elapsed)
        let delayNanoseconds =
            delaySeconds > 0
            ? UInt64(delaySeconds * 1_000_000_000)
            : 0
        renderTask = Task { @MainActor [weak self] in
            if delayNanoseconds > 0 {
                await self?.sleeper(delayNanoseconds)
            }
            guard let self, !Task.isCancelled else { return }
            self.renderTask = nil
            guard let pending = self.pendingUpdate else { return }
            self.pendingUpdate = nil
            self.renderNow(pending)
        }
    }

    /// スロットルを挟まず即時描画する。無効化通知以外は描画時刻を記録する。
    func renderNow(_ update: RealtimeSubtitleUpdate) {
        if !update.isInvalidation {
            lastRenderedAt = nowProvider()
        }
        delegate?.subtitleDisplayScheduler(self, requestsRenderOf: update)
    }

    /// 予約中の描画タスクを止め、未描画の更新を取り出す。
    /// 停止処理が滞留分を取りこぼさず即時適用するために使う。
    @discardableResult
    func takePendingUpdate() -> RealtimeSubtitleUpdate? {
        renderTask?.cancel()
        renderTask = nil
        defer { pendingUpdate = nil }
        return pendingUpdate
    }

    /// 予約と保留中の更新を描画せず破棄する。
    /// 受信欠落・再接続・確定直前のフラッシュなど、古い画面状態を残したくない経路で使う。
    func discardPending() {
        renderTask?.cancel()
        renderTask = nil
        pendingUpdate = nil
    }

    /// 録音停止後に字幕を消す予約を立てる。
    /// 待機後 `shouldClear` が true のときだけ `onClear` を実行する。
    func schedulePostStopClear(
        afterNanoseconds delayNanoseconds: UInt64,
        shouldClear: @escaping @MainActor () -> Bool,
        onClear: @escaping @MainActor () -> Void
    ) {
        cancelPostStopClear()
        postStopClearTask = Task { @MainActor [weak self] in
            guard let self else { return }
            await self.sleeper(delayNanoseconds)
            guard !Task.isCancelled else { return }
            guard shouldClear() else { return }
            onClear()
            self.postStopClearTask = nil
        }
    }

    func cancelPostStopClear() {
        postStopClearTask?.cancel()
        postStopClearTask = nil
    }
}
