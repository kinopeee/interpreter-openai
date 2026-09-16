import Foundation

/// PCM16 little-endian mono frame のピーク振幅（0.0〜1.0）。
/// 音声活動の閾値判定だけに使い、音声データ自体は保持・記録しない。
enum PCM16AudioActivity {
    static func normalizedPeakAmplitude(of frame: Data) -> Double {
        frame.withUnsafeBytes { buffer in
            normalizedPeakAmplitude(of: buffer)
        }
    }

    static func normalizedPeakAmplitude(of buffer: UnsafeRawBufferPointer) -> Double {
        var peak = 0
        let sampleCount = buffer.count / 2
        for index in 0..<sampleCount {
            let value = buffer.load(
                fromByteOffset: index * 2,
                as: Int16.self
            )
            let magnitude = value == Int16.min ? 32768 : abs(Int(value))
            peak = max(peak, magnitude)
        }
        return min(Double(peak) / 32767.0, 1.0)
    }
}
