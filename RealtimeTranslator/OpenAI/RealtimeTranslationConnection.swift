import Foundation
import os

struct RealtimeTranslationStreamEvent: Sendable, Equatable {
    let lane: RealtimeTranslationLane
    let event: RealtimeTranslationServerEvent
    let epoch: Int

    var target: RealtimeTranslationOutputLanguage {
        guard let target = lane.target else {
            preconditionFailure("source lane has no translation target")
        }
        return target
    }

    init(
        target: RealtimeTranslationOutputLanguage,
        event: RealtimeTranslationServerEvent,
        epoch: Int
    ) {
        if case .inputTranscriptDelta = event {
            self.lane = .source
        } else {
            self.lane = .translation(target)
        }
        self.event = event
        self.epoch = epoch
    }

    init(
        lane: RealtimeTranslationLane,
        event: RealtimeTranslationServerEvent,
        epoch: Int
    ) {
        self.lane = lane
        self.event = event
        self.epoch = epoch
    }
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
        safetyIdentifier: String,
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
        let apiKey = try RealtimeTranslationError.requireNormalizedAPIKey(apiKey)

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
        // handshake未完了では receive loop が無いため session.closed を待てない。
        // 原文接続と同様に即 force-close し、停止が closeTimeout まで固まらないようにする。
        guard isReady else {
            await forceClose()
            return
        }
        isClosing = true
        isReady = false
        // forceClose / 次の start が進めた epoch のソケットを、古い close 待ちが閉じない。
        let closeEpoch = epoch

        try? await send(.sessionClose)

        let deadline = ContinuousClock.now + .nanoseconds(Int64(closeTimeoutNanoseconds))
        while ContinuousClock.now < deadline {
            if didReceiveClosed {
                await tearDownTransportIfCurrentEpoch(closeEpoch)
                return
            }
            do {
                try await Task.sleep(nanoseconds: 50_000_000)
            } catch is CancellationError {
                // async let の片方が失敗してキャンセルされたとき、期限まで待たない。
                await tearDownTransportIfCurrentEpoch(closeEpoch)
                throw CancellationError()
            }
        }
        await tearDownTransportIfCurrentEpoch(closeEpoch)
        guard closeEpoch == epoch else { return }
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
        // URLSessionWebSocketTask.receive は Swift Task キャンセルを見ない。
        // timeout / 親 Task キャンセルで transport を閉じ、TaskGroup の残り待ちを解く。
        let transport = self.transport
        return try await withTaskCancellationHandler {
            try await withThrowingTaskGroup(
                of: RealtimeTranslationServerEvent.self
            ) { group in
                group.addTask {
                    let data = try await transport.receive()
                    return try RealtimeTranslationMessageCodec.decodeServerEvent(from: data)
                }
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
                            message: UiCopy.text("error.transportDisconnected"),
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
            #if DEBUG
            if outputAudioEventCount == 1 || outputAudioEventCount.isMultiple(of: 25) {
                AppLogger.realtime.notice(
                    "DBG_OUTPUT_AUDIO_EVENT target=\(self.target.rawValue, privacy: .public) count=\(self.outputAudioEventCount, privacy: .public) epoch=\(currentEpoch, privacy: .public)"
                )
            }
            #endif
            // MVP は翻訳音声を再生しない。AsyncStream(bufferingNewest) へ入れると
            // Stop の close-drain 待ちで字幕 delta が落ちうるので受信カウントのみにする。
            return
        case .inputTranscriptDelta:
            #if DEBUG
            AppLogger.realtime.notice(
                "DBG_TRANSCRIPT_EVENT target=\(self.target.rawValue, privacy: .public) kind=input epoch=\(currentEpoch, privacy: .public)"
            )
            #endif
            // 翻訳接続の input_transcript は原文 authority にしない（専用 transcription のみ）。
            // target=en 翻訳セッションの delta を通すと assembler が原文として取り込む。
            return
        case .outputTranscriptDelta:
            #if DEBUG
            AppLogger.realtime.notice(
                "DBG_TRANSCRIPT_EVENT target=\(self.target.rawValue, privacy: .public) kind=output epoch=\(currentEpoch, privacy: .public)"
            )
            #endif
        case .error:
            break
        case .unknown(let type):
            AppLogger.realtime.notice(
                "Unknown realtime event type=\(AppLogger.redact(type), privacy: .public)"
            )
        default:
            break
        }
        eventContinuation?.yield(
            RealtimeTranslationStreamEvent(
                lane: .translation(target),
                event: event,
                epoch: currentEpoch
            )
        )
    }

    private func classifyServerError(message: String, code: String?) -> RealtimeTranslationError {
        if RealtimeTranslationError.isAuthenticationFailure(code: code, message: message) {
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

    private func tearDownTransportIfCurrentEpoch(_ closeEpoch: Int) async {
        guard closeEpoch == epoch else { return }
        await tearDownTransport()
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
