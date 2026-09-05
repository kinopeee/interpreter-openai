import XCTest
@testable import RealtimeTranslator

final class SourceBoundaryTrackerTests: XCTestCase {
    // Given: v2 boundary fixture の pair / current language / delta
    // When: routing window と detector / selector を順に適用する
    // Then: tracker の UTF-16 候補 offset と split source が fixture と一致する
    func testBoundaryFixtureHarness() throws {
        let root = try SharedFixtures.load("subtitle", version: 2)
        let boundary = try XCTUnwrap(root["boundary"] as? [String: Any])
        let cases = try XCTUnwrap(boundary["cases"] as? [Any])

        for item in cases {
            let fixture = try XCTUnwrap(item as? [String: Any])
            let pair = try XCTUnwrap(LanguagePair(rawValue: SharedFixtures.text(fixture["pair"])))
            let currentLanguage = language(SharedFixtures.text(fixture["currentLanguage"]))
            let currentTarget = try XCTUnwrap(pair.translationTarget(for: currentLanguage))
            var reverseEvidenceCount = 0
            var routing = ""
            var source = ""
            var tracker = SourceBoundaryTracker()
            var candidates: [Int?] = []
            var switchDelta: Int?

            for (index, value) in try XCTUnwrap(fixture["deltas"] as? [Any]).enumerated() {
                let delta = SharedFixtures.text(value)
                let deltaStart = source.utf16.count
                source += delta
                routing = RoutingSourceTextWindow.trim(routing + delta, pair: pair)
                let evidence = SpokenLanguageDetector.recentEvidence(in: routing, pair: pair)
                let selection = TranslationTargetSelector.select(
                    pair: pair,
                    currentTarget: currentTarget,
                    reverseEvidenceCount: reverseEvidenceCount,
                    evidence: evidence
                )
                reverseEvidenceCount = selection.reverseEvidenceCount

                if selection.target == currentTarget {
                    tracker.observe(
                        segmentSource: source,
                        deltaStart: deltaStart,
                        segmentGeneration: 0,
                        pair: pair,
                        currentLanguage: currentLanguage,
                        reverseEvidenceCount: reverseEvidenceCount
                    )
                    candidates.append(tracker.candidateOffset)
                } else {
                    if pair != .enEs {
                        tracker.observe(
                            segmentSource: source,
                            deltaStart: deltaStart,
                            segmentGeneration: 0,
                            pair: pair,
                            currentLanguage: currentLanguage,
                            reverseEvidenceCount: 0
                        )
                    }
                    candidates.append(tracker.candidateOffset ?? deltaStart)
                    switchDelta = index
                    break
                }
            }

            let expectedCandidates = try XCTUnwrap(fixture["expectedCandidateOffsets"] as? [Any])
                .map(SharedFixtures.optionalNumber)
            XCTAssertEqual(candidates, expectedCandidates, SharedFixtures.text(fixture["name"]))
            XCTAssertEqual(
                switchDelta,
                SharedFixtures.optionalNumber(fixture["expectedSwitchAtDelta"]),
                SharedFixtures.text(fixture["name"])
            )

            if let offset = switchDelta {
                let expectedOld = SharedFixtures.optionalText(fixture["expectedOldSource"])
                let expectedNew = SharedFixtures.optionalText(fixture["expectedNewSource"])
                let splitOffset = candidates[offset] ?? source.utf16.count
                let index = String.Index(utf16Offset: splitOffset, in: source)
                XCTAssertEqual(String(source[..<index]), expectedOld)
                XCTAssertEqual(String(source[index...]), expectedNew)
            }
        }
    }

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

    private func language(_ value: String) -> SpokenLanguage {
        switch value {
        case "ja": return .japanese
        case "en": return .english
        case "es": return .spanish
        default: return .unknown
        }
    }
}
