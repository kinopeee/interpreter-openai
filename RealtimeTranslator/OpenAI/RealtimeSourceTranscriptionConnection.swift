import Foundation

actor RealtimeSourceTranscriptionConnection {
    static let eventBufferLimit = 256
    static let endpointURL = URL(
        string: "wss://api.openai.com/v1/realtime?intent=transcription"
    )!

    private let transport: any RealtimeWebSocketTransport
    private let safetyIdentifier: String
    private let handshakeTimeoutNanoseconds: UInt64
    private let closeTimeoutNanoseconds: UInt64

    private var epoch = 0
    private var isReady = false
    private var didReceiveCommitOutcome = false
    /// commit 送信完了後だけ立てる。送信待ち中の録音時 outcome を commit 結果にしない。
    private var isAwaitingCommitOutcome = false
    private var languagePair: LanguagePair = .jaEn
    /// 接続開始時のnoise_reduction。live updateでは変更しない。
    private var connectedNoiseReduction: RealtimeTranslationNoiseReduction = .farField
    private var receiveTask: Task<Void, Never>?
    private var eventContinuation: AsyncStream<RealtimeTranslationStreamEvent>.Continuation?
    private var deliveryYielder: EventDeliveryYielder?
    private(set) var events: AsyncStream<RealtimeTranslationStreamEvent>

    init(
        transport: any RealtimeWebSocketTransport = URLSessionWebSocketTransport(),
        safetyIdentifier: String,
        handshakeTimeoutNanoseconds: UInt64 = RealtimeTranslationConnection.defaultHandshakeTimeoutNanoseconds,
        closeTimeoutNanoseconds: UInt64 = 5_000_000_000
    ) {
        self.transport = transport
        self.safetyIdentifier = safetyIdentifier
        self.handshakeTimeoutNanoseconds = handshakeTimeoutNanoseconds
        self.closeTimeoutNanoseconds = closeTimeoutNanoseconds
        (events, eventContinuation) = RealtimeTransportSupport.makeEventStream(
            bufferingLimit: Self.eventBufferLimit
        )
    }

    func start(
        apiKey: String,
        tuning: RealtimeSessionTuning = .default,
        pair: LanguagePair,
        deliveryState: EventDeliveryState? = nil
    ) async throws {
        await forceClose()
        recreateEventStream()
        epoch += 1
        let currentEpoch = epoch
        let state = deliveryState ?? EventDeliveryState(epoch: currentEpoch)
        deliveryYielder = EventDeliveryYielder(
            continuation: eventContinuation!,
            deliveryState: state,
            stage: .source,
            capacity: Self.eventBufferLimit
        )
        didReceiveCommitOutcome = false
        isAwaitingCommitOutcome = false

        let apiKey = try RealtimeTranslationError.requireNormalizedAPIKey(apiKey)

        do {
            try await transport.connect(
                url: Self.endpointURL,
                headers: [
                    "Authorization": "Bearer \(apiKey)",
                    "OpenAI-Safety-Identifier": safetyIdentifier,
                ]
            )
            let created = try await receiveHandshakeJSON(timeoutNanoseconds: handshakeTimeoutNanoseconds)
            guard created["type"] as? String == "session.created" else {
                throw RealtimeTranslationError.invalidMessage
            }

            connectedNoiseReduction = tuning.noiseReduction
            languagePair = pair
            try await sendJSON(makeSessionUpdatePayload(tuning: tuning, pair: pair))

            let updated = try await receiveHandshakeJSON(timeoutNanoseconds: handshakeTimeoutNanoseconds)
            guard updated["type"] as? String == "session.updated" else {
                throw RealtimeTranslationError.invalidMessage
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

    /// 録音中にprompt/keywords/delayを更新する。noise_reductionは接続時値を維持する。
    func updateTuning(_ tuning: RealtimeSessionTuning) async throws {
        guard isReady else {
            throw RealtimeTranslationError.notConnected
        }
        var liveTuning = tuning
        liveTuning.noiseReduction = connectedNoiseReduction
        try await sendJSON(makeSessionUpdatePayload(tuning: liveTuning, pair: languagePair))
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
        // 録音中の failed / completed を、この commit の結果として使わない。
        didReceiveCommitOutcome = false
        isAwaitingCommitOutcome = true
        try await sendJSON(["type": "input_audio_buffer.commit"])

        let deadline = ContinuousClock.now + .nanoseconds(Int64(closeTimeoutNanoseconds))
        while ContinuousClock.now < deadline {
            if didReceiveCommitOutcome {
                await forceClose()
                return
            }
            do {
                try await Task.sleep(nanoseconds: 50_000_000)
            } catch is CancellationError {
                // DualClientの並行closeでキャンセルされたとき、期限まで待たない。
                await forceClose()
                throw CancellationError()
            }
        }
        await forceClose()
        throw RealtimeTranslationError.closeTimeout
    }

    func forceClose() async {
        isReady = false
        isAwaitingCommitOutcome = false
        epoch += 1
        receiveTask?.cancel()
        receiveTask = nil
        await transport.close()
        RealtimeTransportSupport.finishEventStream(
            eventContinuation: &eventContinuation,
            deliveryYielder: &deliveryYielder
        )
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
                        #if DEBUG
                        AppLogger.realtime.notice(
                            "DBG_TRANSCRIPT_EVENT target=source kind=input epoch=\(currentEpoch, privacy: .public)"
                        )
                        #endif
                        guard deliveryYielder?.deliver(
                            RealtimeTranslationStreamEvent(
                                lane: .source,
                                event: .inputTranscriptDelta(
                                    delta: delta,
                                    // item_idは同一turnの全deltaで共通なので重複排除に使わない。
                                    eventID: object["event_id"] as? String,
                                    elapsedMs: nil
                                ),
                                epoch: currentEpoch
                            )
                        ) == true else { return }
                    case "conversation.item.input_audio_transcription.completed":
                        if isAwaitingCommitOutcome {
                            didReceiveCommitOutcome = true
                        }
                    case "conversation.item.input_audio_transcription.failed":
                        if isAwaitingCommitOutcome {
                            didReceiveCommitOutcome = true
                        }
                        let error = object["error"] as? [String: Any]
                        let classification = EventDeliveryState.classifyTranscriptionFailure(
                            errorType: error?["type"] as? String,
                            code: error?["code"] as? String
                        )
                        if classification.disposition != .keepAlive {
                            deliveryYielder?.deliveryState.tryRecordTermination(classification)
                        }
                        guard deliveryYielder?.deliver(
                            RealtimeTranslationStreamEvent(
                                lane: .source,
                                event: .inputTranscriptFailed(
                                    itemID: object["item_id"] as? String,
                                    eventID: object["event_id"] as? String,
                                    code: error?["code"] as? String,
                                    errorType: error?["type"] as? String
                                ),
                                epoch: currentEpoch
                            )
                        ) == true else { return }
                    case "error":
                        let serverError = Self.serverError(object)
                        let classification = EventDeliveryState.classify(
                            errorType: serverError.errorType,
                            code: serverError.code,
                            message: serverError.message
                        )
                        // keepAlive は接続も stream もそのまま。termination も下流イベントも出さない。
                        if classification.disposition == .keepAlive {
                            continue
                        }
                        deliveryYielder?.deliveryState.tryRecordTermination(classification)
                        guard deliveryYielder?.deliver(
                            RealtimeTranslationStreamEvent(
                                lane: .source,
                                event: .error(
                                    message: serverError.message,
                                    code: serverError.code,
                                    errorType: serverError.errorType
                                ),
                                epoch: currentEpoch
                            )
                        ) == true else { return }
                    default:
                        break
                    }
                } catch is CancellationError {
                    return
                } catch {
                    guard currentEpoch == epoch else { return }
                    deliveryYielder?.deliveryState.tryRecordTermination(.transportFailure)
                    guard deliveryYielder?.deliver(
                        RealtimeTranslationStreamEvent(
                            lane: .source,
                            event: .error(
                                message: UiCopy.text("error.sourceDisconnected"),
                                code: RealtimeServerErrorClassification.transportCode,
                                errorType: nil
                            ),
                            epoch: currentEpoch
                        )
                        ) == true else { return }
                    return
                }
            }
        }
    }

    private func makeSessionUpdatePayload(
        tuning: RealtimeSessionTuning,
        pair: LanguagePair
    ) -> [String: Any] {
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
                            "languages": pair.languages.map { language in
                                switch language {
                                case .japanese: return "ja"
                                case .english: return "en"
                                case .spanish: return "es"
                                case .unknown: return ""
                                }
                            },
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
            data = try await RealtimeTransportSupport.receiveWithTimeout(
                from: transport,
                timeoutNanoseconds: timeoutNanoseconds
            )
        } else {
            data = try await transport.receive()
        }
        guard let object = try JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            throw RealtimeTranslationError.invalidMessage
        }
        return object
    }

    /// handshake 中の error も翻訳接続と同じ分類で扱う。keepAlive は読み飛ばして次を待つ。
    /// 期限は handshake 1 段あたり 1 つ（keep-alive で延長しない）。
    private func receiveHandshakeJSON(timeoutNanoseconds: UInt64) async throws -> [String: Any] {
        let deadline = ContinuousClock.now + .nanoseconds(Int64(timeoutNanoseconds))
        while true {
            let remaining = ContinuousClock.now.duration(to: deadline)
            guard remaining > .zero else {
                throw RealtimeTranslationError.sessionUpdateTimeout
            }
            let object = try await receiveJSON(
                timeoutNanoseconds: UInt64(ReconnectBudget.nanoseconds(remaining))
            )
            guard object["type"] as? String == "error" else {
                return object
            }
            let serverError = Self.serverError(object)
            let classification = RealtimeServerErrorClassification.classify(
                errorType: serverError.errorType,
                code: serverError.code,
                message: serverError.message
            )
            if classification.disposition == .keepAlive {
                continue
            }
            throw classification.makeError()
        }
    }

    /// `error.code` / `error.type` は原文接続固有の値へ置き換えず、サーバーの値をそのまま保持する。
    static func serverError(_ object: [String: Any]) -> (message: String, code: String?, errorType: String?) {
        let body = object["error"] as? [String: Any]
        let message = body?["message"] as? String ?? UiCopy.text("error.sourceSessionGeneric")
        return (message, body?["code"] as? String, body?["type"] as? String)
    }

    private func recreateEventStream() {
        RealtimeTransportSupport.finishEventStream(
            eventContinuation: &eventContinuation,
            deliveryYielder: &deliveryYielder
        )
        (events, eventContinuation) = RealtimeTransportSupport.makeEventStream(
            bufferingLimit: Self.eventBufferLimit
        )
    }

    deinit {
        eventContinuation?.finish()
    }
}
