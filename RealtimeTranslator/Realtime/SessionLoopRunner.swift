import Foundation

/// 再接続ループ。connectAndStream の試行を繰り返し、回復可能エラーの
/// 分類・バックオフ・終了診断・teardown を担う。
@MainActor
final class SessionLoopRunner {
    private let context: SessionRuntimeContext
    private let streamRunner: SessionStreamRunner

    init(context: SessionRuntimeContext) {
        self.context = context
        self.streamRunner = SessionStreamRunner(context: context)
    }

    func run(generation: Int) async {
        while generation == context.lifecycleGeneration {
            var reconnectDetail: String?
            var recoverableError: Error?
            do {
                try await streamRunner.connectAndStream(generation: generation)
                return
            } catch is CancellationError {
                return
            } catch let error as RealtimeTranslationError where error.isRecoverable {
                // recoverable: 終了診断を記録して再接続へ進む
                recoverableError = error
                if case .recoverableTransportFailure(let detail) = error {
                    reconnectDetail = detail
                }
            } catch let error as URLError where Self.isTransientURLError(error) {
                // 一時的な URLSession 切断のみ再接続
                recoverableError = error
            } catch let error as URLSessionWebSocketTransportError {
                // transport 境界の未接続など: fall through to reconnect
                recoverableError = error
            } catch let error as NSError where Self.isTransientPOSError(error) {
                // URLError に bridge されない POSIX 切断
                recoverableError = error
            } catch let error as RealtimeTranslationError {
                guard generation == context.lifecycleGeneration else { return }
                await haltWithError(
                    kind: SessionTerminationKind(error),
                    message: error.localizedDescription
                )
                return
            } catch let error as RealtimeAudioCaptureError {
                switch error {
                case .inputDeviceChanged:
                    // マイク切断/切替は再接続。バナーに理由を残す。
                    reconnectDetail = error.localizedDescription
                    recoverableError = error
                case .pipelineOverloaded:
                    // フレーム経路の背圧は再接続で立て直す
                    recoverableError = error
                default:
                    guard generation == context.lifecycleGeneration else { return }
                    await haltWithError(kind: .other, message: error.localizedDescription)
                    return
                }
            } catch {
                // 未知のアプリエラーは再接続せず即 error（予測可能性を優先）。
                guard generation == context.lifecycleGeneration else { return }
                await haltWithError(kind: .other, message: error.localizedDescription)
                return
            }

            guard generation == context.lifecycleGeneration else { return }
            let decision = context.reconnectBudget.recordFailure()
            guard decision.kind == .wait else {
                let budgetExhausted = decision.kind == .budgetExhausted
                await haltWithError(
                    kind: budgetExhausted ? .reconnectBudgetExhausted : .reconnectAttemptLimit,
                    message: UiCopy.text(
                        budgetExhausted ? "error.reconnectBudgetExhausted" : "error.reconnectLimit"
                    )
                )
                return
            }

            context.state = .reconnecting
            let micMessage = RealtimeAudioCaptureError.inputDeviceChanged.errorDescription
            if let reconnectDetail, let micMessage, reconnectDetail == micMessage {
                context.subtitlePipeline.aggregator.setStatusBanner(
                    reconnectingBanner(detail: reconnectDetail)
                )
            } else {
                context.subtitlePipeline.aggregator.setStatusBanner(reconnectingBanner(detail: nil))
            }
            context.publishSubtitles()
            // recoverable 失敗も終了診断として記録する（診断のみ、挙動は変えない）。
            if let recoverableError {
                await context.healthBookkeeper.recordTermination(recoverableError)
            }
            await tearDownStreaming(keepSubtitles: true)

            // 停止（cancel）で待ちを即座に打ち切る。ループ先頭の世代確認で再接続を始めない。
            try? await Task.sleep(for: decision.delay)
        }
    }

    /// 再接続しない失敗の終了シーケンス。世代 guard と return は呼び出し側に残す。
    /// 全経路で 終了診断の記録 → teardown → 完全ペアの確定 → error 遷移 の順を共有する。
    private func haltWithError(kind: SessionTerminationKind, message: String) async {
        await context.healthBookkeeper.recordTermination(kind: kind)
        await tearDownStreaming()
        // epoch/buffer を捨てる前に完全ペアを確定し、オプトイン字幕記録へ渡す。
        context.subtitlePipeline.flushPendingFinalizeIfNeeded()
        context.enterErrorMessage(message)
    }

    private func tearDownStreaming(keepSubtitles: Bool = false) async {
        // recoverable・正常終了を問わず接続 teardown で健康世代を閉じる。
        context.healthBookkeeper.endGeneration()
        await context.audioCapture.stop()
        await context.dualClient.forceClose()
        if let feed = context.activeFeed {
            context.subtitlePipeline.discardFailedSourceIfNeeded(feed)
        }
        context.activeFeed = nil
        context.subtitlePipeline.resetHandledLossRunToken()
        context.subtitlePipeline.deactivateLanguagePair()
        context.subtitlePipeline.clearBoundaryCandidate()
        context.ticker.stop()
        if !keepSubtitles {
            context.subtitlePipeline.discardPending()
        }
    }

    /// 再接続対象の一時的な URLError のみ許可する（証明書/ATS/不正 URL は即 error）。
    /// handshake が HTTP ステータス付きで拒否された場合は transport が分類済みの error に置き換えるため、
    /// ここへ届く `badServerResponse` はステータス不明のものだけ。
    private static func isTransientURLError(_ error: URLError) -> Bool {
        switch error.code {
        case .timedOut,
            .cannotFindHost,
            .cannotConnectToHost,
            .networkConnectionLost,
            .dnsLookupFailed,
            .notConnectedToInternet,
            .cannotLoadFromNetwork,
            .internationalRoamingOff,
            .callIsActive,
            .dataNotAllowed:
            return true
        default:
            return false
        }
    }

    /// URLError に bridge されない POSIX 切断コード（Darwin 値）。
    private static let transientPOSIXCodes: Set<Int> = [
        32, // EPIPE
        50, // ENETDOWN
        51, // ENETUNREACH
        53, // ECONNABORTED
        54, // ECONNRESET
        57, // ENOTCONN
        60, // ETIMEDOUT
        65, // EHOSTUNREACH
    ]

    private static func isTransientPOSError(_ error: NSError) -> Bool {
        error.domain == NSPOSIXErrorDomain && transientPOSIXCodes.contains(error.code)
    }

    private func reconnectingBanner(detail: String?) -> String {
        let substitutions = [
            "detail": detail ?? "",
            "attempt": String(context.reconnectBudget.currentAttempt),
            "max": String(context.reconnectBudget.policy.maxAttempts),
        ]
        return UiCopy.text("banner.reconnectingProgress", substitutions)
            .trimmingCharacters(in: .whitespaces)
    }
}
