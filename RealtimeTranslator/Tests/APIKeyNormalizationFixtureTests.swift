import XCTest
@testable import RealtimeTranslator

final class APIKeyNormalizationFixtureTests: XCTestCase {
    // Given: shared fixture の API キー正規化ケース
    // When: 実装の正規化を行う
    // Then: empty / malformed / valid と expected が一致する
    func testNormalizeMatchesFixture() throws {
        for name in try SharedFixtures.caseNames("api-key", "normalize") {
            let fixture = try SharedFixtures.case("api-key", "normalize", name)
            let input = SharedFixtures.text(fixture["input"])
            let status = SharedFixtures.text(fixture["status"])
            let result = APIKeyNormalization.normalize(input)

            switch status {
            case "empty":
                XCTAssertEqual(result, .empty, name)
            case "malformed":
                XCTAssertEqual(result, .malformed, name)
            case "valid":
                XCTAssertEqual(result, .valid(SharedFixtures.text(fixture["expected"])), name)
            default:
                XCTFail("unknown status \(status) in \(name)")
            }
        }
    }
}
