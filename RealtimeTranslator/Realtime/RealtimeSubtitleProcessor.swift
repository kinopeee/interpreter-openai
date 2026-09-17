import Foundation

enum RealtimeSubtitleRoutingAction: Equatable, Sendable {
    case none
    case select(RealtimeTranslationOutputLanguage?)
    case `switch`(RealtimeTranslationOutputLanguage?)
}

struct RealtimeSubtitleProcessingResult: Equatable, Sendable {
    var updates: [RealtimeSubtitleUpdate]
    var routingAction: RealtimeSubtitleRoutingAction
    var ingestedUpdate: RealtimeSubtitleUpdate
    var isSourceUpdate: Bool
}

struct RealtimeSubtitleProcessor: Sendable {
    private var assembler = RealtimeSubtitleAssembler()
    private(set) var activeLanguagePair: LanguagePair?
    private(set) var routingSourceText = ""
    private var selectedTranslationTarget: RealtimeTranslationOutputLanguage?
    private var reverseEvidenceCount = 0
    private var sourceBoundaryTracker = SourceBoundaryTracker()
    private var handledFailedSourceKeys = Set<String>()

    var currentSourceLength: Int { assembler.currentSourceLength }
    var currentSegmentGeneration: Int { assembler.currentSegmentGeneration }
    var isCurrentSegmentTainted: Bool { assembler.isCurrentSegmentTainted }
    var hasSelectedTranslationTarget: Bool { selectedTranslationTarget != nil }

    mutating func beginEpoch(_ epoch: Int, pair: LanguagePair) {
        assembler.beginNewEpoch(epoch)
        clearBoundaryCandidate()
        routingSourceText = ""
        activeLanguagePair = pair
        selectedTranslationTarget = nil
        reverseEvidenceCount = 0
        handledFailedSourceKeys.removeAll()
        assembler.setLanguagePair(pair)
    }

    mutating func deactivateLanguagePair() {
        activeLanguagePair = nil
    }

    mutating func clearBoundaryCandidate() {
        sourceBoundaryTracker.reset()
        assembler.setBoundaryCandidatePending(false)
    }

    mutating func resetRoutingForNextSegment() {
        routingSourceText = ""
        selectedTranslationTarget = nil
        reverseEvidenceCount = 0
        clearBoundaryCandidate()
        assembler.expectLane(nil)
    }

    mutating func discardUnconfirmed() -> RealtimeSubtitleUpdate {
        assembler.discardUnconfirmed()
        clearBoundaryCandidate()
        routingSourceText = ""
        selectedTranslationTarget = nil
        reverseEvidenceCount = 0
        return RealtimeSubtitleUpdate(
            sourceText: "",
            translatedText: "",
            isTranslationCurrent: false,
            shouldFinalize: false,
            segmentGeneration: assembler.currentSegmentGeneration,
            isInvalidation: true
        )
    }

    mutating func discardFailedSource(itemID: String?, eventID: String?) -> RealtimeSubtitleUpdate? {
        let key = itemID ?? eventID
        if let key, handledFailedSourceKeys.contains(key) {
            return nil
        }
        guard assembler.hasUnconfirmedContent else { return nil }
        if let key {
            handledFailedSourceKeys.insert(key)
        }
        return discardUnconfirmed()
    }

    mutating func markAudioLoss(now: Date) -> RealtimeSubtitleUpdate {
        assembler.markAudioLoss(now: now)
        clearBoundaryCandidate()
        routingSourceText = ""
        selectedTranslationTarget = nil
        reverseEvidenceCount = 0
        assembler.expectLane(nil)
        return RealtimeSubtitleUpdate(
            sourceText: "",
            translatedText: "",
            isTranslationCurrent: false,
            shouldFinalize: false,
            segmentGeneration: assembler.currentSegmentGeneration,
            isInvalidation: true
        )
    }

    mutating func tick(now: Date) -> RealtimeSubtitleUpdate? {
        assembler.tick(now: now)
    }

    mutating func process(
        _ streamEvent: RealtimeTranslationStreamEvent,
        now: Date,
        isReplay: Bool = false
    ) -> RealtimeSubtitleProcessingResult? {
        if case .inputTranscriptFailed = streamEvent.event {
            return nil
        }
        let deltaStart = assembler.currentSourceLength
        let sourceDelta: String?
        if case .inputTranscriptDelta(let delta, _, _) = streamEvent.event,
           streamEvent.lane.isSource {
            sourceDelta = delta
        } else {
            sourceDelta = nil
        }
        guard let update = assembler.ingest(streamEvent, now: now, isReplay: isReplay) else {
            return nil
        }
        let result = RealtimeSubtitleProcessingResult(
            updates: [update],
            routingAction: .none,
            ingestedUpdate: update,
            isSourceUpdate: sourceDelta != nil
        )
        guard let sourceDelta else { return result }
        return evaluateSourceRouting(delta: sourceDelta, deltaStart: deltaStart, now: now, result: result)
    }

    private mutating func evaluateSourceRouting(
        delta: String,
        deltaStart: Int,
        now: Date,
        result: RealtimeSubtitleProcessingResult
    ) -> RealtimeSubtitleProcessingResult {
        var result = result
        guard let pair = activeLanguagePair else { return result }

        routingSourceText = RoutingSourceTextWindow.trim(
            routingSourceText + delta,
            pair: pair
        )
        let evidence = SpokenLanguageDetector.recentEvidence(
            in: routingSourceText,
            pair: pair
        )
        var oppositeRun: OppositeScriptRun?
        if pair != .enEs,
           let currentTarget = selectedTranslationTarget,
           let currentLanguage = pair.counterpart(of: currentTarget)
        {
            sourceBoundaryTracker.observe(
                segmentSource: assembler.currentSourceText,
                deltaStart: deltaStart,
                segmentGeneration: assembler.currentSegmentGeneration,
                pair: pair,
                currentLanguage: currentLanguage,
                reverseEvidenceCount: 0
            )
            oppositeRun = sourceBoundaryTracker.oppositeScriptRun(
                in: assembler.currentSourceText
            )
        }
        let selection = TranslationTargetSelector.select(
            pair: pair,
            currentTarget: selectedTranslationTarget,
            reverseEvidenceCount: reverseEvidenceCount,
            evidence: evidence,
            oppositeRun: oppositeRun
        )
        reverseEvidenceCount = selection.reverseEvidenceCount

        guard selectedTranslationTarget != nil else {
            guard let target = selection.target else { return result }
            selectedTranslationTarget = target
            sourceBoundaryTracker.reset()
            assembler.setBoundaryCandidatePending(false)
            assembler.expectLane(target)
            result.routingAction = .select(target)
            return result
        }

        guard let currentTarget = selectedTranslationTarget else {
            return result
        }
        guard let target = selection.target, target != currentTarget else {
            if pair == .enEs, let currentLanguage = pair.counterpart(of: currentTarget) {
                sourceBoundaryTracker.observe(
                    segmentSource: assembler.currentSourceText,
                    deltaStart: deltaStart,
                    segmentGeneration: assembler.currentSegmentGeneration,
                    pair: pair,
                    currentLanguage: currentLanguage,
                    reverseEvidenceCount: selection.reverseEvidenceCount
                )
            }
            assembler.setBoundaryCandidatePending(
                sourceBoundaryTracker.candidateOffset != nil
            )
            return result
        }

        let offset = sourceBoundaryTracker.candidateOffset ?? deltaStart
        let split = assembler.splitForLanguageSwitch(at: offset, now: now)
        sourceBoundaryTracker.reset()
        routingSourceText = RoutingSourceTextWindow.trim(
            assembler.currentSourceText,
            pair: pair
        )
        selectedTranslationTarget = target
        reverseEvidenceCount = 0
        assembler.expectLane(target)
        result.updates = split.finalized.map { [$0, split.current] } ?? [split.current]
        result.routingAction = .switch(target)
        return result
    }
}
