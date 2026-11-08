import Foundation

/// `DualRealtimeTranslationClient` の終了・ドレイン処理。
/// 正常停止（未送信翻訳 frame の drain → 全接続 close → stop drain 確定 → merge stream finish）、
/// forceClose、drain 待ち、close drain 予算の解決、stop drain 窓の窓口をここへ集約する。
/// 状態はクライアントが持ち、各メソッドは `isolated` 引数でクライアントの actor 上で実行する。
/// 分割前と同じ actor 上・同じ suspension point で動き、順序契約を変えない。
struct DualClientShutdownCoordinator: Sendable {
    private let sourceConnection: RealtimeSourceTranscriptionConnection
    private let connections: [RealtimeTranslationOutputLanguage: RealtimeTranslationConnection]
    private let tuning: DualRealtimeTranslationClientTuning
    private let translationDrainTimeoutNanoseconds: UInt64

    init(
        sourceConnection: RealtimeSourceTranscriptionConnection,
        connections: [RealtimeTranslationOutputLanguage: RealtimeTranslationConnection],
        tuning: DualRealtimeTranslationClientTuning,
        translationDrainTimeoutNanoseconds: UInt64
    ) {
        self.sourceConnection = sourceConnection
        self.connections = connections
        self.tuning = tuning
        self.translationDrainTimeoutNanoseconds = translationDrainTimeoutNanoseconds
    }

    func resolveCloseDrainTimeoutNanoseconds(on client: isolated DualRealtimeTranslationClient) -> UInt64 {
        var pending = client.frameQueues.pendingCount
        if client.pump.isTracked {
            pending += 1
        }
        return tuning.resolveTranslationDrainTimeoutNanoseconds(
            baseNanoseconds: translationDrainTimeoutNanoseconds,
            pendingFrameCount: pending
        )
    }

    /// 翻訳ポンプが現在の待ち行列を処理し終えるまで待つ。
    /// 送信が停滞しても `timeoutNanoseconds` で待機だけを打ち切り、送信ポンプ自体は停止しない。
    func waitForTranslationDrain(
        on client: isolated DualRealtimeTranslationClient,
        timeoutNanoseconds: UInt64
    ) async throws {
        let deadline = ContinuousClock.now + .nanoseconds(Int64(timeoutNanoseconds))
        let pollInterval = Duration.milliseconds(5)
        while true {
            if !client.pump.isTracked, client.frameQueues.pendingCount == 0 {
                return
            }

            let remaining = deadline - ContinuousClock.now
            guard remaining > .zero else {
                if !client.pump.isTracked, client.frameQueues.pendingCount == 0 {
                    return
                }
                throw RealtimeTranslationError.recoverableTransportFailure("translation pump drain timeout")
            }

            // TaskGroupはスコープ終了時にキャンセル済み子タスクの完了も待つ。
            // pump.value待ちはキャンセルで解けないため、状態を短周期で再確認する。
            try await Task.sleep(for: min(remaining, pollInterval))
        }
    }

    func beginStopDrainCapture(on client: isolated DualRealtimeTranslationClient) {
        client.mergedEvents.beginStopDrainCapture()
    }

    func acknowledgeConsumedStreamEvent(on client: isolated DualRealtimeTranslationClient, runToken: Int?) {
        client.mergedEvents.acknowledge(runToken: runToken, currentEpoch: client.connectionEpoch)
    }

    func closeGracefully(on client: isolated DualRealtimeTranslationClient) async -> [RealtimeTranslationStreamEvent] {
        guard client.isRunning else {
            return client.mergedEvents.takeStopDrainEvents()
        }

        // consumer 停止後〜ここまでのイベントも落とさない。未武装ならここで武装する。
        client.mergedEvents.beginStopDrainCapture()

        // 未送信の翻訳フレームを先に送り、停止時の訳文欠落を防ぐ。
        // preroll flush 直後は待ち行列が長いので pending 数に応じて予算を伸ばす。
        // drain 待ち中に届く最終 delta も stopDrainBuffer へ蓄える。
        try? await waitForTranslationDrain(
            on: client,
            timeoutNanoseconds: resolveCloseDrainTimeoutNanoseconds(on: client)
        )

        client.isRunning = false
        client.pump.invalidate()
        client.frameQueues.clearPending()
        // 原文 close が先に失敗しても翻訳 close を捨てない。Windows の WhenAll と同じく
        // 全 lane を待ち、未 await の close が次セッションのソケットを閉じるのを防ぐ。
        var closeFailed = false
        let closes = connections.values.map { connection in
            Task { try await connection.closeGracefully() }
        }
        do {
            try await sourceConnection.closeGracefully()
        } catch {
            closeFailed = true
            AppLogger.realtime.error(
                "Graceful close failed: \(AppLogger.redact(error.localizedDescription), privacy: .public)"
            )
        }
        for close in closes {
            do {
                try await close.value
            } catch {
                if !closeFailed {
                    closeFailed = true
                    AppLogger.realtime.error(
                        "Graceful close failed: \(AppLogger.redact(error.localizedDescription), privacy: .public)"
                    )
                }
            }
        }
        client.mergeTask?.cancel()
        client.mergeTask = nil
        // close 失敗でも drain を先に確定し、forceClose で消えないようにする。
        let drained = client.mergedEvents.takeStopDrainEvents()
        client.mergedEvents.finishStream()
        if closeFailed {
            await forceClose(on: client)
        }
        return drained
    }

    func forceClose(on client: isolated DualRealtimeTranslationClient) async {
        client.isRunning = false
        client.selectedTranslationTarget = nil
        client.startedTranslationTargets = []
        client.frameQueues.clearAll()
        client.pump.reset()
        // beginStopDrainCapture 済みの窓は残す。reconnect の tearDown / generation
        // mismatch の forceClose が、stop が close drain へ渡す未読 delta を消さない。
        client.mergedEvents.clearYieldWindow()
        client.connectionEpoch += 1
        client.pump.invalidate()
        client.mergeTask?.cancel()
        client.mergeTask = nil
        await sourceConnection.forceClose()
        for connection in connections.values {
            await connection.forceClose()
        }
        client.mergedEvents.finishStream()
    }
}
