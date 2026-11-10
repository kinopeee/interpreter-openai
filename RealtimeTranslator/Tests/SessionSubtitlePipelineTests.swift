import XCTest
@testable import RealtimeTranslator

@MainActor
final class SessionSubtitlePipelineTests: XCTestCase {
    func testForcedFlushInvalidatesRenderedPairAfterPendingZeroPrefixSourceAdvance() throws {
        // Given: 訳文より後に届いた最初の原文を表示し、次の原文更新を描画待ちにする
        var snapshots: [SubtitleSnapshot] = []
        let (pipeline, aggregator, _) = makePipeline { snapshots.append($0) }
        let eventTime = Date().addingTimeInterval(-10)
        pipeline.processSubtitleEvent(
            event(
                .english,
                .outputTranscriptDelta(delta: "Hello", eventID: "flush-zero-prefix-t1", elapsedMs: 10)
            ),
            now: eventTime
        )
        let firstSource = try XCTUnwrap(
            pipeline.processSubtitleEvent(
                event(
                    .english,
                    .inputTranscriptDelta(delta: "こんにちは", eventID: "flush-zero-prefix-s1", elapsedMs: 20)
                ),
                now: eventTime.addingTimeInterval(0.1)
            )?.ingestedUpdate
        )
        pipeline.renderNow(firstSource)
        XCTAssertEqual(aggregator.snapshot().current.sourceText, "こんにちは")
        XCTAssertEqual(aggregator.snapshot().current.translatedText, "Hello")
        XCTAssertTrue(aggregator.snapshot().current.isTranslationCurrent)

        let secondSource = try XCTUnwrap(
            pipeline.processSubtitleEvent(
                event(
                    .english,
                    .inputTranscriptDelta(delta: "、皆さん", eventID: "flush-zero-prefix-s2", elapsedMs: 30)
                ),
                now: eventTime.addingTimeInterval(0.2)
            )?.ingestedUpdate
        )
        pipeline.enqueueRender(secondSource)
        pipeline.clearBoundaryCandidate()

        // When: 強制flushが描画待ちを破棄して空prefixをidle判定する
        pipeline.flushPendingFinalizeIfNeeded()

        // Then: 旧pairを確定せず、表示を無効化して消す
        XCTAssertTrue(aggregator.snapshot().current.isEmpty)
        XCTAssertFalse(snapshots.contains { $0.current.state == .finalized })
        XCTAssertTrue(snapshots.last?.isInvalidation == true)
    }

    func testOrdinaryIdleTickKeepsDisplayAndIgnoresLateTranslationUntilNextSource() throws {
        // Given: 訳文より後に届いた原文suffixが描画待ちで、表示には先のpairが残る
        var snapshots: [SubtitleSnapshot] = []
        let (pipeline, aggregator, _) = makePipeline { snapshots.append($0) }
        let eventTime = Date().addingTimeInterval(-10)
        pipeline.processSubtitleEvent(
            event(
                .english,
                .outputTranscriptDelta(delta: "Hello", eventID: "tick-zero-prefix-t1", elapsedMs: 10)
            ),
            now: eventTime
        )
        let firstSource = try XCTUnwrap(
            pipeline.processSubtitleEvent(
                event(
                    .english,
                    .inputTranscriptDelta(delta: "こんにちは", eventID: "tick-zero-prefix-s1", elapsedMs: 20)
                ),
                now: eventTime.addingTimeInterval(0.1)
            )?.ingestedUpdate
        )
        pipeline.renderNow(firstSource)
        let displayedPair = aggregator.snapshot()
        let secondSource = try XCTUnwrap(
            pipeline.processSubtitleEvent(
                event(
                    .english,
                    .inputTranscriptDelta(delta: "、皆さん", eventID: "tick-zero-prefix-s2", elapsedMs: 30)
                ),
                now: eventTime.addingTimeInterval(0.2)
            )?.ingestedUpdate
        )
        pipeline.enqueueRender(secondSource)

        // When: 通常idle tickを行い、遅れてきた訳文を受け取る
        let tick = pipeline.tickProcessor(
            now: eventTime.addingTimeInterval(RealtimeSubtitleAssembler.idleFinalizeInterval + 0.3)
        )
        let lateTranslation = pipeline.processSubtitleEvent(
            event(
                .english,
                .outputTranscriptDelta(
                    delta: " everyone",
                    eventID: "tick-zero-prefix-late-t2",
                    elapsedMs: 40
                )
            ),
            now: eventTime.addingTimeInterval(RealtimeSubtitleAssembler.idleFinalizeInterval + 0.4)
        )

        // Then: tickはnilのまま表示を変えず、次の原文まで遅延訳文を無視する
        XCTAssertNil(tick)
        XCTAssertNil(lateTranslation)
        XCTAssertEqual(aggregator.snapshot(), displayedPair)
        XCTAssertEqual(snapshots.count, 1)

        let nextSource = try XCTUnwrap(
            pipeline.processSubtitleEvent(
                event(
                    .english,
                    .inputTranscriptDelta(delta: "ありがとう", eventID: "tick-zero-prefix-s3", elapsedMs: 50)
                ),
                now: eventTime.addingTimeInterval(RealtimeSubtitleAssembler.idleFinalizeInterval + 0.5)
            )?.ingestedUpdate
        )
        pipeline.discardPending()
        pipeline.renderNow(nextSource)
        XCTAssertEqual(aggregator.snapshot().current.sourceText, "ありがとう")
        XCTAssertEqual(aggregator.snapshot().current.translatedText, "")
    }

    func testForcedFlushKeepsAggregatorFallbackWhenProcessorIsEmpty() {
        // Given: processorは空で、aggregatorだけに完全なcurrent pairがある
        var snapshots: [SubtitleSnapshot] = []
        let (pipeline, aggregator, _) = makePipeline { snapshots.append($0) }
        aggregator.replaceCurrent(
            sourceText: "手動で設定した原文",
            translatedText: "Seeded translation",
            isTranslationCurrent: true,
            canFinalize: false
        )

        // When: 強制flushを行う
        pipeline.flushPendingFinalizeIfNeeded()

        // Then: 既存のaggregator fallbackで完全pairを確定する
        XCTAssertEqual(snapshots.last?.current.state, .finalized)
        XCTAssertEqual(snapshots.last?.current.sourceText, "手動で設定した原文")
        XCTAssertEqual(snapshots.last?.current.translatedText, "Seeded translation")
    }

    private func makePipeline(
        publishUpdate: @escaping @MainActor (SubtitleSnapshot) -> Void
    ) -> (SessionSubtitlePipeline, SubtitleAggregator, SubtitleDisplayScheduler) {
        let aggregator = SubtitleAggregator()
        let displayScheduler = SubtitleDisplayScheduler(renderIntervalNanoseconds: 60_000_000_000)
        let healthBookkeeper = SessionHealthBookkeeper(
            thresholds: SessionHealthThresholds(),
            healthNow: ReconnectBudget.continuousNow,
            wallClockNow: { Date().timeIntervalSince1970 },
            reservedEpochProvider: { 1 },
            diagnosticSink: { _ in }
        )
        let pipeline = SessionSubtitlePipeline(
            aggregator: aggregator,
            displayScheduler: displayScheduler,
            postStopSubtitleRetentionNanoseconds: 0,
            healthBookkeeper: healthBookkeeper,
            dualClient: FakeDualRealtimeTranslationClient()
        )
        pipeline.bind(
            activeFeed: { nil },
            lifecycleGeneration: { 0 },
            state: { .listening },
            publishUpdate: publishUpdate
        )
        pipeline.beginEpoch(1, pair: .jaEn)
        return (pipeline, aggregator, displayScheduler)
    }

    private func event(
        _ target: RealtimeTranslationOutputLanguage,
        _ serverEvent: RealtimeTranslationServerEvent
    ) -> RealtimeTranslationStreamEvent {
        let lane: RealtimeTranslationLane
        if case .inputTranscriptDelta = serverEvent {
            lane = .source
        } else {
            lane = .translation(target)
        }
        return RealtimeTranslationStreamEvent(lane: lane, event: serverEvent, epoch: 1)
    }
}
