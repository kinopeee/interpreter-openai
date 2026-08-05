import Foundation

actor RealtimeSourceTranscriptionConnection {
    static let endpointURL = URL(
        string: "wss://api.openai.com/v1/realtime?intent=transcription"
    )!

    private let transport: any RealtimeWebSocketTransport
    private let safetyIdentifier: String
    private let handshakeTimeoutNanoseconds: UInt64
    private let closeTimeoutNanoseconds: UInt64

    private var epoch = 0
    private var isReady = false
    private var didReceiveCompleted = false
    private var receiveTask: Task<Void, Never>?
    private var eventContinuation: AsyncStream<RealtimeTranslationStreamEvent>.Continuation?
    private(set) var events: AsyncStream<RealtimeTranslationStreamEvent>

    init(
        transport: any RealtimeWebSocketTransport = URLSessionWebSocketTransport(),
        safetyIdentifier: String = OpenAISafetyIdentifier.hashedValue(),
        handshakeTimeoutNanoseconds: UInt64 = 15_000_000_000,
        closeTimeoutNanoseconds: UInt64 = 5_000_000_000
    ) {
        self.transport = transport
        self.safetyIdentifier = safetyIdentifier
        self.handshakeTimeoutNanoseconds = handshakeTimeoutNanoseconds
        self.closeTimeoutNanoseconds = closeTimeoutNanoseconds
        (events, eventContinuation) = Self.makeEventStream()
    }

    func start(
        apiKey: String,
        tuning: RealtimeSessionTuning = .default
    ) async throws {
        await forceClose()
        recreateEventStream()
        epoch += 1
        let currentEpoch = epoch
        didReceiveCompleted = false

        do {
            try await transport.connect(
                url: Self.endpointURL,
                headers: [
                    "Authorization": "Bearer \(apiKey)",
                    "OpenAI-Safety-Identifier": safetyIdentifier,
                ]
            )
            let created = try await receiveJSON(timeoutNanoseconds: handshakeTimeoutNanoseconds)
            guard created["type"] as? String == "session.created" else {
                throw classifyError(created)
            }

            try await sendJSON(makeSessionUpdatePayload(tuning: tuning))

            let updated = try await receiveJSON(timeoutNanoseconds: handshakeTimeoutNanoseconds)
            guard updated["type"] as? String == "session.updated" else {
                throw classifyError(updated)
            }
            guard currentEpoch == epoch else {
                throw RealtimeTranslationError.cancelled
            }
            isReady = true
            startReceiveLoop(epoch: currentEpoch)
        } catch {
            await forceClose()
            throw error
        }
    }

    /// 録音中にprompt/keywordsを更新する。noise_reductionも同payloadで送るが接続時値を維持する。
    func updateTuning(_ tuning: RealtimeSessionTuning) async throws {
        guard isReady else {
            throw RealtimeTranslationError.notConnected
        }
        try await sendJSON(makeSessionUpdatePayload(tuning: tuning))
    }

    func appendAudioFrame(_ pcm16LE: Data) async throws {
        guard isReady else {
            throw RealtimeTranslationError.notConnected
        }
        try await sendJSON([
            "type": "input_audio_buffer.append",
            "audio": pcm16LE.base64EncodedString(),
        ])
    }

    func closeGracefully() async throws {
        guard isReady else {
            await forceClose()
            return
        }
        isReady = false
        try await sendJSON(["type": "input_audio_buffer.commit"])

        let deadline = ContinuousClock.now + .nanoseconds(Int64(closeTimeoutNanoseconds))
        while ContinuousClock.now < deadline {
            if didReceiveCompleted {
                await forceClose()
                return
            }
            try? await Task.sleep(nanoseconds: 50_000_000)
        }
        await forceClose()
        throw RealtimeTranslationError.closeTimeout
    }

    func forceClose() async {
        isReady = false
        epoch += 1
        receiveTask?.cancel()
        receiveTask = nil
        await transport.close()
        eventContinuation?.finish()
        eventContinuation = nil
    }

    private func startReceiveLoop(epoch currentEpoch: Int) {
        receiveTask?.cancel()
        receiveTask = Task {
            while !Task.isCancelled {
                do {
                    let object = try await receiveJSON()
                    guard currentEpoch == epoch else { return }
                    let type = object["type"] as? String ?? ""
                    switch type {
                    case "conversation.item.input_audio_transcription.delta":
                        let delta = object["delta"] as? String ?? ""
                        guard !delta.isEmpty else { continue }
                        AppLogger.realtime.notice(
                            "DBG_TRANSCRIPT_EVENT target=source kind=input epoch=\(currentEpoch, privacy: .public)"
                        )
                        eventContinuation?.yield(
                            RealtimeTranslationStreamEvent(
                                target: .english,
                                event: .inputTranscriptDelta(
                                    delta: delta,
                                    // item_idは同一turnの全deltaで共通なので重複排除に使わない。
                                    eventID: object["event_id"] as? String,
                                    elapsedMs: nil
                                ),
                                epoch: currentEpoch
                            )
                        )
                    case "conversation.item.input_audio_transcription.completed":
                        didReceiveCompleted = true
                    case "error":
                        let error = classifyError(object)
                        eventContinuation?.yield(
                            RealtimeTranslationStreamEvent(
                                target: .english,
                                event: .error(
                                    message: error.localizedDescription,
                                    code: "transcription"
                                ),
                                epoch: currentEpoch
                            )
                        )
                    default:
                        break
                    }
                } catch is CancellationError {
                    return
                } catch {
                    guard currentEpoch == epoch else { return }
                    eventContinuation?.yield(
                        RealtimeTranslationStreamEvent(
                            target: .english,
                            event: .error(
                                message: "原文字幕サーバーとの接続が切れました",
                                code: "transport"
                            ),
                            epoch: currentEpoch
                        )
                    )
                    return
                }
            }
        }
    }

    private func makeSessionUpdatePayload(tuning: RealtimeSessionTuning) -> [String: Any] {
        [
            "type": "session.update",
            "session": [
                "type": "transcription",
                "audio": [
                    "input": [
                        "format": [
                            "type": "audio/pcm",
                            "rate": 24_000,
                        ],
                        "transcription": [
                            "model": "gpt-live-transcribe",
                            "languages": ["ja", "en"],
                            "delay": tuning.transcriptionDelay.rawValue,
                            "prompt": tuning.transcriptionPrompt,
                            "keywords": tuning.transcriptionKeywords,
                        ],
                        "noise_reduction": [
                            "type": tuning.noiseReduction.rawValue,
                        ],
                        "turn_detection": NSNull(),
                    ],
                ],
            ],
        ]
    }

    private func sendJSON(_ object: [String: Any]) async throws {
        let data = try JSONSerialization.data(withJSONObject: object)
        try await transport.send(data)
    }

    private func receiveJSON(
        timeoutNanoseconds: UInt64? = nil
    ) async throws -> [String: Any] {
        let data: Data
        if let timeoutNanoseconds {
            data = try await withThrowingTaskGroup(of: Data.self) { group in
                group.addTask { try await self.transport.receive() }
                group.addTask {
                    try await Task.sleep(nanoseconds: timeoutNanoseconds)
                    throw RealtimeTranslationError.sessionUpdateTimeout
                }
                let result = try await group.next()!
                group.cancelAll()
                return result
            }
        } else {
            data = try await transport.receive()
        }
        guard let object = try JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            throw RealtimeTranslationError.invalidMessage
        }
        return object
    }

    private func classifyError(_ object: [String: Any]) -> RealtimeTranslationError {
        let body = object["error"] as? [String: Any]
        let message = body?["message"] as? String ?? "原文字幕セッションでエラーが発生しました"
        let code = body?["code"] as? String ?? ""
        let lowered = "\(code) \(message)".lowercased()
        if lowered.contains("auth")
            || lowered.contains("invalid_api_key")
            || lowered.contains("401")
            || lowered.contains("403")
        {
            return .authenticationFailed
        }
        return .fatalServerError(message)
    }

    private func recreateEventStream() {
        eventContinuation?.finish()
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
