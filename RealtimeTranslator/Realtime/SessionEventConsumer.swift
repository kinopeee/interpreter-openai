import Foundation

/// EventFeed のストリームイベントを消費し、字幕パイプライン・epoch・
/// 終了診断・routing へ振り分ける。
@MainActor
final class SessionEventConsumer {
    private let context: SessionRuntimeContext

    init(context: SessionRuntimeContext) {
        self.context = context
    }

    func run(generation: Int, feed: EventFeed) async throws {
        let stream = feed.events
        for await streamEvent in stream {
            // stop 後の未読 delta は Dual recentYields → close drain が assembler へ同期適用する。
            // ここで ingest/enqueueRender すると、performStop が消した renderTask が再生成され、
            // forceFinalize 後に replaceCurrent で確定ペアを live へ戻す。
            guard context.isCurrentGeneration(generation) else { return }
            try context.subtitlePipeline.throwIfEventsLost(feed)
            guard streamEvent.epoch == feed.runToken else {
                await context.dualClient.acknowledgeConsumedStreamEvent(runToken: feed.runToken)
                continue
            }

            // delta 文字列は monitor へ渡さない（検知は到着・進捗の事実だけを見る）。
            switch streamEvent.event {
            case .inputTranscriptDelta(let delta, _, _)
            where streamEvent.lane == .source
                && !delta.isEmpty:
                context.healthBookkeeper.recordSourceProgress()
            case .outputTranscriptDelta(let delta, _, _) where !delta.isEmpty:
                context.healthBookkeeper.recordTranslationProgress(lane: streamEvent.lane)
            default:
                break
            }

            if case .error(let message, let code, let errorType) = streamEvent.event {
                let classification = EventDeliveryState.classify(
                    errorType: errorType,
                    code: code,
                    message: message
                )
                if classification.disposition == .keepAlive {
                    await context.dualClient.acknowledgeConsumedStreamEvent(runToken: feed.runToken)
                    continue
                }
                feed.deliveryState.tryRecordTermination(classification)
                throw feed.deliveryState.makeError()
            }

            if case .inputTranscriptFailed(let itemID, let eventID, let code, let errorType) = streamEvent.event {
                let classification = EventDeliveryState.classifyTranscriptionFailure(
                    errorType: errorType,
                    code: code
                )
                let invalidation = context.subtitlePipeline.discardFailedSource(itemID: itemID, eventID: eventID)
                feed.deliveryState.noteSourceFailureConsumed()
                if let invalidation {
                    context.subtitlePipeline.discardPending()
                    // halt/recover の flushPendingFinalizeIfNeeded が discardPending するため、
                    // 間引きせず即時適用し、aggregator の未確定ペアを先に消す。
                    context.subtitlePipeline.renderNow(invalidation)
                    await context.subtitlePipeline.resetAudioRoutingForNextSegment()
                }
                if classification.disposition == .keepAlive {
                    await context.dualClient.acknowledgeConsumedStreamEvent(runToken: feed.runToken)
                    continue
                }
                feed.deliveryState.tryRecordTermination(classification)
                throw feed.deliveryState.makeError()
            }

            // 原文 routing は専用 transcription の source lane だけを使う。
            // 適用または明示破棄のあとで acknowledge する。ack を先にすると、
            // この await 中に performStop が走ったとき未適用イベントが stop drain から外れる。
            guard context.isCurrentGeneration(generation) else { return }
            if let result = context.subtitlePipeline.processSubtitleEvent(streamEvent, now: Date()) {
                #if DEBUG
                AppLogger.session.notice(
                    "DBG_ASSEMBLER_UPDATE epoch=\(streamEvent.epoch, privacy: .public) generation=\(result.ingestedUpdate.segmentGeneration, privacy: .public) sourceEmpty=\(result.ingestedUpdate.sourceText.isEmpty, privacy: .public) translationEmpty=\(result.ingestedUpdate.translatedText.isEmpty, privacy: .public)"
                )
                #endif
                for update in result.updates {
                    context.subtitlePipeline.enqueueRender(update)
                }
                switch result.routingAction {
                case .none:
                    if !result.isSourceUpdate && result.ingestedUpdate.shouldFinalize {
                        await context.subtitlePipeline.resetAudioRoutingForNextSegment()
                    }
                case .select(let target):
                    context.healthBookkeeper.setSelectedLane(target.map { .translation($0) })
                    try await context.dualClient.selectTranslationTarget(target)
                case .switch(let target):
                    context.healthBookkeeper.setSelectedLane(target.map { .translation($0) })
                    await context.dualClient.resetAudioRouting()
                    try await context.dualClient.selectTranslationTarget(target)
                }
            }
            await context.dualClient.acknowledgeConsumedStreamEvent(runToken: feed.runToken)
        }
        guard context.isCurrentGeneration(generation) else { return }
        try context.subtitlePipeline.throwIfEventsLost(feed)
        if feed.deliveryState.termination != .none {
            context.subtitlePipeline.discardFailedSourceIfNeeded(feed)
            throw feed.deliveryState.makeError()
        }
        throw RealtimeTranslationError.recoverableTransportFailure("event stream ended")
    }
}
