import Foundation

/// 字幕パイプラインの帳簿。`RealtimeSubtitleProcessor`（struct）、
/// `AudioLossTracker`（struct）、loss runToken を所有し、
/// aggregator / displayScheduler への描画を束ねる。@MainActor 閉じ込め。
@MainActor
final class SessionSubtitlePipeline {
    let aggregator: SubtitleAggregator
    let displayScheduler: SubtitleDisplayScheduler
    private var processor = RealtimeSubtitleProcessor()
    private var audioLossTracker = AudioLossTracker()
    private var handledLossRunToken: Int?

    private let postStopSubtitleRetentionNanoseconds: UInt64
    private let healthBookkeeper: SessionHealthBookkeeper
    private let dualClient: any DualRealtimeTranslationClienting

    /// セッション側の状態を読む provider。init 後に bind で結ぶ（self 捕捉のため）。
    private var activeFeedProvider: @MainActor () -> EventFeed? = { nil }
    private var lifecycleGenerationProvider: @MainActor () -> Int = { 0 }
    private var stateProvider: @MainActor () -> TranslationState = { .idle }
    private var publishUpdate: @MainActor (SubtitleSnapshot) -> Void = { _ in }

    init(
        aggregator: SubtitleAggregator,
        displayScheduler: SubtitleDisplayScheduler,
        postStopSubtitleRetentionNanoseconds: UInt64,
        healthBookkeeper: SessionHealthBookkeeper,
        dualClient: any DualRealtimeTranslationClienting
    ) {
        self.aggregator = aggregator
        self.displayScheduler = displayScheduler
        self.postStopSubtitleRetentionNanoseconds = postStopSubtitleRetentionNanoseconds
        self.healthBookkeeper = healthBookkeeper
        self.dualClient = dualClient
        self.displayScheduler.delegate = self
    }

    func bind(
        activeFeed: @escaping @MainActor () -> EventFeed?,
        lifecycleGeneration: @escaping @MainActor () -> Int,
        state: @escaping @MainActor () -> TranslationState,
        publishUpdate: @escaping @MainActor (SubtitleSnapshot) -> Void
    ) {
        activeFeedProvider = activeFeed
        lifecycleGenerationProvider = lifecycleGeneration
        stateProvider = state
        self.publishUpdate = publishUpdate
    }

    var audioLossMetrics: AudioLossMetrics {
        audioLossTracker.metrics
    }

    var activeLanguagePair: LanguagePair? {
        processor.activeLanguagePair
    }

    var isCurrentSegmentTainted: Bool {
        processor.isCurrentSegmentTainted
    }

    var hasSelectedTranslationTarget: Bool {
        processor.hasSelectedTranslationTarget
    }

    func beginEpoch(_ epoch: Int, pair: LanguagePair) {
        processor.beginEpoch(epoch, pair: pair)
    }

    func deactivateLanguagePair() {
        processor.deactivateLanguagePair()
    }

    func clearBoundaryCandidate() {
        processor.clearBoundaryCandidate()
    }

    func resetHandledLossRunToken() {
        handledLossRunToken = nil
    }

    func resetAudioLoss() {
        audioLossTracker.reset()
    }

    func observeAudio(
        generation: Int,
        sequence: Int,
        discardedMilliseconds: Int,
        queueWaitMilliseconds: Int,
        atMilliseconds: Int
    ) -> AudioLossObservation {
        audioLossTracker.observe(
            generation: generation,
            sequence: sequence,
            discardedMilliseconds: discardedMilliseconds,
            queueWaitMilliseconds: queueWaitMilliseconds,
            atMilliseconds: atMilliseconds
        )
    }

    func discardFailedSource(itemID: String?, eventID: String?) -> RealtimeSubtitleUpdate? {
        processor.discardFailedSource(itemID: itemID, eventID: eventID)
    }

    func discardUnconfirmed() -> RealtimeSubtitleUpdate {
        processor.discardUnconfirmed()
    }

    func tickProcessor(now: Date) -> RealtimeSubtitleUpdate? {
        processor.tick(now: now)
    }

    func takePendingUpdate() -> RealtimeSubtitleUpdate? {
        displayScheduler.takePendingUpdate()
    }

    func renderNow(_ update: RealtimeSubtitleUpdate) {
        displayScheduler.renderNow(update)
    }

    func discardPending() {
        displayScheduler.discardPending()
    }

    /// 正常停止の close drain で届いた字幕イベントを assembler へ取り込む。
    func ingestStopDrainEvents(_ events: [RealtimeTranslationStreamEvent], feed: EventFeed) {
        guard !feed.deliveryState.didLoseEvents else { return }
        for streamEvent in events {
            if case .error = streamEvent.event {
                continue
            }
            if case .inputTranscriptFailed(let itemID, let eventID, _, _) = streamEvent.event {
                guard streamEvent.epoch == feed.runToken else { continue }
                let invalidation = processor.discardFailedSource(itemID: itemID, eventID: eventID)
                feed.deliveryState.noteSourceFailureConsumed()
                if let invalidation {
                    displayScheduler.renderNow(invalidation)
                }
                continue
            }
            guard let result = processSubtitleEvent(streamEvent, now: Date(), isReplay: true) else {
                continue
            }
            for update in result.updates {
                displayScheduler.renderNow(update)
            }
        }
    }

    func schedulePostStopSubtitleClearIfNeeded() {
        cancelPostStopClear()
        guard !aggregator.snapshot().current.isEmpty else { return }
        let generation = lifecycleGenerationProvider()
        displayScheduler.schedulePostStopClear(
            afterNanoseconds: postStopSubtitleRetentionNanoseconds
        ) { [weak self] in
            guard let self else { return false }
            return self.lifecycleGenerationProvider() == generation && self.stateProvider() == .idle
        } onClear: { [weak self] in
            guard let self else { return }
            self.aggregator.reset()
            self.publishUpdate(self.aggregator.snapshot())
        }
    }

    func cancelPostStopClear() {
        displayScheduler.cancelPostStopClear()
    }

    /// 完全な原文+訳文ペアが assembler / aggregator に残っていれば idle 待ちを飛ばして確定する。
    /// 停止・再接続・致命エラーで epoch/buffer を捨てる直前に呼び、字幕記録の欠落を防ぐ。
    func flushPendingFinalizeIfNeeded() {
        if let feed = activeFeedProvider(),
            checkEventLoss(feed, generation: lifecycleGenerationProvider())
        {
            return
        }
        if let feed = activeFeedProvider(), feed.deliveryState.hasPendingSourceFailure {
            discardFailedSourceIfNeeded(feed)
            return
        }
        // スロットル中の live snapshot より assembler を正とする。
        displayScheduler.discardPending()

        if processor.isCurrentSegmentTainted {
            let invalidation = processor.discardUnconfirmed()
            displayScheduler.renderNow(invalidation)
            return
        }

        let flushAt = Date().addingTimeInterval(RealtimeSubtitleAssembler.idleFinalizeInterval)
        if let update = processor.tick(now: flushAt) {
            displayScheduler.renderNow(update)
            return
        }

        // assembler が空でも、live 経路 (canFinalize: false) の完全ペアが
        // aggregator に残っている場合がある — 字幕記録のため確定する。
        let before = aggregator.snapshot().current
        guard before.state != .finalized else { return }
        let snapshot = aggregator.forceFinalize()
        guard snapshot.current.state == .finalized else { return }
        publishUpdate(snapshot)
    }

    func discardFailedSourceIfNeeded(_ feed: EventFeed) {
        guard feed.deliveryState.hasPendingSourceFailure else { return }
        if let invalidation = processor.discardFailedSource(itemID: nil, eventID: nil) {
            displayScheduler.discardPending()
            displayScheduler.renderNow(invalidation)
        }
        while feed.deliveryState.hasPendingSourceFailure {
            feed.deliveryState.noteSourceFailureConsumed()
        }
    }

    func enqueueRender(_ update: RealtimeSubtitleUpdate) {
        if let feed = activeFeedProvider(),
            checkEventLoss(feed, generation: lifecycleGenerationProvider())
        {
            return
        }
        if update.isInvalidation {
            displayScheduler.discardPending()
            displayScheduler.renderNow(update)
            return
        }
        displayScheduler.enqueue(update)
    }

    func processSubtitleEvent(
        _ streamEvent: RealtimeTranslationStreamEvent,
        now: Date,
        isReplay: Bool = false
    ) -> RealtimeSubtitleProcessingResult? {
        if let feed = activeFeedProvider(),
            checkEventLoss(feed, generation: lifecycleGenerationProvider())
        {
            return nil
        }
        return processor.process(streamEvent, now: now, isReplay: isReplay)
    }

    func resetAudioRoutingForNextSegment() async {
        processor.resetRoutingForNextSegment()
        healthBookkeeper.setSelectedLane(nil)
        await dualClient.resetAudioRouting()
    }

    func handleAudioLoss() async {
        let invalidation = processor.markAudioLoss(now: Date())
        displayScheduler.discardPending()
        displayScheduler.renderNow(invalidation)
        if !processor.hasSelectedTranslationTarget {
            await dualClient.resetAudioRouting()
        }
    }

    /// scheduler からの描画要求を aggregator・delegate へ反映する。
    private func apply(_ update: RealtimeSubtitleUpdate) {
        if update.isInvalidation {
            let snapshot = aggregator.invalidateCurrent()
            publishUpdate(snapshot)
            return
        }

        if stateProvider() == .listening || stateProvider() == .reconnecting {
            aggregator.setStatusBanner(nil)
        }

        if update.shouldFinalize {
            let snapshot = aggregator.finalizePair(
                sourceText: update.sourceText,
                translatedText: update.translatedText,
                clearCurrent: true
            )
            publishUpdate(snapshot)
            return
        }

        let snapshot = aggregator.replaceCurrent(
            sourceText: update.sourceText,
            translatedText: update.translatedText,
            isTranslationCurrent: update.isTranslationCurrent,
            canFinalize: false
        )
        publishUpdate(snapshot)
    }

    func checkEventLoss(_ feed: EventFeed, generation _: Int) -> Bool {
        guard feed.deliveryState.didLoseEvents else { return false }
        if handledLossRunToken != feed.runToken {
            handledLossRunToken = feed.runToken
            let invalidation = processor.discardUnconfirmed()
            displayScheduler.discardPending()
            displayScheduler.renderNow(invalidation)
        }
        return true
    }

    func handleEventLoss(_ feed: EventFeed) {
        _ = checkEventLoss(feed, generation: lifecycleGenerationProvider())
    }
}

extension SessionSubtitlePipeline: SubtitleDisplaySchedulerDelegate {
    func subtitleDisplayScheduler(
        _ scheduler: SubtitleDisplayScheduler,
        requestsRenderOf update: RealtimeSubtitleUpdate
    ) {
        apply(update)
    }
}
