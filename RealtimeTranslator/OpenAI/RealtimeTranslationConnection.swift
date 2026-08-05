import Foundation
import os

struct RealtimeTranslationStreamEvent: Sendable, Equatable {
    let target: RealtimeTranslationOutputLanguage
    let event: RealtimeTranslationServerEvent
    let epoch: Int
}

actor RealtimeTranslationConnection {
    static let endpointURL = URL(
        string: "wss://api.openai.com/v1/realtime/translations?model=gpt-realtime-translate"
    )!

    private let target: RealtimeTranslationOutputLanguage
    private let transport: any RealtimeWebSocketTransport
    private let safetyIdentifier: String
    private let sessionUpdateTimeoutNanoseconds: UInt64
    private let closeTimeoutNanoseconds: UInt64

    private var epoch = 0
    private var isReady = false
    private var isClosing = false
    private var didReceiveClosed = false
    private var outputAudioEventCount = 0
    private var receiveTask: Task<Void, Never>?
    private var eventContinuation: AsyncStream<RealtimeTranslationStreamEvent>.Continuation?
    private(set) var events: AsyncStream<RealtimeTranslationStreamEvent>

    init(
        target: RealtimeTranslationOutputLanguage,
        transport: any RealtimeWebSocketTransport = URLSessionWebSocketTransport(),
        safetyIdentifier: String = OpenAISafetyIdentifier.hashedValue(),
        sessionUpdateTimeoutNanoseconds: UInt64 = 15_000_000_000,
        closeTimeoutNanoseconds: UInt64 = 15_000_000_000
    ) {
        self.target = target
        self.transport = transport
        self.safetyIdentifier = safetyIdentifier
        self.sessionUpdateTimeoutNanoseconds = sessionUpdateTimeoutNanoseconds
        self.closeTimeoutNanoseconds = closeTimeoutNanoseconds
        (events, eventContinuation) = Self.makeEventStream()
    }

    func start(
        apiKey: String,
        config: RealtimeTranslationSessionConfig
    ) async throws {
        guard !apiKey.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw RealtimeTranslationError.missingAPIKey
        }

        await tearDownTransport()
        recreateEventStream()
        epoch += 1
        let currentEpoch = epoch
        isReady = false
        isClosing = false
        didReceiveClosed = false
        outputAudioEventCount = 0

        do {
            try await transport.connect(
                url: Self.endpointURL,
                headers: [
                    "Authorization": "Bearer \(apiKey)",
                    "OpenAI-Safety-Identifier": safetyIdentifier,
                ]
            )

            // handshakeは共有streamを消費せず、transportから直接読む。
            let created = try await receiveDirectEvent(
                timeoutNanoseconds: sessionUpdateTimeoutNanoseconds
            )
            if case .error(let message, let code) = created {
                throw classifyServerError(message: message, code: code)
            }
            guard case .sessionCreated = created else {
                throw RealtimeTranslationError.invalidMessage
            }

            try await send(.sessionUpdate(config))

            let updated = try await receiveDirectEvent(
                timeoutNanoseconds: sessionUpdateTimeoutNanoseconds
            )
            if case .error(let message, let code) = updated {
                throw classifyServerError(message: message, code: code)
            }
            guard case .sessionUpdated = updated else {
                throw RealtimeTranslationError.invalidMessage
            }

            guard currentEpoch == epoch else {
                throw RealtimeTranslationError.cancelled
            }
            isReady = true
            startReceiveLoop(epoch: currentEpoch)
        } catch {
            await tearDownTransport()
            throw error
        }
    }

    func appendAudioFrame(_ pcm16LE: Data) async throws {
        guard isReady, !isClosing else {
            throw RealtimeTranslationError.notConnected
        }
        let base64 = pcm16LE.base64EncodedString()
        try await send(.inputAudioBufferAppend(base64Audio: base64))
    }

    func closeGracefully() async throws {
        guard !isClosing else { return }
        isClosing = true
        isReady = false

        try? await send(.sessionClose)

        let deadline = ContinuousClock.now + .nanoseconds(Int64(closeTimeoutNanoseconds))
        while ContinuousClock.now < deadline {
            if didReceiveClosed {
                await tearDownTransport()
                return
            }
            do {
                try await Task.sleep(nanoseconds: 50_000_000)
            } catch is CancellationError {
                // async let の片方が失敗してキャンセルされたとき、期限まで待たない。
                await tearDownTransport()
                throw CancellationError()
            }
        }
        await tearDownTransport()
        throw RealtimeTranslationError.closeTimeout
    }

    func forceClose() async {
        isClosing = true
        isReady = false
        epoch += 1
        await tearDownTransport()
    }

    private func send(_ event: RealtimeTranslationClientEvent) async throws {
        let data = try RealtimeTranslationMessageCodec.encode(event)
        try await transport.send(data)
    }

    private func receiveDirectEvent(
        timeoutNanoseconds: UInt64
    ) async throws -> RealtimeTranslationServerEvent {
        try await withThrowingTaskGroup(
            of: RealtimeTranslationServerEvent.self
        ) { group in
            group.addTask {
                let data = try await self.transport.receive()
                return try RealtimeTranslationMessageCodec.decodeServerEvent(from: data)
            }
            group.addTask {
                try await Task.sleep(nanoseconds: timeoutNanoseconds)
                throw RealtimeTranslationError.sessionUpdateTimeout
            }
            let result = try await group.next()!
            group.cancelAll()
            return result
        }
    }

    private func startReceiveLoop(epoch currentEpoch: Int) {
        receiveTask?.cancel()
        receiveTask = Task {
            while !Task.isCancelled {
                do {
                    let data = try await transport.receive()
                    guard currentEpoch == epoch else { return }
                    let event = try RealtimeTranslationMessageCodec.decodeServerEvent(from: data)
                    if case .sessionClosed = event {
                        didReceiveClosed = true
                    }
                    publish(event, epoch: currentEpoch)
                    if case .sessionClosed = event {
                        finishEventStream()
                        return
                    }
                } catch is CancellationError {
                    return
                } catch {
                    guard currentEpoch == epoch else { return }
                    publish(
                        .error(
                            message: "翻訳サーバーとの接続が切れました",
                            code: "transport"
                        ),
                        epoch: currentEpoch
                    )
                    finishEventStream()
                    return
                }
            }
        }
    }

    private func publish(
        _ event: RealtimeTranslationServerEvent,
        epoch currentEpoch: Int
    ) {
        switch event {
        case .outputAudioDelta:
            outputAudioEventCount += 1
            if outputAudioEventCount == 1 || outputAudioEventCount.isMultiple(of: 25) {
                AppLogger.realtime.notice(
                    "DBG_OUTPUT_AUDIO_EVENT target=\(self.target.rawValue, privacy: .public) count=\(self.outputAudioEventCount, privacy: .public) epoch=\(currentEpoch, privacy: .public)"
                )
            }
        case .inputTranscriptDelta:
            AppLogger.realtime.notice(
                "DBG_TRANSCRIPT_EVENT target=\(self.target.rawValue, privacy: .public) kind=input epoch=\(currentEpoch, privacy: .public)"
            )
        case .outputTranscriptDelta:
            AppLogger.realtime.notice(
                "DBG_TRANSCRIPT_EVENT target=\(self.target.rawValue, privacy: .public) kind=output epoch=\(currentEpoch, privacy: .public)"
            )
        case .error(_, let code):
            AppLogger.realtime.error(
                "Realtime translation error code=\(code ?? "none", privacy: .public)"
            )
        case .unknown(let type):
            AppLogger.realtime.notice(
                "Unknown realtime event type=\(type, privacy: .public)"
            )
        default:
            break
        }
        eventContinuation?.yield(
            RealtimeTranslationStreamEvent(
                target: target,
                event: event,
                epoch: currentEpoch
            )
        )
    }

    private func classifyServerError(message: String, code: String?) -> RealtimeTranslationError {
        let lowered = (code ?? message).lowercased()
        if lowered.contains("auth")
            || lowered.contains("unauthorized")
            || lowered.contains("invalid_api_key")
            || lowered.contains("401")
            || lowered.contains("403")
        {
            return .authenticationFailed
        }
        return .fatalServerError(message)
    }

    private func tearDownTransport() async {
        receiveTask?.cancel()
        receiveTask = nil
        await transport.close()
        finishEventStream()
    }

    private func finishEventStream() {
        eventContinuation?.finish()
        eventContinuation = nil
    }

    private func recreateEventStream() {
        finishEventStream()
        let pair = Self.makeEventStream()
        events = pair.stream
        eventContinuation = pair.continuation
    }

    private static func makeEventStream() -> (
        stream: AsyncStream<RealtimeTranslationStreamEvent>,
        continuation: AsyncStream<RealtimeTranslationStreamEvent>.Continuation
    ) {
        var continuation: AsyncStream<RealtimeTranslationStreamEvent>.Continuation!
        let stream = AsyncStream(bufferingPolicy: .bufferingNewest(256)) {
            continuation = $0
        }
        return (stream, continuation)
    }

    deinit {
        eventContinuation?.finish()
    }
}
