import XCTest
@testable import RealtimeTranslator

final class SourceBoundaryTrackerTests: XCTestCase {
    // Given: tracker が一つの generation を観測した
    // When: generation が変わった source を観測する
    // Then: 古い候補を捨てて新しい segment から再計算する
    func testGenerationMismatchResetsCandidate() {
        var tracker = SourceBoundaryTracker()
        tracker.observe(
            segmentSource: "あいうえお To",
            deltaStart: 0,
            segmentGeneration: 1,
            pair: .jaEn,
            currentLanguage: .japanese,
            reverseEvidenceCount: 0
        )
        XCTAssertEqual(tracker.candidateOffset, 5)
        tracker.observe(
            segmentSource: "日本語",
            deltaStart: 0,
            segmentGeneration: 2,
            pair: .jaEn,
            currentLanguage: .japanese,
            reverseEvidenceCount: 0
        )
        XCTAssertNil(tracker.candidateOffset)
    }

}
