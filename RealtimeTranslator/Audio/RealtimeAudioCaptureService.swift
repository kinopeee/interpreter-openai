@preconcurrency import AVFoundation
import Foundation
import os

enum RealtimeAudioFrameYieldOutcome {
    static func isTerminated<Element>(
        _ result: AsyncStream<Element>.Continuation.YieldResult
    ) -> Bool {
        switch result {
        case .enqueued:
            return false
        case .dropped:
            return false
        case .terminated:
            return true
        @unknown default:
            return true
        }
    }
}

enum RealtimeAudioCaptureError: Error, LocalizedError, Sendable {
    case microphoneDenied
    case audioFormatUnavailable
    case audioConverterUnavailable
    case audioBufferPoolUnavailable
    case pipelineOverloaded
    case inputDeviceChanged

    var errorDescription: String? {
        switch self {
        case .microphoneDenied:
            return UiCopy.text("error.micDenied")
        case .audioFormatUnavailable:
            return UiCopy.text("error.micFormatUnavailable")
        case .audioConverterUnavailable:
            return UiCopy.text("error.micConverterUnavailable")
        case .audioBufferPoolUnavailable:
            return UiCopy.text("error.micBufferUnavailable")
        case .pipelineOverloaded:
            return UiCopy.text("error.micPipelineOverloaded")
        case .inputDeviceChanged:
            return UiCopy.text("error.micDeviceChanged")
        }
    }
}

@MainActor
protocol RealtimeAudioCaptureServicing: AnyObject {
    var frames: AsyncStream<CapturedAudioFrame> { get }
    /// `frames` が終端した理由。正常停止時は nil。
    var terminationError: Error? { get }
    func start() async throws
    func stop() async
}

@MainActor
final class RealtimeAudioCaptureService: RealtimeAudioCaptureServicing {
    private static let tapBufferSize: AVAudioFrameCount = 4096
    private static let bufferPoolCapacity = 64
    private static let targetSampleRate = 24_000.0

    private let audioEngine = AVAudioEngine()
    private let discardedMilliseconds = OSAllocatedUnfairLock(initialState: 0)
    private var frameQueue: RealtimeAudioFrameQueue
    private var captureContinuation: AsyncStream<CapturedAudioBuffer>.Continuation?
    private var feederTask: Task<Void, Never>?
    private var configurationObserver: (any NSObjectProtocol)?
    private var isTapInstalled = false
    private var lifecycleGeneration = 0
    private(set) var frames: AsyncStream<CapturedAudioFrame>
    private(set) var terminationError: Error?

    init() {
        frameQueue = RealtimeAudioFrameQueue()
        frames = frameQueue.frames
    }

    func start() async throws {
        guard feederTask == nil else { return }
        lifecycleGeneration += 1
        let generation = lifecycleGeneration
        terminationError = nil
        discardedMilliseconds.withLock { $0 = 0 }
        recreateFrameStream()

        let microphoneGranted = await requestMicrophonePermission()
        guard generation == lifecycleGeneration else { throw CancellationError() }
        guard microphoneGranted else {
            throw RealtimeAudioCaptureError.microphoneDenied
        }

        let inputNode = audioEngine.inputNode
        let inputFormat = inputNode.outputFormat(forBus: 0)
        guard inputFormat.sampleRate > 0, inputFormat.channelCount > 0 else {
            throw RealtimeAudioCaptureError.audioFormatUnavailable
        }
        #if DEBUG
        AppLogger.audio.notice(
            "DBG_CAPTURE_START rate=\(inputFormat.sampleRate, privacy: .public) channels=\(inputFormat.channelCount, privacy: .public)"
        )
        #endif

        guard let targetFormat = AVAudioFormat(
            commonFormat: .pcmFormatFloat32,
            sampleRate: Self.targetSampleRate,
            channels: 1,
            interleaved: false
        ) else {
            throw RealtimeAudioCaptureError.audioFormatUnavailable
        }

        guard let converter = AnalyzerAudioConverter(
            inputFormat: inputFormat,
            outputFormat: targetFormat
        ) else {
            throw RealtimeAudioCaptureError.audioConverterUnavailable
        }

        let captureFrameCapacity = max(
            Self.tapBufferSize,
            AVAudioFrameCount(inputFormat.sampleRate.rounded(.up))
        )
        guard let bufferPool = CapturedAudioBufferPool(
            format: inputFormat,
            frameCapacity: captureFrameCapacity,
            capacity: Self.bufferPoolCapacity
        ) else {
            throw RealtimeAudioCaptureError.audioBufferPoolUnavailable
        }

        let (captureStream, captureContinuation) =
            AsyncStream<CapturedAudioBuffer>.makeStream(
                bufferingPolicy: .bufferingNewest(Self.bufferPoolCapacity - 2)
            )
        self.captureContinuation = captureContinuation

        let audioTap = AnalyzerAudioTap(
            continuation: captureContinuation,
            bufferPool: bufferPool,
            discardedMilliseconds: discardedMilliseconds,
            inputSampleRate: inputFormat.sampleRate
        )
        let tapBlock: @Sendable (AVAudioPCMBuffer, AVAudioTime) -> Void = { buffer, _ in
            audioTap.receive(buffer)
        }
        inputNode.removeTap(onBus: 0)
        inputNode.installTap(
            onBus: 0,
            bufferSize: Self.tapBufferSize,
            format: inputFormat,
            block: tapBlock
        )
        isTapInstalled = true

        feederTask = Task.detached(priority: .userInitiated) { [weak self] in
            var packetizer = PCM16FramePacketizer()
            var adaptiveGain = AdaptiveMicrophoneGain()
            var emittedFrameCount = 0
            do {
                for await captured in captureStream {
                    defer { captured.release() }
                    try Task.checkCancellation()
                    let converted = try converter.convert(captured.buffer)
                    let pcm16 = try Self.encodePCM16(
                        from: converted,
                        adaptiveGain: &adaptiveGain
                    )
                    let frames = packetizer.append(pcm16)
                    for frame in frames {
                        emittedFrameCount += 1
                        #if DEBUG
                        if emittedFrameCount == 1 || emittedFrameCount.isMultiple(of: 25) {
                            let peak = Self.peakAmplitude(inPCM16LE: frame)
                            AppLogger.audio.notice(
                                "DBG_CAPTURE_FRAME count=\(emittedFrameCount, privacy: .public) bytes=\(frame.count, privacy: .public) peak=\(peak, privacy: .public)"
                            )
                        }
                        #endif
                        let result = await self?.yieldFrame(frame, generation: generation)
                        if result == false {
                            await self?.reportFailure(
                                RealtimeAudioCaptureError.pipelineOverloaded,
                                generation: generation
                            )
                            return
                        }
                    }
                }
                if let padded = packetizer.flushWithSilencePadding() {
                    _ = await self?.yieldFrame(padded, generation: generation)
                }
            } catch is CancellationError {
                return
            } catch {
                AppLogger.audio.error(
                    "Audio feeder failed: \(AppLogger.redact(error.localizedDescription), privacy: .public)"
                )
                await self?.reportFailure(error, generation: generation)
            }
        }

        do {
            audioEngine.prepare()
            try audioEngine.start()
        } catch {
            // tap / feeder を残したまま返すと、後続startが early-return して録音不能になる。
            await stop()
            throw error
        }
        guard generation == lifecycleGeneration else {
            await stop()
            throw CancellationError()
        }

        // prepare/start 周辺の誤検知を避けるため、起動成功後にだけ監視する。
        removeConfigurationObserver()
        configurationObserver = NotificationCenter.default.addObserver(
            forName: .AVAudioEngineConfigurationChange,
            object: audioEngine,
            queue: nil
        ) { [weak self] _ in
            // コールバックは MainActor を仮定しない。境界は @Sendable ヘルパーへ集約する。
            Self.enqueueConfigurationChange(service: self, generation: generation)
        }
    }

    func stop() async {
        lifecycleGeneration += 1
        removeConfigurationObserver()
        if isTapInstalled {
            audioEngine.inputNode.removeTap(onBus: 0)
            isTapInstalled = false
        }
        audioEngine.stop()
        captureContinuation?.finish()
        captureContinuation = nil
        if let feederTask {
            await feederTask.value
        }
        feederTask = nil
        frameQueue.finish()
    }

    private func removeConfigurationObserver() {
        if let configurationObserver {
            NotificationCenter.default.removeObserver(configurationObserver)
            self.configurationObserver = nil
        }
    }

    /// NotificationCenter コールバックから MainActor 上の判定へ渡す境界。
    nonisolated private static func enqueueConfigurationChange(
        service: RealtimeAudioCaptureService?,
        generation: Int
    ) {
        Task { @MainActor in
            service?.handleConfigurationChange(generation: generation)
        }
    }

    private func handleConfigurationChange(generation: Int) {
        guard generation == lifecycleGeneration else { return }

        // Apple はハードウェア変更時に engine を停止してから本通知を出す。
        // 監視は start 成功後にだけ付け、stop 前に外すため、ここへ来たら切断/切替として扱う。
        reportFailure(
            RealtimeAudioCaptureError.inputDeviceChanged,
            generation: generation
        )
    }

    private func yieldFrame(_ frame: Data, generation: Int) -> Bool {
        let discarded = discardedMilliseconds.withLock { $0 }
        return frameQueue.enqueue(
            pcm16: frame,
            generation: generation,
            discardedMilliseconds: discarded,
            capturedAt: .now
        )
    }

    private func reportFailure(_ error: Error, generation: Int) {
        guard generation == lifecycleGeneration else { return }
        // 先に付いた理由（例: マイク切断）を pipelineOverloaded で上書きしない。
        if terminationError == nil {
            terminationError = error
        }
        AppLogger.audio.error(
            "Realtime audio capture failed: \(AppLogger.redact(error.localizedDescription), privacy: .public)"
        )
        // feeder の for-await を終わらせ、追加 yield → pipelineOverloaded を防ぐ。
        captureContinuation?.finish()
        captureContinuation = nil
        frameQueue.finish()
        removeConfigurationObserver()
    }

    private func recreateFrameStream() {
        frameQueue.finish()
        frameQueue = RealtimeAudioFrameQueue()
        frames = frameQueue.frames
    }

    private func requestMicrophonePermission() async -> Bool {
        await withCheckedContinuation { continuation in
            AVCaptureDevice.requestAccess(for: .audio) { granted in
                continuation.resume(returning: granted)
            }
        }
    }

    nonisolated private static func encodePCM16(
        from buffer: AVAudioPCMBuffer,
        adaptiveGain: inout AdaptiveMicrophoneGain
    ) throws -> Data {
        let frameLength = Int(buffer.frameLength)
        guard frameLength > 0 else { return Data() }

        if buffer.format.commonFormat == .pcmFormatInt16,
           let channel = buffer.int16ChannelData?[0]
        {
            return PCM16LittleEndianEncoder.encode(
                int16Samples: channel,
                frameCount: frameLength
            )
        }

        guard let channel = buffer.floatChannelData?[0] else {
            throw RealtimeAudioCaptureError.audioFormatUnavailable
        }
        let gain = adaptiveGain.observe(floatSamples: channel, frameCount: frameLength)
        return PCM16LittleEndianEncoder.encode(
            floatSamples: channel,
            frameCount: frameLength,
            gain: gain
        )
    }

    nonisolated private static func peakAmplitude(inPCM16LE data: Data) -> Int {
        data.withUnsafeBytes { rawBuffer in
            rawBuffer.bindMemory(to: Int16.self).reduce(into: 0) { peak, sample in
                peak = max(peak, abs(Int(sample)))
            }
        }
    }
}
