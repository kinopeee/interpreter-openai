import Foundation

struct RealtimeSubtitleUpdate: Equatable, Sendable {
    var sourceText: String
    var translatedText: String
    var isTranslationCurrent: Bool
    var shouldFinalize: Bool
    var segmentGeneration: Int
}

/// 原文authorityとペア内の2出力を時間整列し、自動lane選択する。
struct RealtimeSubtitleAssembler: Sendable {
    // Realtime Translation can pause output deltas for 5 seconds or more while
    // continuing the same sentence. A short idle cutoff truncates the translation.
    /// shared/fixtures の assembler.idleFinalizeSeconds と一致させる。
    static let idleFinalizeInterval: TimeInterval = 8

    private var epoch = 0
    private var segmentGeneration = 0
    private var sourceText = ""
    private var translationText: [RealtimeTranslationOutputLanguage: String] = [:]
    private var selectedLane: RealtimeTranslationOutputLanguage?
    private var expectedLane: RealtimeTranslationOutputLanguage?
    private var languagePair: LanguagePair
    private var seenEventIDs = Set<String>()
    private var lastActivityAt = Date.distantPast
    private var finalizedCutoffElapsedMs: Int?
    private var awaitingSourceAfterFinalize = false

    init(languagePair: LanguagePair = .jaEn) {
        self.languagePair = languagePair
    }

    mutating func setLanguagePair(_ pair: LanguagePair) {
        languagePair = pair
    }

    mutating func reset(epoch: Int) {
        self.epoch = epoch
        segmentGeneration = 0
        clearSegmentBuffers(advancingGeneration: false)
        expectedLane = nil
        seenEventIDs.removeAll(keepingCapacity: true)
        finalizedCutoffElapsedMs = nil
        awaitingSourceAfterFinalize = false
    }

    mutating func beginNewEpoch(_ epoch: Int) {
        reset(epoch: epoch)
    }

    /// セッションが判定した期待翻訳lane。同言語echoより優先する。
    mutating func expectLane(_ lane: RealtimeTranslationOutputLanguage?) {
        expectedLane = lane
        if selectedLane == nil {
            resolveLaneIfNeeded()
        }
    }

    /// 言語切替時に現行ペアを確定する。完全ペアがなければbufferだけクリアする。
    mutating func finalizeForLanguageSwitch(now: Date = Date()) -> RealtimeSubtitleUpdate? {
        let hasCompletePair =
            !sourceText.isEmpty
            && selectedLane != nil
            && !currentTranslation.isEmpty
        if hasCompletePair {
            return finalizeCurrent(elapsedHint: nil, now: now)
        }
        clearSegmentBuffers(advancingGeneration: true)
        awaitingSourceAfterFinalize = true
        lastActivityAt = now
        return nil
    }

    mutating func ingest(
        _ streamEvent: RealtimeTranslationStreamEvent,
        now: Date = Date()
    ) -> RealtimeSubtitleUpdate? {
        guard streamEvent.epoch == epoch else { return nil }

        switch streamEvent.event {
        case .inputTranscriptDelta(let delta, let eventID, let elapsedMs):
            guard streamEvent.lane.isSource else { return nil }
            return appendSource(delta, eventID: eventID, elapsedMs: elapsedMs, now: now)
        case .outputTranscriptDelta(let delta, let eventID, let elapsedMs):
            guard case .translation(let target) = streamEvent.lane else { return nil }
            return appendTranslation(
                delta,
                target: target,
                eventID: eventID,
                elapsedMs: elapsedMs,
                now: now
            )
        default:
            return nil
        }
    }

    mutating func tick(now: Date = Date()) -> RealtimeSubtitleUpdate? {
        evaluateFinalize(now: now)
    }

    private mutating func appendSource(
        _ delta: String,
        eventID: String?,
        elapsedMs: Int?,
        now: Date
    ) -> RealtimeSubtitleUpdate? {
        guard !delta.isEmpty else { return nil }
        if let eventID, !seenEventIDs.insert(eventID).inserted {
            return nil
        }
        if let elapsedMs, let cutoff = finalizedCutoffElapsedMs, elapsedMs <= cutoff {
            return nil
        }

        if awaitingSourceAfterFinalize {
            awaitingSourceAfterFinalize = false
        } else if shouldStartNewSegmentForSourceUpdate() {
            clearSegmentBuffers(advancingGeneration: true)
        }

        sourceText += delta
        lastActivityAt = now
        resolveLaneIfNeeded()
        return snapshot(isTranslationCurrent: selectedLane != nil && !currentTranslation.isEmpty)
    }

    private mutating func appendTranslation(
        _ delta: String,
        target: RealtimeTranslationOutputLanguage,
        eventID: String?,
        elapsedMs: Int?,
        now: Date
    ) -> RealtimeSubtitleUpdate? {
        guard !delta.isEmpty else { return nil }
        // 確定後に届いた旧segmentの訳文で、保持中の完全ペアを上書きしない。
        // 次のsource deltaが来るまでtarget deltaは破棄する。
        guard !awaitingSourceAfterFinalize else { return nil }
        if let eventID, !seenEventIDs.insert(eventID).inserted {
            return nil
        }
        if let elapsedMs, let cutoff = finalizedCutoffElapsedMs, elapsedMs <= cutoff {
            return nil
        }

        translationText[target, default: ""] += delta

        lastActivityAt = now

        if selectedLane == nil {
            if let expectedLane, target == expectedLane {
                // 期待laneの出力を優先。旧targetからの同言語echoで誤選択しない。
                selectedLane = expectedLane
            } else if expectedLane == nil {
                // 一次信号: どちらのセッションが訳文を出したか。
                let pairTargets = languagePair.languages.compactMap { languagePair.translationTarget(for: $0) }
                let available = pairTargets.filter { !(translationText[$0] ?? "").isEmpty }
                if available.count == 1 {
                    selectedLane = available[0]
                } else {
                    resolveLaneIfNeeded()
                }
            } else {
                resolveLaneIfNeeded()
            }
        }

        // 非選択laneはbufferのみ。表示中の選択laneの現行フラグは維持する。
        return snapshot(
            isTranslationCurrent: selectedLane != nil
                && !sourceText.isEmpty
                && !currentTranslation.isEmpty
        )
    }

    private mutating func resolveLaneIfNeeded() {
        guard selectedLane == nil else { return }

        if let expectedLane {
            switch expectedLane {
            case .english where !(translationText[.english] ?? "").isEmpty:
                selectedLane = .english
                return
            case .japanese where !(translationText[.japanese] ?? "").isEmpty:
                selectedLane = .japanese
                return
            case .spanish where !(translationText[.spanish] ?? "").isEmpty:
                selectedLane = .spanish
                return
            default:
                // 期待laneがまだ出力していない間は、他laneのfirst-outputで確定しない。
                return
            }
        }

        // 一次: 片側だけが出力していればそれを選ぶ。
        let pairTargets = languagePair.languages.compactMap { languagePair.translationTarget(for: $0) }
        let available = pairTargets.filter { !(translationText[$0] ?? "").isEmpty }
        if available.count == 1 {
            selectedLane = available[0]
            return
        }

        // 補助: 原文の文字種。
        switch SpokenLanguageDetector.detect(sourceText, pair: languagePair) {
        case .japanese:
            selectedLane = languagePair.translationTarget(for: .japanese)
        case .english:
            selectedLane = languagePair.translationTarget(for: .english)
        case .spanish:
            selectedLane = languagePair.translationTarget(for: .spanish)
        case .unknown:
            break
        }
    }

    private mutating func evaluateFinalize(now: Date) -> RealtimeSubtitleUpdate? {
        guard !sourceText.isEmpty, let selectedLane else { return nil }
        let translation = currentTranslation
        guard !translation.isEmpty else { return nil }

        let idleExpired = now.timeIntervalSince(lastActivityAt) >= Self.idleFinalizeInterval
        if idleExpired {
            return finalizeCurrent(elapsedHint: nil, now: now)
        }
        return nil
    }

    private mutating func finalizeCurrent(
        elapsedHint: Int?,
        now: Date
    ) -> RealtimeSubtitleUpdate {
        if let elapsedHint {
            finalizedCutoffElapsedMs = elapsedHint
        }
        let update = RealtimeSubtitleUpdate(
            sourceText: sourceText,
            translatedText: currentTranslation,
            isTranslationCurrent: true,
            shouldFinalize: true,
            segmentGeneration: segmentGeneration
        )
        // 次のsource開始まで表示内容はaggregator側で保持する。
        clearSegmentBuffers(advancingGeneration: true)
        awaitingSourceAfterFinalize = true
        lastActivityAt = now
        return update
    }

    private var currentTranslation: String {
        switch selectedLane {
        selectedLane.flatMap { translationText[$0] } ?? ""
    }

    private func snapshot(isTranslationCurrent: Bool) -> RealtimeSubtitleUpdate {
        let translation = selectedLane == nil ? "" : currentTranslation
        return RealtimeSubtitleUpdate(
            sourceText: sourceText,
            translatedText: translation,
            isTranslationCurrent: isTranslationCurrent && !translation.isEmpty,
            shouldFinalize: false,
            segmentGeneration: segmentGeneration
        )
    }

    private mutating func clearSegmentBuffers(advancingGeneration: Bool) {
        sourceText = ""
        translationText.removeAll(keepingCapacity: true)
        selectedLane = nil
        if advancingGeneration {
            segmentGeneration += 1
        }
    }

    private func shouldStartNewSegmentForSourceUpdate() -> Bool {
        // 直前segment確定後、空のまま次の原文が来たら新segmentとして扱う。
        sourceText.isEmpty && selectedLane == nil && (!englishText.isEmpty || !japaneseText.isEmpty)
    }

}
