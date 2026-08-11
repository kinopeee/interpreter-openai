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
    ///
    /// NaN は無音 (0) にする。`Int16(Float.nan)` は Swift で trap するため、
    /// AGC が NaN をスキップした同一バッファをここで再走査しても録音経路を落とさない。
    /// ±Infinity は従来どおり ±1 へクリップする。
    static func encode(
        floatSamples: UnsafePointer<Float>,
        frameCount: Int,
        gain: Float = 1
    ) -> Data {
        var data = Data(count: frameCount * 2)
        let safeGain = gain.isFinite ? gain : 1
        data.withUnsafeMutableBytes { rawBuffer in
            let output = rawBuffer.bindMemory(to: Int16.self)
            for index in 0..<frameCount {
                let sample = floatSamples[index]
                if sample.isNaN {
                    output[index] = 0
                    continue
                }
                let amplified = sample * safeGain
                if amplified.isNaN {
                    // 例: Infinity * 0。trap を避けて無音にする。
                    output[index] = 0
                    continue
                }
                let clipped = max(-1.0 as Float, min(1.0 as Float, amplified))
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
