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
    /// event_id が無い delta の close drain 再適用防止。segment 境界で捨てる。
    /// live では本文キーで棄てない（原文は elapsedMs が常に nil で、繰り返し語が落ちる）。
    private var seenNilEventKeys = Set<String>()
    private var lastActivityAt = Date.distantPast
    private var finalizedCutoffElapsedMs: Int?
    private var maxTranslationElapsedMs: Int?
    private var awaitingSourceAfterFinalize = false
    private var translationIsCurrent = false

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
        seenNilEventKeys.removeAll(keepingCapacity: true)
        finalizedCutoffElapsedMs = nil
        maxTranslationElapsedMs = nil
        awaitingSourceAfterFinalize = false
        translationIsCurrent = false
    }

    mutating func beginNewEpoch(_ epoch: Int) {
        reset(epoch: epoch)
    }

    /// セッションが判定した期待翻訳lane。同言語echoより優先する。
    mutating func expectLane(_ lane: RealtimeTranslationOutputLanguage?) {
        expectedLane = lane
        if let expectedLane {
            // 一次信号: first-output / echo で lock 済みでも期待 lane へ付け替える。
            if !(translationText[expectedLane] ?? "").isEmpty {
                let alreadySelected = selectedLane == expectedLane
                selectedLane = expectedLane
                if !alreadySelected {
                    translationIsCurrent = true
                }
            } else if selectedLane != expectedLane {
                selectedLane = nil
                translationIsCurrent = false
            }
            return
        }

        if selectedLane == nil {
            resolveLaneIfNeeded()
        }
    }

    /// 言語切替時に現行ペアを確定する。完全ペアがなければbufferだけクリアする。
    /// hysteresis で原文が伸びて訳が stale でも、切替境界としては既存ペアを確定する。
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
        now: Date = Date(),
        fromStopDrain: Bool = false
    ) -> RealtimeSubtitleUpdate? {
        guard streamEvent.epoch == epoch else { return nil }

        switch streamEvent.event {
        case .inputTranscriptDelta(let delta, let eventID, let elapsedMs):
            guard streamEvent.lane.isSource else { return nil }
            return appendSource(
                delta,
                eventID: eventID,
                elapsedMs: elapsedMs,
                now: now,
                fromStopDrain: fromStopDrain
            )
        case .outputTranscriptDelta(let delta, let eventID, let elapsedMs):
            guard case .translation(let target) = streamEvent.lane else { return nil }
            return appendTranslation(
                delta,
                target: target,
                eventID: eventID,
                elapsedMs: elapsedMs,
                now: now,
                fromStopDrain: fromStopDrain
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
        now: Date,
        fromStopDrain: Bool
    ) -> RealtimeSubtitleUpdate? {
        guard !delta.isEmpty else { return nil }
        if let eventID, !seenEventIDs.insert(eventID).inserted {
            return nil
        }
        if let elapsedMs, let cutoff = finalizedCutoffElapsedMs, elapsedMs <= cutoff {
            return nil
        }

        var extendingExistingSource = !sourceText.isEmpty
        if awaitingSourceAfterFinalize {
            awaitingSourceAfterFinalize = false
        } else if shouldStartNewSegmentForSourceUpdate() {
            clearSegmentBuffers(advancingGeneration: true)
            extendingExistingSource = false
        }
        // nil-id キーは新 segment の clear より後に登録する。先に入れると safety-net が消える。
        // live は記録のみ。棄てるのは close drain 再適用だけ（正当な繰り返しを残す）。
        if eventID == nil,
           hasSeenNilEventKey(
            "source|\(delta)|\(elapsedMs.map(String.init) ?? "")",
            fromStopDrain: fromStopDrain
           ) {
            return nil
        }

        sourceText += delta
        lastActivityAt = now
        if extendingExistingSource && !currentTranslation.isEmpty {
            // 原文が伸びた間の旧訳文は表示用に残すが、現行でも確定対象でもない。
            translationIsCurrent = false
        }
        resolveLaneIfNeeded()
        return snapshot()
    }

    private mutating func appendTranslation(
        _ delta: String,
        target: RealtimeTranslationOutputLanguage,
        eventID: String?,
        elapsedMs: Int?,
        now: Date,
        fromStopDrain: Bool
    ) -> RealtimeSubtitleUpdate? {
        guard !delta.isEmpty else { return nil }
        // 確定後に届いた旧segmentの訳文で、保持中の完全ペアを上書きしない。
        // 次のsource deltaが来るまでtarget deltaは破棄する。
        guard !awaitingSourceAfterFinalize else { return nil }
        if hasSeenTranscriptEvent(
            eventID: eventID,
            key: "translation|\(target.rawValue)|\(delta)|\(elapsedMs.map(String.init) ?? "")",
            fromStopDrain: fromStopDrain
        ) {
            return nil
        }
        if let elapsedMs, let cutoff = finalizedCutoffElapsedMs, elapsedMs <= cutoff {
            return nil
        }

        translationText[target, default: ""] += delta
        rememberTranslationElapsed(elapsedMs)

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

        if selectedLane == target && !currentTranslation.isEmpty {
            translationIsCurrent = true
        }

        // 非選択laneはbufferのみ。表示中の選択laneの現行フラグは維持する。
        return snapshot()
    }

    private mutating func resolveLaneIfNeeded() {
        guard selectedLane == nil else { return }

        if let expectedLane {
            switch expectedLane {
            case .english where !(translationText[.english] ?? "").isEmpty:
                selectedLane = .english
                translationIsCurrent = true
                return
            case .japanese where !(translationText[.japanese] ?? "").isEmpty:
                selectedLane = .japanese
                translationIsCurrent = true
                return
            case .spanish where !(translationText[.spanish] ?? "").isEmpty:
                selectedLane = .spanish
                translationIsCurrent = true
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
            translationIsCurrent = true
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
        if let selectedLane, !(translationText[selectedLane] ?? "").isEmpty {
            translationIsCurrent = true
        }
    }

    private mutating func evaluateFinalize(now: Date) -> RealtimeSubtitleUpdate? {
        guard !sourceText.isEmpty, selectedLane != nil else { return nil }
        guard now.timeIntervalSince(lastActivityAt) >= Self.idleFinalizeInterval else { return nil }

        let translation = currentTranslation
        if !translation.isEmpty, translationIsCurrent {
            return finalizeCurrent(elapsedHint: nil, now: now)
        }
        if !translation.isEmpty {
            // 旧訳文は確定しないが、次発話の原文が同一セグメントへ連結しないよう境界だけ進める。
            abandonStaleSegment(now: now)
        }
        return nil
    }

    private mutating func finalizeCurrent(
        elapsedHint: Int?,
        now: Date
    ) -> RealtimeSubtitleUpdate {
        finalizedCutoffElapsedMs = elapsedHint ?? maxTranslationElapsedMs
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
        selectedLane.flatMap { translationText[$0] } ?? ""
    }

    private func snapshot() -> RealtimeSubtitleUpdate {
        let translation = selectedLane == nil ? "" : currentTranslation
        return RealtimeSubtitleUpdate(
            sourceText: sourceText,
            translatedText: translation,
            isTranslationCurrent: translationIsCurrent && !translation.isEmpty,
            shouldFinalize: false,
            segmentGeneration: segmentGeneration
        )
    }

    private mutating func abandonStaleSegment(now: Date) {
        finalizedCutoffElapsedMs = maxTranslationElapsedMs
        clearSegmentBuffers(advancingGeneration: true)
        awaitingSourceAfterFinalize = true
        lastActivityAt = now
    }

    private mutating func rememberTranslationElapsed(_ elapsedMs: Int?) {
        guard let elapsedMs else { return }
        maxTranslationElapsedMs = max(maxTranslationElapsedMs ?? elapsedMs, elapsedMs)
    }

    private mutating func clearSegmentBuffers(advancingGeneration: Bool) {
        sourceText = ""
        translationText.removeAll(keepingCapacity: true)
        selectedLane = nil
        translationIsCurrent = false
        seenNilEventKeys.removeAll(keepingCapacity: true)
        if advancingGeneration {
            segmentGeneration += 1
        }
    }

    private mutating func hasSeenTranscriptEvent(
        eventID: String?,
        key: String,
        fromStopDrain: Bool
    ) -> Bool {
        if let eventID {
            return !seenEventIDs.insert(eventID).inserted
        }
        return hasSeenNilEventKey(key, fromStopDrain: fromStopDrain)
    }

    /// live は本文キーを記録するだけ。同じ本文の繰り返しは残す。
    /// close drain だけ、既に live で見たキーを再適用しない。
    private mutating func hasSeenNilEventKey(_ key: String, fromStopDrain: Bool) -> Bool {
        if fromStopDrain {
            return !seenNilEventKeys.insert(key).inserted
        }
        seenNilEventKeys.insert(key)
        return false
    }

    private func shouldStartNewSegmentForSourceUpdate() -> Bool {
        // 直前segment確定後、空のまま次の原文が来たら新segmentとして扱う。
        sourceText.isEmpty && selectedLane == nil && translationText.values.contains { !$0.isEmpty }
    }

}
