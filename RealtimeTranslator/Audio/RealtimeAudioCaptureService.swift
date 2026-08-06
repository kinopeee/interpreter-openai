@preconcurrency import AVFoundation
import Foundation
import os

/// `AsyncStream` の yield 結果を録音継続可否へ写す。
///
/// `.bufferingNewest` は満杯時に古い要素を捨てて新規を enqueue したうえで
/// `.dropped(古い要素)` を返す。これを失敗扱いすると送信遅延で再接続嵐になる。
enum RealtimeAudioFrameYieldOutcome {
    static func didAccept(
        _ result: AsyncStream<Data>.Continuation.YieldResult
    ) -> Bool {
        switch result {
        case .enqueued, .dropped:
            return true
        case .terminated:
            return false
        @unknown default:
            return false
        }
    }
}

enum RealtimeAudioCaptureError: Error, LocalizedError, Sendable {
    case microphoneDenied
    case audioFormatUnavailable
    case audioConverterUnavailable
    case audioBufferPoolUnavailable
    case pipelineOverloaded

    var errorDescription: String? {
        switch self {
        case .microphoneDenied:
            return "マイクを利用できません"
        case .audioFormatUnavailable:
            return "マイク入力の音声形式を取得できません"
        case .audioConverterUnavailable:
            return "翻訳用の音声変換を開始できません"
        case .audioBufferPoolUnavailable:
            return "音声バッファを準備できません"
        case .pipelineOverloaded:
            return "音声処理が遅延しています"
        }
    }
}

@MainActor
protocol RealtimeAudioCaptureServicing: AnyObject {
    var frames: AsyncStream<Data> { get }
    func start() async throws
    func stop() async
}

@MainActor
final class RealtimeAudioCaptureService: RealtimeAudioCaptureServicing {
    private static let tapBufferSize: AVAudioFrameCount = 4096
    private static let bufferPoolCapacity = 64
    private static let targetSampleRate = 24_000.0

    private let audioEngine = AVAudioEngine()
    private var captureContinuation: AsyncStream<CapturedAudioBuffer>.Continuation?
    private var frameContinuation: AsyncStream<Data>.Continuation?
    private var feederTask: Task<Void, Never>?
    private var isTapInstalled = false
    private var lifecycleGeneration = 0
    private(set) var frames: AsyncStream<Data>

    init() {
        var continuation: AsyncStream<Data>.Continuation!
        frames = AsyncStream(bufferingPolicy: .bufferingNewest(32)) {
            continuation = $0
        }
        frameContinuation = continuation
    }

    func start() async throws {
        guard feederTask == nil else { return }
        lifecycleGeneration += 1
        let generation = lifecycleGeneration
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
        AppLogger.audio.notice(
            "DBG_CAPTURE_START rate=\(inputFormat.sampleRate, privacy: .public) channels=\(inputFormat.channelCount, privacy: .public)"
        )

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
            bufferPool: bufferPool
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
                        if emittedFrameCount == 1 || emittedFrameCount.isMultiple(of: 25) {
                            let peak = Self.peakAmplitude(inPCM16LE: frame)
                            AppLogger.audio.notice(
                                "DBG_CAPTURE_FRAME count=\(emittedFrameCount, privacy: .public) bytes=\(frame.count, privacy: .public) peak=\(peak, privacy: .public)"
                            )
                        }
                        let result = await self?.yieldFrame(frame)
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
                    _ = await self?.yieldFrame(padded)
                }
            } catch is CancellationError {
                return
            } catch {
                AppLogger.audio.error(
                    "Audio feeder failed: \(error.localizedDescription, privacy: .public)"
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
    }

    func stop() async {
        lifecycleGeneration += 1
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
        frameContinuation?.finish()
        frameContinuation = nil
    }

    private func yieldFrame(_ frame: Data) -> Bool {
        guard let frameContinuation else { return false }
        return RealtimeAudioFrameYieldOutcome.didAccept(frameContinuation.yield(frame))
    }

    private func reportFailure(_ error: Error, generation: Int) {
        guard generation == lifecycleGeneration else { return }
        AppLogger.audio.error(
            "Realtime audio capture failed: \(error.localizedDescription, privacy: .public)"
        )
        frameContinuation?.finish()
        frameContinuation = nil
    }

    private func recreateFrameStream() {
        frameContinuation?.finish()
        var continuation: AsyncStream<Data>.Continuation!
        frames = AsyncStream(bufferingPolicy: .bufferingNewest(32)) {
            continuation = $0
        }
        frameContinuation = continuation
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
