import Foundation

/// `events` 出力ストリーム、merge yielder、未消費イベント窓、stop drain を管理する。
/// `DualRealtimeTranslationClient` の actor 隔離下だけで触る値型コンポーネント。
struct MergedEventBuffer {
    private(set) var stream: AsyncStream<RealtimeTranslationStreamEvent>
    private var continuation: AsyncStream<RealtimeTranslationStreamEvent>.Continuation
    private var yielder: EventDeliveryYielder?
    private(set) var deliveryState: EventDeliveryState
    /// closeGracefully 中だけ詰め、停止時の最終 delta 欠落を防ぐ。
    private var stopDrainBuffer: [RealtimeTranslationStreamEvent]?
    /// `events` AsyncStream と同じ容量。finish() が未読を捨ててもここから close drain へ移せる。
    /// yield 済み字幕イベントの最新側。stream の bufferingNewest と同じ窓。
    private var recentYields: [(sequence: Int, event: RealtimeTranslationStreamEvent)] = []
    /// `forward` が yield した回数。recentYields と同じ窓で数える。
    private var nextSequence = 0
    /// session consumer が stream から読んで acknowledge した回数。
    private var ackedSequence = 0
    private let tuning: DualRealtimeTranslationClientTuning

    init(tuning: DualRealtimeTranslationClientTuning = .default) {
        self.tuning = tuning
        let pair = Self.makeStream(capacity: tuning.mergedEventBufferLimit)
        stream = pair.stream
        continuation = pair.continuation
        deliveryState = EventDeliveryState(epoch: 0)
    }

    /// 新しい接続世代向けに stream を張り直す。旧 continuation は finish する。
    mutating func recreate() {
        continuation.finish()
        clearYieldWindow()
        let pair = Self.makeStream(capacity: tuning.mergedEventBufferLimit)
        stream = pair.stream
        continuation = pair.continuation
    }

    /// 接続世代ごとの deliveryState と merge yielder を用意する。
    mutating func arm(epoch: Int) {
        deliveryState = EventDeliveryState(epoch: epoch)
        yielder = EventDeliveryYielder(
            continuation: continuation,
            deliveryState: deliveryState,
            stage: .merge,
            capacity: tuning.mergedEventBufferLimit
        )
    }

    mutating func forward(_ event: RealtimeTranslationStreamEvent) {
        // 接続側で落とすのが正攻法だが、stopDrainBuffer のメモリ肥大も防ぐ。
        if case .outputAudioDelta = event.event {
            return
        }
        if deliveryState.didLoseEvents {
            return
        }
        if stopDrainBuffer != nil {
            guard stopDrainBuffer!.count < tuning.stopDrainRetentionLimit else {
                deliveryState.recordLoss(
                    stage: .stopDrain,
                    capacity: tuning.stopDrainRetentionLimit
                )
                return
            }
            stopDrainBuffer?.append(event)
            return
        }
        nextSequence += 1
        recentYields.append((sequence: nextSequence, event: event))
        if recentYields.count > tuning.unacknowledgedRetentionLimit {
            deliveryState.recordLoss(
                stage: .merge,
                capacity: tuning.unacknowledgedRetentionLimit
            )
            yielder?.finish()
            return
        }
        _ = yielder?.deliver(event)
    }

    /// transport failure 等、接続側から直接購読者へ届ける終了イベント。
    /// stop drain 窓・ack 窓を経由しない。
    @discardableResult
    func deliver(_ event: RealtimeTranslationStreamEvent) -> Bool {
        yielder?.deliver(event) ?? false
    }

    /// 停止開始時に呼ぶ。未読の merge イベントとこれ以降の close 窓を stop drain へ蓄える。
    mutating func beginStopDrainCapture() {
        if stopDrainBuffer == nil {
            // consumer が generation bump で ingest を止めたあと、
            // AsyncStream.finish() は未読要素を捨てる。Windows Channel と違い再読できないので、
            // 未消費の最新窓だけを移す。既に ingest した nil event_id delta は再適用しない。
            stopDrainBuffer = recentYields
                .filter { $0.sequence > ackedSequence }
                .map { $0.event }
        }
    }

    /// session consumer が stream から読んで適用／破棄したあとに呼ぶ。
    /// stop drain は未消費の recentYields だけをコピーし、既読の nil event_id delta を再適用しない。
    mutating func acknowledge(runToken: Int?, currentEpoch: Int) {
        if let runToken, runToken != currentEpoch {
            return
        }
        guard ackedSequence < nextSequence else { return }
        ackedSequence += 1
        recentYields.removeAll { $0.sequence <= ackedSequence }
    }

    /// 蓄えた stop drain イベントを取り出して窓を閉じる。
    mutating func takeStopDrainEvents() -> [RealtimeTranslationStreamEvent] {
        defer { stopDrainBuffer = nil }
        return stopDrainBuffer ?? []
    }

    mutating func clearStopDrain() {
        stopDrainBuffer = nil
    }

    /// yield 済み窓だけを捨てる。stop drain 窓は reconnect tearDown でも残す。
    mutating func clearYieldWindow() {
        recentYields.removeAll(keepingCapacity: true)
        nextSequence = 0
        ackedSequence = 0
    }

    /// 購読側のイベント流が終わったら yielder を閉じ、deliveryState を正常完了にする。
    mutating func finishDelivery() {
        yielder?.finish()
        yielder = nil
        deliveryState.completeNormally()
    }

    /// 出力ストリーム自体を閉じる。未読要素は捨てるため、止める前に stop drain へ退避する。
    mutating func finishStream() {
        continuation.finish()
        finishDelivery()
    }

    /// deinit など、購読側の完了通知を介さず continuation だけを閉じる。
    mutating func finishContinuation() {
        continuation.finish()
    }

    private static func makeStream(capacity: Int) -> (
        stream: AsyncStream<RealtimeTranslationStreamEvent>,
        continuation: AsyncStream<RealtimeTranslationStreamEvent>.Continuation
    ) {
        var continuation: AsyncStream<RealtimeTranslationStreamEvent>.Continuation!
        let stream = AsyncStream(bufferingPolicy: .bufferingOldest(capacity)) {
            continuation = $0
        }
        return (stream, continuation)
    }
}
