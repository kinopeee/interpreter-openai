import Foundation

struct SourceBoundaryTracker: Sendable {
    private(set) var candidateOffset: Int?
    private var observedSegmentGeneration: Int?

    mutating func reset() {
        candidateOffset = nil
        observedSegmentGeneration = nil
    }

    mutating func observe(
        segmentSource: String,
        deltaStart: Int,
        segmentGeneration: Int,
        pair: LanguagePair,
        currentLanguage: SpokenLanguage,
        reverseEvidenceCount: Int
    ) {
        if observedSegmentGeneration != segmentGeneration {
            reset()
            observedSegmentGeneration = segmentGeneration
        }

        let sourceLength = segmentSource.utf16.count
        let start = min(max(deltaStart, 0), sourceLength)
        if pair == .enEs {
            observeEnEs(
                segmentSource: segmentSource,
                deltaStart: start,
                currentLanguage: currentLanguage,
                reverseEvidenceCount: reverseEvidenceCount
            )
        } else {
            observeScriptPair(
                segmentSource: segmentSource,
                deltaStart: start,
                currentLanguage: currentLanguage
            )
        }
    }

    private mutating func observeScriptPair(
        segmentSource: String,
        deltaStart: Int,
        currentLanguage: SpokenLanguage
    ) {
        let oppositeIsJapanese = currentLanguage != .japanese
        let oppositeIsLatin = currentLanguage == .japanese
        let entries = scalarEntries(in: segmentSource)

        for entry in entries where entry.offset >= deltaStart {
            let isJapanese = Self.isJapanese(entry.scalar)
            let isLatin = SpokenLanguageDetector.isLatinWordScalar(entry.scalar)
            let isOpposite = oppositeIsJapanese ? isJapanese : isLatin
            let isOwn = oppositeIsJapanese ? isLatin : isJapanese

            if candidateOffset == nil, isOpposite {
                candidateOffset = moveBackwardOverNewSidePrefix(
                    candidateOffset: entry.offset,
                    entries: entries
                )
            } else if candidateOffset != nil, isOwn {
                candidateOffset = nil
            }
        }
    }

    private mutating func observeEnEs(
        segmentSource: String,
        deltaStart _: Int,
        currentLanguage: SpokenLanguage,
        reverseEvidenceCount: Int
    ) {
        guard reverseEvidenceCount > 0 else {
            candidateOffset = nil
            return
        }
        guard reverseEvidenceCount == 1 else { return }

        let reverseLanguage = currentLanguage == .english ? SpokenLanguage.spanish : .english
        if let candidateOffset,
           firstCueStarting(atOrAfter: candidateOffset, in: segmentSource) == reverseLanguage
        {
            return
        }

        let spans = SpokenLanguageDetector.wordSpans(in: segmentSource)
        let recentStart = SpokenLanguageDetector.recentWordWindowStart(in: segmentSource)
        let recentStartOffset = recentStart.utf16Offset(in: segmentSource)
        let recentEnd = segmentSource.utf16.count
        let recentSpans = spans.filter {
            $0.lowerBound >= recentStartOffset && $0.upperBound <= recentEnd
        }
        let firstReverseWord = recentSpans.first {
            cueLanguage(String(segmentSource.utf16Slice($0))) == reverseLanguage
        }?.lowerBound
        let firstReverseMark = firstStandaloneSpanishMark(
            in: segmentSource,
            from: recentStartOffset,
            to: recentEnd
        )
        let cueStart = [firstReverseWord, firstReverseMark]
            .compactMap { $0 }
            .min()

        guard let cueStart else {
            candidateOffset = nil
            return
        }

        let sentenceStart = sentenceStart(
            in: segmentSource,
            windowStart: recentStartOffset,
            before: cueStart
        )
        let hasCurrentCue = recentSpans.contains {
            $0.lowerBound >= sentenceStart
                && $0.lowerBound < cueStart
                && cueLanguage(String(segmentSource.utf16Slice($0)))
                    == currentLanguage
        }
        let rawCandidate = hasCurrentCue ? cueStart : sentenceStart
        candidateOffset = moveBackwardOverNewSidePrefix(
            candidateOffset: rawCandidate,
            entries: scalarEntries(in: segmentSource)
        )
    }

    private func firstCueStarting(
        atOrAfter offset: Int,
        in source: String
    ) -> SpokenLanguage? {
        let firstWord = SpokenLanguageDetector.wordSpans(in: source)
            .filter { $0.lowerBound >= offset }
            .compactMap { span -> (Int, SpokenLanguage)? in
                guard let language = cueLanguage(String(source.utf16Slice(span))) else {
                    return nil
                }
                return (span.lowerBound, language)
            }
            .first
        let firstMark = firstStandaloneSpanishMark(
            in: source,
            from: offset,
            to: source.utf16.count
        ).map { ($0, SpokenLanguage.spanish) }
        switch [firstWord, firstMark].compactMap({ $0 }).min(by: { $0.0 < $1.0 })?.1 {
        case .some(let language):
            return language
        case .none:
            return nil
        }
    }

    private func cueLanguage(_ word: String) -> SpokenLanguage? {
        let lower = word.lowercased(with: Locale(identifier: "en_US_POSIX"))
        if SpokenLanguageDetector.englishExclusiveWords.contains(lower) {
            return .english
        }
        if SpokenLanguageDetector.spanishExclusiveWords.contains(lower)
            || word.unicodeScalars.contains(where: Self.isSpanishAccentOrN)
        {
            return .spanish
        }
        return nil
    }

    private func sentenceStart(in source: String, windowStart: Int, before offset: Int) -> Int {
        var result = windowStart
        for entry in scalarEntries(in: source)
            where entry.offset >= windowStart && entry.offset < offset
        {
            if Self.isSentenceTerminator(entry.scalar) {
                result = entry.offset + entry.scalar.utf16.count
            }
        }
        return result
    }

    private func firstStandaloneSpanishMark(
        in source: String,
        from start: Int,
        to end: Int
    ) -> Int? {
        scalarEntries(in: source).first {
            $0.offset >= start
                && $0.offset < end
                && ($0.scalar.value == 0x00BF || $0.scalar.value == 0x00A1)
        }?.offset
    }

    private func moveBackwardOverNewSidePrefix(
        candidateOffset: Int,
        entries: [(offset: Int, scalar: Unicode.Scalar)]
    ) -> Int {
        var result = candidateOffset
        var index = entries.firstIndex { $0.offset >= candidateOffset } ?? entries.count
        while index > 0 {
            let previous = entries[index - 1]
            guard CharacterSet.whitespacesAndNewlines.contains(previous.scalar)
                || previous.scalar.value == 0x00BF
                || previous.scalar.value == 0x00A1
            else {
                break
            }
            result = previous.offset
            index -= 1
        }
        return result
    }

    private func scalarEntries(in source: String) -> [(offset: Int, scalar: Unicode.Scalar)] {
        var result: [(offset: Int, scalar: Unicode.Scalar)] = []
        var offset = 0
        for scalar in source.unicodeScalars {
            result.append((offset: offset, scalar: scalar))
            offset += scalar.utf16.count
        }
        return result
    }

    private static func isJapanese(_ scalar: Unicode.Scalar) -> Bool {
        switch scalar.value {
        case 0x3040...0x30FF, 0x3400...0x4DBF, 0x4E00...0x9FFF:
            return true
        default:
            return false
        }
    }

    private static func isSpanishAccentOrN(_ scalar: Unicode.Scalar) -> Bool {
        switch scalar.value {
        case 0x00E1, 0x00E9, 0x00ED, 0x00F3, 0x00FA, 0x00FC,
             0x00C1, 0x00C9, 0x00CD, 0x00D3, 0x00DA, 0x00DC,
             0x00F1, 0x00D1:
            return true
        default:
            return false
        }
    }

    private static func isSentenceTerminator(_ scalar: Unicode.Scalar) -> Bool {
        scalar.value == 0x002E || scalar.value == 0x0021 || scalar.value == 0x003F
            || scalar.value == 0x3002 || scalar.value == 0xFF01 || scalar.value == 0xFF1F
    }
}

private extension String {
    func utf16Slice(_ range: Range<Int>) -> String {
        let start = String.Index(utf16Offset: range.lowerBound, in: self)
        let end = String.Index(utf16Offset: range.upperBound, in: self)
        return String(self[start..<end])
    }
}
