import Foundation

/// 24 kHz PCM16 mono little-endian を100 ms単位へ分割する。
struct PCM16FramePacketizer: Sendable {
    static let sampleRate = 24_000
    static let bytesPerSample = 2
    static let frameDurationMilliseconds = 100
    static let samplesPerFrame = sampleRate * frameDurationMilliseconds / 1_000
    static let bytesPerFrame = samplesPerFrame * bytesPerSample

    private var pending = Data()

    var pendingByteCount: Int { pending.count }

    mutating func append(_ pcm16LE: Data) -> [Data] {
        guard !pcm16LE.isEmpty else { return [] }
        pending.append(pcm16LE)
        var frames: [Data] = []
        while pending.count >= Self.bytesPerFrame {
            let frame = pending.prefix(Self.bytesPerFrame)
            frames.append(Data(frame))
            pending.removeFirst(Self.bytesPerFrame)
        }
        return frames
    }

    /// 正常停止時に端数を無音paddingして最後の1frameを返す。
    mutating func flushWithSilencePadding() -> Data? {
        guard !pending.isEmpty else { return nil }
        var frame = pending
        pending.removeAll(keepingCapacity: true)
        if frame.count < Self.bytesPerFrame {
            frame.append(Data(count: Self.bytesPerFrame - frame.count))
        } else if frame.count > Self.bytesPerFrame {
            frame = Data(frame.prefix(Self.bytesPerFrame))
        }
        return frame
    }

    mutating func reset() {
        pending.removeAll(keepingCapacity: true)
    }
}

enum PCM16LittleEndianEncoder {
    /// Float32 interleaved / non-interleaved mono buffer を PCM16 LE へ変換する。
    static func encode(
        floatSamples: UnsafePointer<Float>,
        frameCount: Int,
        gain: Float = 1
    ) -> Data {
        var data = Data(count: frameCount * 2)
        data.withUnsafeMutableBytes { rawBuffer in
            let output = rawBuffer.bindMemory(to: Int16.self)
            for index in 0..<frameCount {
                let amplified = floatSamples[index] * gain
                let clipped = max(-1.0, min(1.0, amplified))
                let scaled = clipped * Float(Int16.max)
                output[index] = Int16(scaled.rounded())
            }
        }
        return data
    }

    static func encode(int16Samples: UnsafePointer<Int16>, frameCount: Int) -> Data {
        Data(bytes: int16Samples, count: frameCount * MemoryLayout<Int16>.size)
    }
}
