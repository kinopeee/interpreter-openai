import Foundation

/// 音声フレームの取り込み、音声欠落検知、translation への送信を担う。
@MainActor
final class SessionAudioFeeder {
    private let context: SessionRuntimeContext

    init(context: SessionRuntimeContext) {
        self.context = context
    }

    func run(generation: Int) async throws {
        for await frame in context.audioCapture.frames {
            guard context.isCurrentGeneration(generation) else { return }
            guard context.state == .listening else { return }
            context.healthBookkeeper.recordCapture(
                hasAudioActivity: PCM16AudioActivity.normalizedPeakAmplitude(of: frame.pcm16)
                    > context.healthBookkeeper.thresholds.audioActivityPeakFloor
            )
            let queueWaitMilliseconds = max(
                0,
                Int(
                    ReconnectBudget.nanoseconds(
                        frame.capturedAt.duration(to: .now)
                    ) / 1_000_000
                )
            )
            let atMilliseconds = max(
                0,
                Int(ReconnectBudget.nanoseconds(ReconnectBudget.continuousNow()) / 1_000_000)
            )
            let observation = context.subtitlePipeline.observeAudio(
                generation: frame.generation,
                sequence: frame.sequence,
                discardedMilliseconds: frame.discardedMilliseconds,
                queueWaitMilliseconds: queueWaitMilliseconds,
                atMilliseconds: atMilliseconds
            )
            if observation.didLose {
                await context.subtitlePipeline.handleAudioLoss()
                #if DEBUG
                AppLogger.session.notice(
                    "DBG_AUDIO_LOSS droppedFrames=\(observation.droppedFrames, privacy: .public) lostMs=\(observation.lostMilliseconds, privacy: .public) reconnect=\(observation.shouldReconnect, privacy: .public)"
                )
                #endif
            }
            if observation.shouldReconnect {
                throw RealtimeAudioCaptureError.pipelineOverloaded
            }
            context.healthBookkeeper.recordSendStart()
            try await context.dualClient.appendAudioFrame(frame.pcm16)
            context.healthBookkeeper.recordSendSuccess()
        }
        guard context.isCurrentGeneration(generation), context.state == .listening else {
            return
        }
        if let terminationError = context.audioCapture.terminationError {
            throw terminationError
        }
        throw RealtimeTranslationError.recoverableTransportFailure("audio stream ended")
    }
}
