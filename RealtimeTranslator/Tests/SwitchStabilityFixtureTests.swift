import XCTest
@testable import RealtimeTranslator

final class SwitchStabilityFixtureTests: XCTestCase {
    // Given: 同じ原文を異なる delta 分割で表した switchStability fixture
    // When: processor と同じ routing window / tracker / selector の流れを各分割へ適用する
    // Then: 切替回数・最終 target・最初の split offset が分割に依存しない
    func testSwitchStabilityMatchesSharedFixture() throws {
        for fixture in try SharedFixtures.section("routing", "switchStability") {
            let pair = try XCTUnwrap(
                LanguagePair(rawValue: SharedFixtures.text(fixture["pair"]))
            )
            let initialTarget = try XCTUnwrap(
                RealtimeTranslationOutputLanguage(
                    rawValue: SharedFixtures.text(fixture["initialTarget"])
                )
            )
            let splits = try XCTUnwrap(fixture["splits"] as? [[String: Any]])
            var joinedText: String?
            var firstSplitOffset: Int?

            for split in splits {
                let deltas = try XCTUnwrap(split["deltas"] as? [String])
                let splitText = deltas.joined()
                if let joinedText {
                    XCTAssertEqual(splitText, joinedText, SharedFixtures.text(fixture["name"]))
                } else {
                    joinedText = splitText
                }

                var target = initialTarget
                var source = ""
                var routing = ""
                var tracker = SourceBoundaryTracker()
                var reverseEvidenceCount = 0
                var switchCount = 0
                var switchDelta: Int?
                var sourceLengthAtSwitch: Int?
                var splitOffset: Int?

                for (index, delta) in deltas.enumerated() {
                    let deltaStart = source.utf16.count
                    source += delta
                    routing = RoutingSourceTextWindow.trim(routing + delta, pair: pair)
                    let evidence = SpokenLanguageDetector.recentEvidence(
                        in: routing,
                        pair: pair
                    )
                    let currentLanguage = try XCTUnwrap(pair.counterpart(of: target))
                    tracker.observe(
                        segmentSource: source,
                        deltaStart: deltaStart,
                        segmentGeneration: 0,
                        pair: pair,
                        currentLanguage: currentLanguage,
                        reverseEvidenceCount: 0
                    )
                    let selection = TranslationTargetSelector.select(
                        pair: pair,
                        currentTarget: target,
                        reverseEvidenceCount: reverseEvidenceCount,
                        evidence: evidence,
                        oppositeRun: tracker.oppositeScriptRun(in: source)
                    )
                    reverseEvidenceCount = selection.reverseEvidenceCount

                    guard let nextTarget = selection.target, nextTarget != target else {
                        continue
                    }

                    let offset = tracker.candidateOffset ?? deltaStart
                    if switchCount == 0 {
                        switchDelta = index
                        sourceLengthAtSwitch = source.utf16.count
                        splitOffset = offset
                    }
                    switchCount += 1
                    let splitIndex = String.Index(utf16Offset: offset, in: source)
                    source = String(source[splitIndex...])
                    routing = RoutingSourceTextWindow.trim(source, pair: pair)
                    tracker.reset()
                    target = nextTarget
                    reverseEvidenceCount = 0
                }

                XCTAssertEqual(
                    switchCount,
                    SharedFixtures.number(fixture["expectedSwitchCount"]),
                    SharedFixtures.text(fixture["name"])
                )
                XCTAssertEqual(
                    target.rawValue,
                    SharedFixtures.text(fixture["expectedFinalTarget"]),
                    SharedFixtures.text(fixture["name"])
                )
                XCTAssertEqual(
                    switchDelta,
                    SharedFixtures.optionalNumber(split["expectedSwitchAtDelta"]),
                    SharedFixtures.text(fixture["name"])
                )
                XCTAssertEqual(
                    sourceLengthAtSwitch,
                    SharedFixtures.optionalNumber(split["expectedSourceLengthAtSwitch"]),
                    SharedFixtures.text(fixture["name"])
                )
                if firstSplitOffset == nil {
                    firstSplitOffset = splitOffset
                } else {
                    XCTAssertEqual(splitOffset, firstSplitOffset, SharedFixtures.text(fixture["name"]))
                }
            }

            XCTAssertEqual(
                firstSplitOffset,
                SharedFixtures.optionalNumber(fixture["expectedSplitOffset"]),
                SharedFixtures.text(fixture["name"])
            )
        }
    }
}
