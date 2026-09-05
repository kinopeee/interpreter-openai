import XCTest
@testable import RealtimeTranslator

final class SubtitleFixtureTests: XCTestCase {
    private static let origin = Date(timeIntervalSince1970: 1_767_225_600) // 2026-01-01T00:00:00Z

    // Given: shared fixture の字幕文字数上限
    // When: clipper の定数と照合する
    // Then: 日本語 60 / 英語 120 / 省略記号が一致する
    func testLimitsMatchFixture() throws {
        let limits = try XCTUnwrap(
            try SharedFixtures.load("subtitle", version: 2)["limits"] as? [String: Any]
        )
        XCTAssertEqual(
            SharedFixtures.number(limits["japaneseCharacterLimit"]),
            SubtitleTailClipper.japaneseCharacterLimit
        )
        XCTAssertEqual(
            SharedFixtures.number(limits["englishCharacterLimit"]),
            SubtitleTailClipper.englishCharacterLimit
        )
        XCTAssertEqual(
            SharedFixtures.text(limits["ellipsis"]),
            SubtitleTailClipper.ellipsis
        )
    }

    // Given: fixture の長文・短文・空白のみの字幕候補
    // When: 末尾優先でクリップする
    // Then: 期待する表示文字列になる
    func testClipMatchesFixture() throws {
        for name in try SharedFixtures.caseNames("subtitle", "clip", version: 2) {
            let fixture = try SharedFixtures.case("subtitle", "clip", name, version: 2)
            let input = SharedFixtures.fixtureString(try XCTUnwrap(fixture["input"]))
            let expected = SharedFixtures.fixtureString(try XCTUnwrap(fixture["expected"]))
            XCTAssertEqual(SubtitleTailClipper.clip(input), expected)
        }
    }

    // Given: shared fixture の無採取 finalize 間隔
    // When: assembler の定数と照合する
    // Then: 8 秒の idle finalize 間隔が一致する
    func testIdleIntervalMatchesFixture() throws {
        let assembler = try XCTUnwrap(
            try SharedFixtures.load("subtitle", version: 2)["assembler"] as? [String: Any]
        )
        XCTAssertEqual(
            TimeInterval(SharedFixtures.number(assembler["idleFinalizeSeconds"])),
            RealtimeSubtitleAssembler.idleFinalizeInterval
        )
    }

    // Given: fixture の原文・翻訳 delta シナリオ（epoch / 重複 ID / lane 期待値を含む）
    // When: assembler へ順に投入し時間を進める
    // Then: finalize タイミングと字幕内容が期待どおりになる
    func testAssemblerMatchesFixture() throws {
        let assemblerRoot = try XCTUnwrap(
            try SharedFixtures.load("subtitle", version: 2)["assembler"] as? [String: Any]
        )
        let cases = try XCTUnwrap(assemblerRoot["cases"] as? [Any])
        for caseItem in cases {
            let fixture = try XCTUnwrap(caseItem as? [String: Any])
            let name = SharedFixtures.text(fixture["name"])
                        let epoch = SharedFixtures.number(fixture["epoch"])
            var assembler = RealtimeSubtitleAssembler()
            assembler.reset(epoch: epoch)
            if let lane = SharedFixtures.optionalText(fixture["expectLane"]) {
                assembler.expectLane(
                    RealtimeTranslationOutputLanguage(rawValue: lane)
                )
            } else {
                assembler.expectLane(nil)
            }

            var last: RealtimeSubtitleUpdate?
            var finalizedPairs: [(String, String)] = []
            let steps = try XCTUnwrap(fixture["steps"] as? [Any])
            for stepItem in steps {
                let step = try XCTUnwrap(stepItem as? [String: Any])
                let now = Self.origin.addingTimeInterval(SharedFixtures.real(step["at"]))
                let kind = SharedFixtures.text(step["kind"])
                let update: RealtimeSubtitleUpdate?
                switch kind {
                case "tick":
                    update = assembler.tick(now: now)
                case "languageSwitch":
                    let split = assembler.splitForLanguageSwitch(
                        at: SharedFixtures.number(step["boundaryOffset"]),
                        now: now
                    )
                    if let finalized = split.finalized {
                        finalizedPairs.append((finalized.sourceText, finalized.translatedText))
                    }
                    update = split.current
                case "sourceDelta", "translationDelta":
                    let lane = SharedFixtures.text(step["lane"])
                    let eventLane: RealtimeTranslationLane = kind == "sourceDelta"
                        ? .source
                        : .translation(
                            try XCTUnwrap(RealtimeTranslationOutputLanguage(rawValue: lane))
                        )
                    update = assembler.ingest(
                        RealtimeTranslationStreamEvent(
                            lane: eventLane,
                            event: serverEvent(kind: kind, step: step),
                            epoch: SharedFixtures.optionalNumber(step["epoch"]) ?? epoch
                        ),
                        now: now
                    )
                default:
                    return XCTFail("unhandled step kind \(kind)")
                }
                if let update {
                    if update.shouldFinalize {
                        finalizedPairs.append((update.sourceText, update.translatedText))
                    }
                    last = update
                }
            }

            let expectedPairs = try XCTUnwrap(fixture["expectedFinalizedPairs"] as? [Any])
            XCTAssertEqual(finalizedPairs.count, expectedPairs.count)
            for (actual, expectedItem) in zip(finalizedPairs, expectedPairs) {
                let expectedPair = try XCTUnwrap(expectedItem as? [String: Any])
                XCTAssertEqual(actual.0, SharedFixtures.text(expectedPair["sourceText"]))
                XCTAssertEqual(actual.1, SharedFixtures.text(expectedPair["translatedText"]))
            }
            let expected = try XCTUnwrap(fixture["expectedFinal"] as? [String: Any])
            let finalUpdate = try XCTUnwrap(last)
            XCTAssertEqual(SharedFixtures.text(expected["sourceText"]), finalUpdate.sourceText)
            XCTAssertEqual(
                SharedFixtures.text(expected["translatedText"]),
                finalUpdate.translatedText
            )
            XCTAssertEqual(
                SharedFixtures.flag(expected["isTranslationCurrent"]),
                finalUpdate.isTranslationCurrent
            )
            XCTAssertEqual(
                SharedFixtures.flag(expected["shouldFinalize"]),
                finalUpdate.shouldFinalize
            )
        }
    }

    // Given: v2 boundary fixture の source delta
    // When: routing window / detector / selector / tracker を順に適用する
    // Then: candidate offset と source split が fixture と一致する
    func testBoundaryMatchesFixture() throws {
        let root = try SharedFixtures.load("subtitle", version: 2)
        let boundary = try XCTUnwrap(root["boundary"] as? [String: Any])
        let cases = try XCTUnwrap(boundary["cases"] as? [Any])

        for item in cases {
            let fixture = try XCTUnwrap(item as? [String: Any])
            let pair = try XCTUnwrap(LanguagePair(rawValue: SharedFixtures.text(fixture["pair"])))
            let currentLanguage = parseLanguage(SharedFixtures.text(fixture["currentLanguage"]))
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

            if let switchDelta {
                let splitOffset = candidates[switchDelta] ?? source.utf16.count
                let splitIndex = String.Index(utf16Offset: splitOffset, in: source)
                XCTAssertEqual(
                    String(source[..<splitIndex]),
                    SharedFixtures.optionalText(fixture["expectedOldSource"])
                )
                XCTAssertEqual(
                    String(source[splitIndex...]),
                    SharedFixtures.optionalText(fixture["expectedNewSource"])
                )
            }
        }
    }

    private func serverEvent(kind: String, step: [String: Any]) -> RealtimeTranslationServerEvent {
        let text = SharedFixtures.text(step["text"])
        let eventID = SharedFixtures.optionalText(step["eventId"])
        let elapsedMs = SharedFixtures.optionalNumber(step["elapsedMs"])
        if kind == "sourceDelta" {
            return .inputTranscriptDelta(delta: text, eventID: eventID, elapsedMs: elapsedMs)
        }
        return .outputTranscriptDelta(delta: text, eventID: eventID, elapsedMs: elapsedMs)
    }

    private func parseLanguage(_ value: String) -> SpokenLanguage {
        switch value {
        case "ja": return .japanese
        case "en": return .english
        case "es": return .spanish
        default: fatalError("unhandled language code \(value)")
        }
    }
}
