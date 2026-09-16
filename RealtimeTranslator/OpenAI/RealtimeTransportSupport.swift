import Foundation

/// 2 接続 actor が共有する transport 受信と event stream の補助。
/// decoding は各接続の protocol に依存するため呼び出し側に残す。
enum RealtimeTransportSupport {
    /// URLSessionWebSocketTask.receive は Swift Task キャンセルを見ない。
    /// timeout / 親 Task キャンセルで transport を閉じ、TaskGroup の残り待ちを解く。
    static func receiveWithTimeout(
        from transport: any RealtimeWebSocketTransport,
        timeoutNanoseconds: UInt64
    ) async throws -> Data {
        try await withTaskCancellationHandler {
            try await withThrowingTaskGroup(of: Data.self) { group in
                group.addTask { try await transport.receive() }
                group.addTask {
                    try await Task.sleep(nanoseconds: timeoutNanoseconds)
                    await transport.close()
                    throw RealtimeTranslationError.sessionUpdateTimeout
                }
                do {
                    let result = try await group.next()!
                    group.cancelAll()
                    return result
                } catch {
                    group.cancelAll()
                    if Task.isCancelled {
                        throw CancellationError()
                    }
                    if error is CancellationError {
                        throw RealtimeTranslationError.sessionUpdateTimeout
                    }
                    throw error
                }
            }
        } onCancel: {
            Task { await transport.close() }
        }
    }

    static func makeEventStream(
        bufferingLimit: Int
    ) -> (
        stream: AsyncStream<RealtimeTranslationStreamEvent>,
        continuation: AsyncStream<RealtimeTranslationStreamEvent>.Continuation
    ) {
        var continuation: AsyncStream<RealtimeTranslationStreamEvent>.Continuation!
        let stream = AsyncStream(bufferingPolicy: .bufferingOldest(bufferingLimit)) {
            continuation = $0
        }
        return (stream, continuation)
    }

    static func finishEventStream(
        eventContinuation: inout AsyncStream<RealtimeTranslationStreamEvent>.Continuation?,
        deliveryYielder: inout EventDeliveryYielder?
    ) {
        eventContinuation?.finish()
        eventContinuation = nil
        deliveryYielder = nil
    }
}
