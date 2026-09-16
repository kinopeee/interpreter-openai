import XCTest
@testable import RealtimeTranslator

/// PCM16 frame のピーク振幅（音声活動の閾値判定のみ、音声は保持しない）。
final class PCM16AudioActivityTests: XCTestCase {
    // Given: 無音の PCM16 frame
    // When: ピーク振幅を計算する
    // Then: 0 を返す
    func testSilenceFrameReturnsZero() {
        let frame = Data(repeating: 0, count: 4800)
        XCTAssertEqual(PCM16AudioActivity.normalizedPeakAmplitude(of: frame), 0)
    }

    // Given: 1 sample だけ 32767 を含む frame
    // When: ピーク振幅を計算する
    // Then: 1.0 を返す
    func testSingleMaxSampleReturnsOne() {
        var frame = Data(repeating: 0, count: 4800)
        frame[100] = 0xFF
        frame[101] = 0x7F
        XCTAssertEqual(PCM16AudioActivity.normalizedPeakAmplitude(of: frame), 1.0, accuracy: 1e-9)
    }

    // Given: Int16.min（-32768）だけを含む frame
    // When: ピーク振幅を計算する
    // Then: 飽和して 1.0 を返す
    func testInt16MinClampsToOne() {
        var frame = Data(repeating: 0, count: 4800)
        frame[0] = 0x00
        frame[1] = 0x80
        XCTAssertEqual(PCM16AudioActivity.normalizedPeakAmplitude(of: frame), 1.0, accuracy: 1e-9)
    }

    // Given: 活動閾値前後の振幅を持つ frame
    // When: ピーク振幅を閾値 0.005 と比較する
    // Then: 閾値超過のみが活動とみなされる
    func testActivityThresholdBoundary() {
        // 164/32767 ≈ 0.005005 → 活動
        var above = Data(repeating: 0, count: 4800)
        above[0] = 0xA4
        above[1] = 0x00
        XCTAssertGreaterThan(PCM16AudioActivity.normalizedPeakAmplitude(of: above), 0.005)

        // 10/32767 ≈ 0.0003 → 非活動
        var below = Data(repeating: 0, count: 4800)
        below[0] = 0x0A
        below[1] = 0x00
        XCTAssertLessThanOrEqual(PCM16AudioActivity.normalizedPeakAmplitude(of: below), 0.005)
    }

    // Given: 奇数バイト長の frame
    // When: ピーク振幅を計算する
    // Then: 末尾の半端バイトを無視して落ちない
    func testOddLengthFrameDoesNotCrash() {
        let frame = Data(repeating: 0x40, count: 99)
        _ = PCM16AudioActivity.normalizedPeakAmplitude(of: frame)
    }
}
