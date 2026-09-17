@preconcurrency import AVFoundation
import Foundation
import os

final class CapturedAudioBuffer: @unchecked Sendable {
    let buffer: AVAudioPCMBuffer
    fileprivate let index: Int

    private weak var pool: CapturedAudioBufferPool?
    private let isLeased = OSAllocatedUnfairLock(initialState: false)

    fileprivate init(
        buffer: AVAudioPCMBuffer,
        index: Int,
        pool: CapturedAudioBufferPool
    ) {
        self.buffer = buffer
        self.index = index
        self.pool = pool
    }

    fileprivate func markLeased() {
        isLeased.withLock { leased in
            precondition(!leased, "Audio buffer leased twice")
            leased = true
        }
    }

    func release() {
        let shouldRecycle = isLeased.withLock { leased in
            guard leased else { return false }
            leased = false
            return true
        }
        if shouldRecycle {
            pool?.recycle(index: index)
        }
    }
}

final class CapturedAudioBufferPool: @unchecked Sendable {
    private let format: AVAudioFormat
    private let frameCapacity: AVAudioFrameCount
    private let availableIndices: OSAllocatedUnfairLock<[Int]>
    private var buffers: [CapturedAudioBuffer] = []

    init?(
        format: AVAudioFormat,
        frameCapacity: AVAudioFrameCount,
        capacity: Int
    ) {
        guard frameCapacity > 0, capacity > 0 else { return nil }
        self.format = format
        self.frameCapacity = frameCapacity
        var indices = Array(0..<capacity)
        indices.reserveCapacity(capacity)
        availableIndices = OSAllocatedUnfairLock(initialState: indices)
        buffers.reserveCapacity(capacity)

        for index in 0..<capacity {
            guard let buffer = AVAudioPCMBuffer(
                pcmFormat: format,
                frameCapacity: frameCapacity
            ) else {
                return nil
            }
            buffers.append(
                CapturedAudioBuffer(buffer: buffer, index: index, pool: self)
            )
        }
    }

    var availableCount: Int {
        availableIndices.withLock { $0.count }
    }

    func capture(_ source: AVAudioPCMBuffer) -> CapturedAudioBuffer? {
        guard source.frameLength <= frameCapacity,
              formatsMatch(source.format, format),
              let index = availableIndices.withLock({ $0.popLast() })
        else {
            return nil
        }

        let captured = buffers[index]
        guard copySamples(from: source, to: captured.buffer) else {
            recycle(index: index)
            return nil
        }
        captured.markLeased()
        return captured
    }

    fileprivate func recycle(index: Int) {
        buffers[index].buffer.frameLength = 0
        availableIndices.withLock { indices in
            indices.append(index)
        }
    }

    private func formatsMatch(_ first: AVAudioFormat, _ second: AVAudioFormat) -> Bool {
        first.commonFormat == second.commonFormat
            && first.sampleRate == second.sampleRate
            && first.channelCount == second.channelCount
            && first.isInterleaved == second.isInterleaved
    }

    private func copySamples(
        from source: AVAudioPCMBuffer,
        to destination: AVAudioPCMBuffer
    ) -> Bool {
        destination.frameLength = source.frameLength
        let sourceBuffers = UnsafeMutableAudioBufferListPointer(
            UnsafeMutablePointer(mutating: source.audioBufferList)
        )
        let destinationBuffers = UnsafeMutableAudioBufferListPointer(
            destination.mutableAudioBufferList
        )
        guard sourceBuffers.count == destinationBuffers.count else { return false }

        for index in sourceBuffers.indices {
            let sourceBuffer = sourceBuffers[index]
            let sourceByteCount = Int(sourceBuffer.mDataByteSize)
            guard sourceByteCount <= Int(destinationBuffers[index].mDataByteSize)
            else {
                return false
            }
            if sourceByteCount > 0 {
                guard let sourceData = sourceBuffer.mData,
                      let destinationData = destinationBuffers[index].mData
                else {
                    return false
                }
                memcpy(destinationData, sourceData, sourceByteCount)
            }
            destinationBuffers[index].mDataByteSize = sourceBuffer.mDataByteSize
        }
        return true
    }
}

private final class AnalyzerInputProvider: @unchecked Sendable {
    private var buffer: AVAudioPCMBuffer?

    init(buffer: AVAudioPCMBuffer) {
        self.buffer = buffer
    }

    func next() -> AVAudioPCMBuffer? {
        defer { buffer = nil }
        return buffer
    }
}

final class AnalyzerAudioConverter: @unchecked Sendable {
    private let converter: AVAudioConverter
    private let outputFormat: AVAudioFormat
    private let lock = NSLock()

    init?(inputFormat: AVAudioFormat, outputFormat: AVAudioFormat) {
        guard let converter = AVAudioConverter(from: inputFormat, to: outputFormat) else {
            return nil
        }
        self.converter = converter
        self.outputFormat = outputFormat
    }

    func convert(_ input: AVAudioPCMBuffer) throws -> AVAudioPCMBuffer {
        lock.lock()
        defer { lock.unlock() }

        let ratio = outputFormat.sampleRate / input.format.sampleRate
        let capacity = AVAudioFrameCount(Double(input.frameLength) * ratio) + 32
        guard let output = AVAudioPCMBuffer(pcmFormat: outputFormat, frameCapacity: capacity) else {
            throw RealtimeAudioCaptureError.audioConverterUnavailable
        }

        let provider = AnalyzerInputProvider(buffer: input)
        var conversionError: NSError?
        let status = converter.convert(to: output, error: &conversionError) { _, outStatus in
            if let buffer = provider.next() {
                outStatus.pointee = .haveData
                return buffer
            }
            outStatus.pointee = .noDataNow
            return nil
        }

        if let conversionError {
            throw conversionError
        }
        guard status != .error else {
            throw RealtimeAudioCaptureError.audioConverterUnavailable
        }
        return output
    }
}

final class AnalyzerAudioTap: @unchecked Sendable {
    private let continuation: AsyncStream<CapturedAudioBuffer>.Continuation
    private let bufferPool: CapturedAudioBufferPool
    private let discardedFrames: OSAllocatedUnfairLock<Int>

    init(
        continuation: AsyncStream<CapturedAudioBuffer>.Continuation,
        bufferPool: CapturedAudioBufferPool,
        discardedFrames: OSAllocatedUnfairLock<Int>
    ) {
        self.continuation = continuation
        self.bufferPool = bufferPool
        self.discardedFrames = discardedFrames
    }

    func receive(_ buffer: AVAudioPCMBuffer) {
        guard let captured = bufferPool.capture(buffer) else { return }
        switch continuation.yield(captured) {
        case .enqueued:
            break
        case .dropped(let dropped):
            let frameCount = Int(dropped.buffer.frameLength)
            discardedFrames.withLock { $0 += frameCount }
            #if DEBUG
            AppLogger.audio.notice(
                "DBG_CAPTURE_PRECONVERSION_DROP frames=\(frameCount, privacy: .public)"
            )
            #endif
            dropped.release()
        case .terminated:
            captured.release()
        @unknown default:
            captured.release()
        }
    }
}
