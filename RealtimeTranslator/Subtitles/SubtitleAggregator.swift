import Foundation

struct SubtitleAggregatorConfig: Sendable {
    var idleFinalizeInterval: TimeInterval = 1.0
    var maxJapaneseCharacters = 60
    var maxEnglishCharacters = 120
}

final class SubtitleAggregator: @unchecked Sendable {
    private let config: SubtitleAggregatorConfig
    private let lock = NSLock()

    private var current = LiveSubtitle.empty
    private var statusBanner: String?

    init(config: SubtitleAggregatorConfig = SubtitleAggregatorConfig()) {
        self.config = config
    }

    func reset() {
        lock.lock()
        defer { lock.unlock() }
        current = .empty
        statusBanner = nil
    }

    func setStatusBanner(_ message: String?) {
        lock.lock()
        defer { lock.unlock() }
        statusBanner = message
    }

    @discardableResult
    func replaceCurrent(
        sourceText: String,
        translatedText: String,
        isTranslationCurrent: Bool = true,
        canFinalize: Bool = true,
        now: Date = Date()
    ) -> SubtitleSnapshot {
        lock.lock()
        defer { lock.unlock() }
        // 履歴なし: 確定済みでも次の発話でそのまま上書きする。
        current = LiveSubtitle(
            sourceText: sourceText,
            translatedText: translatedText,
            lastUpdatedAt: now,
            state: .live,
            isTranslationCurrent: isTranslationCurrent,
            canFinalize: canFinalize
        )
        return snapshotLocked()
    }

    @discardableResult
    func appendSource(_ delta: String, now: Date = Date()) -> SubtitleSnapshot {
        lock.lock()
        defer { lock.unlock() }
        guard !delta.isEmpty else { return snapshotLocked() }
        if current.state == .finalized {
            current = LiveSubtitle(
                sourceText: delta,
                translatedText: "",
                lastUpdatedAt: now,
                state: .live,
                isTranslationCurrent: false,
                canFinalize: false
            )
            return snapshotLocked()
        }
        current.sourceText += delta
        current.isTranslationCurrent = false
        current.canFinalize = false
        current.lastUpdatedAt = now
        current.state = .live
        finalizeIfNeededLocked(now: now)
        return snapshotLocked()
    }

    @discardableResult
    func appendTranslation(_ delta: String, now: Date = Date()) -> SubtitleSnapshot {
        lock.lock()
        defer { lock.unlock() }
        guard !delta.isEmpty else { return snapshotLocked() }
        current.translatedText += delta
        current.isTranslationCurrent = true
        current.canFinalize = true
        current.lastUpdatedAt = now
        current.state = .live
        finalizeIfNeededLocked(now: now)
        return snapshotLocked()
    }

    @discardableResult
    func tick(now: Date = Date()) -> SubtitleSnapshot {
        lock.lock()
        defer { lock.unlock() }
        finalizeIfNeededLocked(now: now)
        return snapshotLocked()
    }

    @discardableResult
    func forceFinalize(now: Date = Date()) -> SubtitleSnapshot {
        lock.lock()
        defer { lock.unlock() }
        // 停止時は idle 用の canFinalize に依存せず、原文＋現行訳文が揃っていれば確定する。
        let hasPair =
            !current.sourceText.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
            && !current.translatedText.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
            && current.isTranslationCurrent
        if hasPair {
            current.canFinalize = true
            current.state = .finalized
            current.lastUpdatedAt = now
        } else {
            clearCurrentLocked(now: now)
        }
        return snapshotLocked()
    }

    @discardableResult
    func finalizePair(
        sourceText: String,
        translatedText: String,
        clearCurrent: Bool,
        now: Date = Date()
    ) -> SubtitleSnapshot {
        lock.lock()
        defer { lock.unlock() }
        let finalized = LiveSubtitle(
            sourceText: sourceText,
            translatedText: translatedText,
            lastUpdatedAt: now,
            state: .finalized,
            isTranslationCurrent: true,
            canFinalize: true
        )
        guard hasCompletePair(finalized) else {
            return snapshotLocked()
        }

        if clearCurrent {
            // このペアが現在の発話そのものなら、視線を動かさないためcurrentに残す。
            // 次の発話開始(replaceCurrent)まで保持し、タイマーでは消さない。
            current = finalized
        }
        // clearCurrent == false: 次発話が既にcurrentを占めている。履歴がないため破棄する。
        return snapshotLocked()
    }

    func snapshot(now: Date = Date()) -> SubtitleSnapshot {
        lock.lock()
        defer { lock.unlock() }
        return snapshotLocked()
    }

    @discardableResult
    func invalidateCurrent(now: Date = Date()) -> SubtitleSnapshot {
        lock.lock()
        defer { lock.unlock() }
        if current.state != .finalized {
            clearCurrentLocked(now: now)
        }
        var snapshot = snapshotLocked()
        snapshot.isInvalidation = true
        return snapshot
    }

    private func finalizeIfNeededLocked(now: Date) {
        guard !current.isEmpty else { return }
        guard current.state != .finalized else { return }
        guard hasCompletePair(current) else { return }

        let idleExpired = now.timeIntervalSince(current.lastUpdatedAt) >= config.idleFinalizeInterval
        // Wait for target punctuation. Source punctuation often arrives before translation
        // and finalizing there would split the source and translation into separate blocks.
        let punctuation = endsWithTerminalPunctuation(current.translatedText)
        let tooLong = exceedsMaxLength(current.sourceText, japanesePreferred: true)
            || exceedsMaxLength(current.translatedText, japanesePreferred: false)

        if punctuation || idleExpired || tooLong {
            current.state = .finalized
            current.lastUpdatedAt = now
        }
    }

    private func clearCurrentLocked(now: Date) {
        current = LiveSubtitle(
            sourceText: "",
            translatedText: "",
            lastUpdatedAt: now,
            state: .live,
            isTranslationCurrent: false,
            canFinalize: false
        )
    }

    private func hasCompletePair(_ subtitle: LiveSubtitle) -> Bool {
        !subtitle.sourceText.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
            && !subtitle.translatedText.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
            && subtitle.isTranslationCurrent
            && subtitle.canFinalize
    }

    private func snapshotLocked() -> SubtitleSnapshot {
        SubtitleSnapshot(
            current: current,
            statusBanner: statusBanner
        )
    }

    private func endsWithTerminalPunctuation(_ text: String) -> Bool {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard let last = trimmed.last else { return false }
        return "。．.!？?！".contains(last)
    }

    private func exceedsMaxLength(_ text: String, japanesePreferred: Bool) -> Bool {
        let hasCJK = text.unicodeScalars.contains { scalar in
            (0x3040...0x30FF).contains(scalar.value)
                || (0x4E00...0x9FFF).contains(scalar.value)
                || (0x3400...0x4DBF).contains(scalar.value)
        }
        let limit = hasCJK || japanesePreferred
            ? config.maxJapaneseCharacters
            : config.maxEnglishCharacters
        return text.count > limit
    }
}
