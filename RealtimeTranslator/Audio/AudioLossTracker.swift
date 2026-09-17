import Foundation

struct AudioLossObservation: Equatable, Sendable {
    var droppedFrames: Int
    var lostMilliseconds: Int
    var queueWaitMilliseconds: Int
    var didLose: Bool { lostMilliseconds > 0 }
    var shouldReconnect: Bool
}

struct AudioLossMetrics: Equatable, Sendable {
    var droppedFrames = 0
    var lostMilliseconds = 0
    var lossEvents = 0
    var maxQueueWaitMilliseconds = 0
}

struct AudioLossPolicy: Sendable, Equatable {
    var frameDurationMilliseconds = 100
    var reconnectLostMillisecondsThreshold = 6_400
    var reconnectWindowMilliseconds = 30_000

    static let `default` = AudioLossPolicy()
}

struct AudioLossTracker: Sendable {
    private let policy: AudioLossPolicy
    private var generation: Int?
    private var lastSequence = 0
    private var lastDiscardedMilliseconds = 0
    private var lossEvents: [(atMilliseconds: Int, lostMilliseconds: Int)] = []
    private(set) var metrics = AudioLossMetrics()

    init(policy: AudioLossPolicy = .default) {
        self.policy = policy
    }

    mutating func observe(
        generation: Int,
        sequence: Int,
        discardedMilliseconds: Int,
        queueWaitMilliseconds: Int,
        atMilliseconds: Int
    ) -> AudioLossObservation {
        let isFirstFrame = self.generation != generation
        if isFirstFrame {
            self.generation = generation
            lastSequence = sequence
            lastDiscardedMilliseconds = 0
        }

        let droppedFrames =
            isFirstFrame
            ? max(0, sequence)
            : max(0, sequence - (lastSequence + 1))
        let discardedDelta = max(0, discardedMilliseconds - lastDiscardedMilliseconds)
        let lostMilliseconds = droppedFrames * policy.frameDurationMilliseconds + discardedDelta

        lastSequence = sequence
        lastDiscardedMilliseconds = max(0, discardedMilliseconds)
        metrics.droppedFrames += droppedFrames
        metrics.lostMilliseconds += lostMilliseconds
        metrics.maxQueueWaitMilliseconds = max(
            metrics.maxQueueWaitMilliseconds,
            max(0, queueWaitMilliseconds)
        )

        if lostMilliseconds > 0 {
            metrics.lossEvents += 1
            lossEvents.append((atMilliseconds, lostMilliseconds))
        }

        let oldestAllowed = atMilliseconds - policy.reconnectWindowMilliseconds
        lossEvents.removeAll { $0.atMilliseconds < oldestAllowed }
        let windowLoss = lossEvents.reduce(0) { $0 + $1.lostMilliseconds }
        let shouldReconnect = windowLoss >= policy.reconnectLostMillisecondsThreshold
        if shouldReconnect {
            lossEvents.removeAll(keepingCapacity: true)
        }

        return AudioLossObservation(
            droppedFrames: droppedFrames,
            lostMilliseconds: lostMilliseconds,
            queueWaitMilliseconds: max(0, queueWaitMilliseconds),
            shouldReconnect: shouldReconnect
        )
    }

    mutating func reset() {
        generation = nil
        lastSequence = 0
        lastDiscardedMilliseconds = 0
        lossEvents.removeAll(keepingCapacity: true)
        metrics = AudioLossMetrics()
    }
}
