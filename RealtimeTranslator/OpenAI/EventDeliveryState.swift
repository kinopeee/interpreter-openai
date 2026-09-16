import Foundation
import os

enum EventDeliveryStage: Sendable, Equatable {
    case source
    case translation(RealtimeTranslationOutputLanguage)
    case merge
    case stopDrain
}

enum EventDeliveryTermination: Comparable, Sendable {
    case none
    case transportFailure
    case recoverableServerError
    case receiveOverflow
    case fatalServerError(String)
    case authenticationFailed

    private var rank: Int {
        switch self {
        case .none:
            return 0
        case .transportFailure:
            return 1
        case .recoverableServerError:
            return 2
        case .receiveOverflow:
            return 3
        case .fatalServerError:
            return 4
        case .authenticationFailed:
            return 5
        }
    }

    static func < (lhs: EventDeliveryTermination, rhs: EventDeliveryTermination) -> Bool {
        lhs.rank < rhs.rank
    }
}

final class EventDeliveryState: @unchecked Sendable {
    private struct State {
        var didLoseEvents = false
        var lossStage: EventDeliveryStage?
        var lossCapacity: Int?
        var termination: EventDeliveryTermination = .none
        var completed = false
        var waiters: [CheckedContinuation<Void, Never>] = []
        var receiveCounts: [RealtimeTranslationLane: Int] = [:]
        var sessionExpiries: [RealtimeTranslationLane: Int] = [:]
    }

    let epoch: Int
    private let state = OSAllocatedUnfairLock(initialState: State())

    init(epoch: Int) {
        self.epoch = epoch
    }

    var didLoseEvents: Bool {
        state.withLock { $0.didLoseEvents }
    }

    var lossStage: EventDeliveryStage? {
        state.withLock { $0.lossStage }
    }

    var lossCapacity: Int? {
        state.withLock { $0.lossCapacity }
    }

    var termination: EventDeliveryTermination {
        state.withLock { $0.termination }
    }

    /// decode した全メッセージ（handshake 受信・keepAlive error・unknown を含む）で +1。
    func recordReceive(lane: RealtimeTranslationLane) {
        state.withLock { $0.receiveCounts[lane, default: 0] += 1 }
    }

    func receiveCount(_ lane: RealtimeTranslationLane) -> Int {
        state.withLock { $0.receiveCounts[lane] ?? 0 }
    }

    /// `session.created` handshake 時に呼ぶ。不明なら nil を記録する。
    func recordSessionExpiry(lane: RealtimeTranslationLane, expiresAtUnixSeconds: Int?) {
        state.withLock { state in
            if let expiresAtUnixSeconds {
                state.sessionExpiries[lane] = expiresAtUnixSeconds
            } else {
                state.sessionExpiries.removeValue(forKey: lane)
            }
        }
    }

    func sessionExpiry(_ lane: RealtimeTranslationLane) -> Int? {
        state.withLock { $0.sessionExpiries[lane] }
    }

    func recordLoss(stage: EventDeliveryStage, capacity: Int) {
        let waiters = state.withLock { state -> [CheckedContinuation<Void, Never>] in
            if !state.didLoseEvents {
                state.didLoseEvents = true
                state.lossStage = stage
                state.lossCapacity = capacity
            }
            if .receiveOverflow > state.termination {
                state.termination = .receiveOverflow
            }
            return completeLocked(&state)
        }
        waiters.forEach { $0.resume() }
    }

    @discardableResult
    func tryRecordTermination(_ termination: EventDeliveryTermination) -> Bool {
        let updated = state.withLock { state -> (Bool, [CheckedContinuation<Void, Never>]) in
            guard termination > state.termination else { return (false, []) }
            state.termination = termination
            return (true, completeLocked(&state))
        }
        updated.1.forEach { $0.resume() }
        return updated.0
    }

    func completeNormally() {
        let waiters = state.withLock { state -> [CheckedContinuation<Void, Never>] in
            completeLocked(&state)
        }
        waiters.forEach { $0.resume() }
    }

    func waitForCompletion() async {
        await withTaskCancellationHandler {
            await withCheckedContinuation { continuation in
                let resumeImmediately = state.withLock { state -> Bool in
                    // cancel は waiter を起こすだけ。completed にすると overflow を
                    // 後から recordLoss しても completionTask が起きず再接続できない。
                    if state.completed || Task.isCancelled {
                        return true
                    }
                    state.waiters.append(continuation)
                    return false
                }
                if resumeImmediately {
                    continuation.resume()
                }
            }
        } onCancel: {
            let waiters = state.withLock { state -> [CheckedContinuation<Void, Never>] in
                let waiters = state.waiters
                state.waiters.removeAll(keepingCapacity: false)
                return waiters
            }
            waiters.forEach { $0.resume() }
        }
    }

    /// keepAlive は何も記録しない。それ以外は優先順位に従って termination を更新する。
    @discardableResult
    func tryRecordTermination(_ classification: RealtimeServerErrorClassification) -> Bool {
        guard classification.disposition != .keepAlive else { return false }
        return tryRecordTermination(classification.termination)
    }

    static func classify(errorType: String?, code: String?, message: String) -> RealtimeServerErrorClassification {
        RealtimeServerErrorClassification.classify(errorType: errorType, code: code, message: message)
    }

    func makeError() -> RealtimeTranslationError {
        switch termination {
        case .none:
            return .recoverableTransportFailure("event delivery ended")
        case .transportFailure:
            return .recoverableTransportFailure("event delivery transport failure")
        case .recoverableServerError:
            return .recoverableServerError
        case .receiveOverflow:
            return .receiveOverflow
        case .fatalServerError(let message):
            return .fatalServerError(message)
        case .authenticationFailed:
            return .authenticationFailed
        }
    }

    private func completeLocked(
        _ state: inout State
    ) -> [CheckedContinuation<Void, Never>] {
        guard !state.completed else { return [] }
        state.completed = true
        let waiters = state.waiters
        state.waiters.removeAll(keepingCapacity: false)
        return waiters
    }
}

final class EventDeliveryYielder: @unchecked Sendable {
    private let continuation: AsyncStream<RealtimeTranslationStreamEvent>.Continuation
    let deliveryState: EventDeliveryState
    private let stage: EventDeliveryStage
    private let capacity: Int
    private let lock = OSAllocatedUnfairLock(initialState: false)

    init(
        continuation: AsyncStream<RealtimeTranslationStreamEvent>.Continuation,
        deliveryState: EventDeliveryState,
        stage: EventDeliveryStage,
        capacity: Int
    ) {
        self.continuation = continuation
        self.deliveryState = deliveryState
        self.stage = stage
        self.capacity = capacity
    }

    func deliver(_ event: RealtimeTranslationStreamEvent) -> Bool {
        let shouldYield = lock.withLock { completed in
            guard !completed, !deliveryState.didLoseEvents else { return false }
            return true
        }
        guard shouldYield else { return false }

        switch continuation.yield(event) {
        case .enqueued(remaining: _):
            return true
        case .dropped:
            lock.withLock { $0 = true }
            deliveryState.recordLoss(stage: stage, capacity: capacity)
            continuation.finish()
            return false
        case .terminated:
            lock.withLock { $0 = true }
            return false
        @unknown default:
            lock.withLock { $0 = true }
            return false
        }
    }

    func finish() {
        let shouldFinish = lock.withLock { completed in
            guard !completed else { return false }
            completed = true
            return true
        }
        if shouldFinish {
            continuation.finish()
        }
    }
}

struct EventFeed: Sendable {
    let events: AsyncStream<RealtimeTranslationStreamEvent>
    let runToken: Int
    let deliveryState: EventDeliveryState
}
