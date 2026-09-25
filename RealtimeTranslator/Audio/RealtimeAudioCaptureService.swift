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
    private let discardedFrames = OSAllocatedUnfairLock(initialState: 0)
    private var frameQueue: RealtimeAudioFrameQueue
    private var captureContinuation: AsyncStream<CapturedAudioBuffer>.Continuation?
    private var feederTask: Task<Void, Never>?
    private var configurationObserver: (any NSObjectProtocol)?
    private var isTapInstalled = false
    private var lifecycleGeneration = 0
    private var inputSampleRate = 0.0
    private(set) var frames: AsyncStream<CapturedAudioFrame>
    private(set) var terminationError: Error?
    /// 録音開始時に一度だけ読み、適応マイクゲインの有効/無効を決める。
    private let automaticGainProvider: @MainActor () -> Bool

    init(automaticGainProvider: @escaping @MainActor () -> Bool = { true }) {
        self.automaticGainProvider = automaticGainProvider
        frameQueue = RealtimeAudioFrameQueue()
        frames = frameQueue.frames
    }

    func start() async throws {
        guard feederTask == nil else { return }
        lifecycleGeneration += 1
        let generation = lifecycleGeneration
        terminationError = nil
        discardedFrames.withLock { $0 = 0 }
        let automaticGainEnabled = automaticGainProvider()
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
        inputSampleRate = inputFormat.sampleRate
        #if DEBUG
        AppLogger.audio.notice(
            "DBG_CAPTURE_START rate=\(inputFormat.sampleRate, privacy: .public) channels=\(inputFormat.channelCount, privacy: .public)"
        )
        #endif

        guard
            let targetFormat = AVAudioFormat(
                commonFormat: .pcmFormatFloat32,
                sampleRate: Self.targetSampleRate,
                channels: 1,
                interleaved: false
            )
        else {
            throw RealtimeAudioCaptureError.audioFormatUnavailable
        }

        guard
            let converter = AnalyzerAudioConverter(
                inputFormat: inputFormat,
                outputFormat: targetFormat
            )
        else {
            throw RealtimeAudioCaptureError.audioConverterUnavailable
        }

        let captureFrameCapacity = max(
            Self.tapBufferSize,
            AVAudioFrameCount(inputFormat.sampleRate.rounded(.up))
        )
        guard
            let bufferPool = CapturedAudioBufferPool(
                format: inputFormat,
                frameCapacity: captureFrameCapacity,
                capacity: Self.bufferPoolCapacity
            )
        else {
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
            discardedFrames: discardedFrames
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
            var accumulator = Float32FrameAccumulator()
            var adaptiveGain = AdaptiveMicrophoneGain(isEnabled: automaticGainEnabled)
            var emittedFrameCount = 0

            // 4,800 bytes = 1 packet なので、AGC 済み PCM16 をそのまま packetizer へ流す。
            func emit(_ pcm16: Data) async -> Bool {
                for frame in packetizer.append(pcm16) {
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
                        return false
                    }
                }
                return true
            }

            do {
                for await captured in captureStream {
                    defer { captured.release() }
                    try Task.checkCancellation()
                    let converted = try converter.convert(captured.buffer)
                    let pcm16Frames = try Self.encodePCM16Frames(
                        from: converted,
                        accumulator: &accumulator,
                        adaptiveGain: &adaptiveGain
                    )
                    for pcm16 in pcm16Frames {
                        guard await emit(pcm16) else { return }
                    }
                }
                // 停止時は端数を無音 padding した 1 フレームを同じ経路で処理する。
                if let pending = accumulator.flushWithSilencePadding() {
                    let pcm16 = pending.withUnsafeBufferPointer { buffer in
                        adaptiveGain.process(
                            floatSamples: buffer.baseAddress!,
                            frameCount: buffer.count
                        )
                    }
                    guard await emit(pcm16) else { return }
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
        let sampleRate = inputSampleRate
        let discarded = discardedFrames.withLock { frames in
            Self.discardedMilliseconds(forFrames: frames, inputSampleRate: sampleRate)
        }
        return frameQueue.enqueue(
            pcm16: frame,
            generation: generation,
            discardedMilliseconds: discarded,
            capturedAt: .now
        )
    }

    nonisolated static func discardedMilliseconds(forFrames frames: Int, inputSampleRate: Double) -> Int {
        guard frames > 0, inputSampleRate > 0 else { return 0 }
        return Int((Double(frames) * 1_000 / inputSampleRate).rounded())
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

    /// 変換後の float バッファを 100ms フレームへ積み上げ、完成した各フレームを
    /// 適応ゲイン + ランプ付きで PCM16 LE 化して返す。
    nonisolated private static func encodePCM16Frames(
        from buffer: AVAudioPCMBuffer,
        accumulator: inout Float32FrameAccumulator,
        adaptiveGain: inout AdaptiveMicrophoneGain
    ) throws -> [Data] {
        let frameLength = Int(buffer.frameLength)
        guard frameLength > 0 else { return [] }

        guard !buffer.format.isInterleaved, let channel = buffer.floatChannelData?[0] else {
            throw RealtimeAudioCaptureError.audioFormatUnavailable
        }

        let emitted = accumulator.append(
            UnsafeBufferPointer(start: channel, count: frameLength)
        )
        return emitted.map { frame in
            frame.withUnsafeBufferPointer { buffer in
                adaptiveGain.process(
                    floatSamples: buffer.baseAddress!,
                    frameCount: buffer.count
                )
            }
        }
    }

    nonisolated private static func peakAmplitude(inPCM16LE data: Data) -> Int {
        data.withUnsafeBytes { rawBuffer in
            rawBuffer.bindMemory(to: Int16.self).reduce(into: 0) { peak, sample in
                peak = max(peak, abs(Int(sample)))
            }
        }
    }
}
