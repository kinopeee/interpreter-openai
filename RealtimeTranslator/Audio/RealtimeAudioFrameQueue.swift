import Foundation
import os

final class RealtimeAudioFrameQueue: @unchecked Sendable {
    static let capacity = 32

    let frames: AsyncStream<CapturedAudioFrame>
    private let continuation: AsyncStream<CapturedAudioFrame>.Continuation
    private let state = OSAllocatedUnfairLock(
        initialState: State(generation: nil, nextSequence: 0, droppedFrameCount: 0)
    )

    private struct State {
        var generation: Int?
        var nextSequence: Int
        var droppedFrameCount: Int
    }

    init() {
        var continuation: AsyncStream<CapturedAudioFrame>.Continuation!
        frames = AsyncStream(bufferingPolicy: .bufferingNewest(Self.capacity)) {
            continuation = $0
        }
        self.continuation = continuation
    }

    var droppedFrameCount: Int {
        state.withLock { $0.droppedFrameCount }
    }

    func enqueue(
        pcm16: Data,
        generation: Int,
        discardedMilliseconds: Int,
        capturedAt: ContinuousClock.Instant
    ) -> Bool {
        state.withLock { state in
            if state.generation != generation {
                state.generation = generation
                state.nextSequence = 0
            }
            let sequence = state.nextSequence
            state.nextSequence += 1
            let frame = CapturedAudioFrame(
                generation: generation,
                sequence: sequence,
                pcm16: pcm16,
                discardedMilliseconds: discardedMilliseconds,
                capturedAt: capturedAt
            )
            switch continuation.yield(frame) {
            case .enqueued:
                return true
            case .dropped:
                state.droppedFrameCount += 1
                #if DEBUG
                let dropCount = state.droppedFrameCount
                AppLogger.audio.notice(
                    "DBG_CAPTURE_QUEUE_DROP count=\(dropCount, privacy: .public)"
                )
                #endif
                return true
            case .terminated:
                return false
            @unknown default:
                return false
            }
        }
    }

    func finish() {
        continuation.finish()
    }
}
