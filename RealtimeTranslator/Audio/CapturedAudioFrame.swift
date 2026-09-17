import Foundation

struct CapturedAudioFrame: Sendable {
    let generation: Int
    let sequence: Int
    let pcm16: Data
    let discardedMilliseconds: Int
    let capturedAt: ContinuousClock.Instant
}
