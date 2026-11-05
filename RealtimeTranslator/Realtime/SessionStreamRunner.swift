import Foundation

/// 1回の接続試行を実行する。handshake → feed/epoch 確立 → audio 開始 →
/// 音声・イベント・完了監視の3タスクを race で監視する。
@MainActor
final class SessionStreamRunner {
    private let context: SessionRuntimeContext
    private let audioFeeder: SessionAudioFeeder
    private let eventConsumer: SessionEventConsumer
    private static let completionRecheckInterval: Duration = .milliseconds(100)

    init(context: SessionRuntimeContext) {
        self.context = context
        self.audioFeeder = SessionAudioFeeder(context: context)
        self.eventConsumer = SessionEventConsumer(context: context)
    }

    func connectAndStream(generation: Int) async throws {
        context.healthBookkeeper.beginAttempt(
            lifecycleGeneration: context.lifecycleGeneration,
            reservedEpoch: await context.dualClient.reservedEpoch
        )
        let apiKey = try requireAPIKey()
        context.state = .connecting
        context.subtitlePipeline.aggregator.setStatusBanner(UiCopy.text("banner.connecting"))
        context.publishSubtitles()

        let pair = context.sessionLanguagePair ?? context.languagePairProvider()
        do {
            try await context.dualClient.start(
                apiKey: apiKey,
                tuning: context.tuningProvider().forPair(pair),
                pair: pair
            )
        } catch {
            // start は network 処理の前に connectionEpoch を予約済み。失敗した handshake の
            // epoch で診断するため読み直す。
            context.healthBookkeeper.updateAttemptEpoch(await context.dualClient.reservedEpoch)
            throw error
        }
        context.healthBookkeeper.updateAttemptEpoch(await context.dualClient.reservedEpoch)
        guard context.isCurrentGeneration(generation) else {
            await context.dualClient.forceClose()
            return
        }

        let feed = await context.dualClient.feed
        context.activeFeed = feed
        let epoch = feed.runToken
        // 再接続時 beginNewEpoch は buffer を捨てる。idle finalize 前の完全ペアを
        // 先に確定しないと、オプトイン字幕記録へ .finalized が届かない。
        context.subtitlePipeline.flushPendingFinalizeIfNeeded()
        context.subtitlePipeline.beginEpoch(epoch, pair: pair)
        await context.dualClient.resetAudioRouting()

        try await context.audioCapture.start()
        guard context.isCurrentGeneration(generation) else {
            await context.audioCapture.stop()
            await context.dualClient.forceClose()
            return
        }

        context.state = .listening
        context.reconnectBudget.recordListening()
        context.healthBookkeeper.beginGeneration(
            generation: generation,
            epoch: epoch,
            deliveryState: feed.deliveryState
        )
        context.subtitlePipeline.aggregator.setStatusBanner(UiCopy.text("banner.listening"))
        context.ticker.start(
            context: context,
            intervalNanoseconds: context.activeTickerIntervalNanoseconds
        )
        context.publishSubtitles()

        let feedTask = Task { @MainActor in
            try await self.audioFeeder.run(generation: generation)
        }
        let eventTask = Task { @MainActor in
            try await self.eventConsumer.run(generation: generation, feed: feed)
        }
        let completionTask = Task<Void, Error> { @MainActor in
            await feed.deliveryState.waitForCompletion()
            if Task.isCancelled {
                throw CancellationError()
            }
            try context.subtitlePipeline.throwIfEventsLost(feed)
            if feed.deliveryState.termination != .none {
                context.afterFailedSourceTerminationForTests?()
            }
            // 欠落なしの終了理由は stream 上の error event が消費側へ届くので、そちらに任せる。
            // ただし tryRecordTermination の直後に error 投入が満杯で recordLoss すると
            // completed 済みのため waitForCompletion は再起床しない。欠落を再確認する。
            // 正常完了も SessionEventConsumer.run に任せる。success で戻ると session loop が終わる。
            while !Task.isCancelled {
                try context.subtitlePipeline.throwIfEventsLost(feed)
                try await Task.sleep(for: Self.completionRecheckInterval)
            }
            throw CancellationError()
        }
        let firstResult = await raceFirstResult([feedTask, eventTask, completionTask])
        feedTask.cancel()
        eventTask.cancel()
        completionTask.cancel()
        // SessionAudioFeeder.run の for-await は cancel だけでは解けないことがある。
        await context.audioCapture.stop()
        _ = await feedTask.result
        _ = await eventTask.result
        if feed.deliveryState.didLoseEvents {
            context.subtitlePipeline.handleEventLossIfNeeded(feed)
        }
        context.subtitlePipeline.discardFailedSourceIfNeeded(feed)
        try firstResult.get()
    }

    private func raceFirstResult(_ tasks: [Task<Void, Error>]) async -> Result<Void, Error> {
        // 先に完了した側の結果で戻る。負け側の非構造化Taskも必ずcancelしないと、
        // withTaskGroupが `.result` 待ちの子タスクで戻りを阻み、再接続不能になる。
        await withTaskGroup(of: Result<Void, Error>.self) { group in
            for task in tasks {
                group.addTask {
                    await withTaskCancellationHandler {
                        await task.result
                    } onCancel: {
                        task.cancel()
                    }
                }
            }
            let value = await group.next() ?? .failure(CancellationError())
            for task in tasks {
                task.cancel()
            }
            group.cancelAll()
            while await group.next() != nil {}
            return value
        }
    }

    private func requireAPIKey() throws -> String {
        guard let key = try context.apiKeyStore.load() else {
            throw RealtimeTranslationError.missingAPIKey
        }
        return try RealtimeTranslationError.requireNormalizedAPIKey(key)
    }
}
