import Foundation
import XCTest

final class SharedFixturesTests: XCTestCase {
    func testOptionalNumberAcceptsIntegralValues() {
        // Given: 整数型と整数値の浮動小数点型
        let values: [Any] = [2_400, NSNumber(value: 4_800.0), NSNumber(value: UInt32.max)]

        // When: fixture の整数として読み取る
        let actual = values.map { SharedFixtures.optionalNumber($0) }

        // Then: 値を変えずに Int へ変換できる
        XCTAssertEqual(actual, [2_400, 4_800, Int(UInt32.max)])
    }

    func testOptionalNumberRejectsFractionalAndOutOfRangeValues() {
        // Given: 小数値と Int の範囲外の値
        let fractional = NSNumber(value: 4_800.9)
        let outOfRange = NSNumber(value: UInt64.max)

        // When: fixture の整数として読み取る
        let fractionalResult = SharedFixtures.optionalNumber(fractional)
        let outOfRangeResult = SharedFixtures.optionalNumber(outOfRange)

        // Then: 切り捨てやオーバーフローをせず拒否する
        XCTAssertNil(fractionalResult)
        XCTAssertNil(outOfRangeResult)
    }
}
