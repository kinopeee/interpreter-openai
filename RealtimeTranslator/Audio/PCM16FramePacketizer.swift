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

/// 24 kHz mono Float32 を100 ms（2,400 samples）単位へ分割する。
/// 適応ゲインをフレーム単位で決めるため、PCM16変換の前に使う。feederから直列に呼ぶ。
struct Float32FrameAccumulator: Sendable {
    static let samplesPerFrame = PCM16FramePacketizer.samplesPerFrame

    private var pending: [Float] = []

    var pendingSampleCount: Int { pending.count }

    init() {
        pending.reserveCapacity(Self.samplesPerFrame)
    }

    mutating func append(_ samples: UnsafeBufferPointer<Float>) -> [[Float]] {
        var frames: [[Float]] = []
        var offset = 0
        while offset < samples.count {
            let take = min(Self.samplesPerFrame - pending.count, samples.count - offset)
            pending.append(contentsOf: samples[offset..<(offset + take)])
            offset += take
            if pending.count == Self.samplesPerFrame {
                frames.append(pending)
                pending.removeAll(keepingCapacity: true)
            }
        }
        return frames
    }

    /// 正常停止時に端数を無音paddingして最後の1frameを返す。端数が無ければnil。
    mutating func flushWithSilencePadding() -> [Float]? {
        guard !pending.isEmpty else { return nil }
        var frame = pending
        pending.removeAll(keepingCapacity: true)
        frame.append(contentsOf: repeatElement(0, count: Self.samplesPerFrame - frame.count))
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
        data.withUnsafeMutableBytes { rawBuffer in
            let output = rawBuffer.bindMemory(to: Int16.self)
            for index in 0..<frameCount {
                output[index] = encodeSample(floatSamples[index], gain: gain)
            }
        }
        return data
    }

    /// 先頭 `rampSamples` の間は `fromGain` から `toGain` へ線形に移し、残りは `toGain` で変換する。
    static func encode(
        floatSamples: UnsafeBufferPointer<Float>,
        fromGain: Float,
        toGain: Float,
        rampSamples: Int
    ) -> Data {
        precondition(rampSamples > 0)
        var data = Data(count: floatSamples.count * 2)
        data.withUnsafeMutableBytes { rawBuffer in
            let output = rawBuffer.bindMemory(to: Int16.self)
            for index in 0..<floatSamples.count {
                let gain = rampGain(from: fromGain, to: toGain, index: index, rampSamples: rampSamples)
                output[index] = encodeSample(floatSamples[index], gain: gain)
            }
        }
        return data
    }

    /// サンプル `index` に掛けるランプ中のゲイン。
    static func rampGain(from fromGain: Float, to toGain: Float, index: Int, rampSamples: Int) -> Float {
        guard index < rampSamples else { return toGain }
        return fromGain + (toGain - fromGain) * (Float(index + 1) / Float(rampSamples))
    }

    /// クリップしてから Int16.max 倍し、0 から遠い側へ四捨五入する。
    static func encodeSample(_ sample: Float, gain: Float) -> Int16 {
        if sample.isNaN {
            return 0
        }
        let safeGain = gain.isFinite ? gain : 1
        let amplified = sample * safeGain
        if amplified.isNaN {
            // 例: Infinity * 0。trap を避けて無音にする。
            return 0
        }
        let clipped = max(-1.0 as Float, min(1.0 as Float, amplified))
        let scaled = clipped * Float(Int16.max)
        return Int16(scaled.rounded())
    }
}
