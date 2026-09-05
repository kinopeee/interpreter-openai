using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Unicode;
using RealtimeTranslator.Core.Audio;

namespace RealtimeTranslator.Core.Realtime;

public sealed class SourceBoundaryTracker
{
    public int? CandidateOffset { get; private set; }

    private int? _observedSegmentGeneration;

    public void Reset()
    {
        CandidateOffset = null;
        _observedSegmentGeneration = null;
    }

    public void Observe(
        string segmentSource,
        int deltaStart,
        int segmentGeneration,
        LanguagePair pair,
        SpokenLanguage currentLanguage,
        int reverseEvidenceCount)
    {
        ArgumentNullException.ThrowIfNull(segmentSource);
        if (_observedSegmentGeneration != segmentGeneration)
        {
            Reset();
            _observedSegmentGeneration = segmentGeneration;
        }

        var start = Math.Clamp(deltaStart, 0, segmentSource.Length);
        if (pair == LanguagePair.EnEs)
        {
            ObserveEnEs(segmentSource, currentLanguage, reverseEvidenceCount);
        }
        else
        {
            ObserveScriptPair(segmentSource, start, currentLanguage);
        }
    }

    private void ObserveScriptPair(
        string source,
        int deltaStart,
        SpokenLanguage currentLanguage)
    {
        var oppositeIsJapanese = currentLanguage != SpokenLanguage.Japanese;
        var entries = ScalarEntries(source);
        foreach (var entry in entries.Where(entry => entry.Offset >= deltaStart))
        {
            var isJapanese = IsJapanese(entry.Rune);
            var isLatin = SpokenLanguageDetector.IsLatinWordScalar(entry.Rune);
            var isOpposite = oppositeIsJapanese ? isJapanese : isLatin;
            var isOwn = oppositeIsJapanese ? isLatin : isJapanese;

            if (CandidateOffset is null && isOpposite)
            {
                CandidateOffset = MoveBackwardOverNewSidePrefix(
                    entry.Offset,
                    entries);
            }
            else if (CandidateOffset is not null && isOwn)
            {
                CandidateOffset = null;
            }
        }
    }

    private void ObserveEnEs(
        string source,
        SpokenLanguage currentLanguage,
        int reverseEvidenceCount)
    {
        if (reverseEvidenceCount == 0)
        {
            CandidateOffset = null;
            return;
        }

        if (reverseEvidenceCount != 1)
        {
            return;
        }

        var reverseLanguage = currentLanguage == SpokenLanguage.English
            ? SpokenLanguage.Spanish
            : SpokenLanguage.English;
        var entries = ScalarEntries(source);
        if (CandidateOffset is { } candidate
            && FirstCueStartingAtOrAfter(source, candidate, entries) == reverseLanguage)
        {
            return;
        }

        var spans = SpokenLanguageDetector.WordSpans(source);
        var windowStart = SpokenLanguageDetector.RecentWordWindowStart(source);
        var recentSpans = spans
            .Where(span => span.Start >= windowStart)
            .ToArray();
        var firstReverseWord = recentSpans
            .Select(span => (Span: span, Language: CueLanguage(source[span.Start..span.End])))
            .Where(value => value.Language == reverseLanguage)
            .Select(value => (int?)value.Span.Start)
            .FirstOrDefault();
        var firstReverseMark = FirstStandaloneSpanishMark(
            source,
            windowStart,
            source.Length,
            entries);
        var cueStart = new[] { firstReverseWord, firstReverseMark }
            .Where(value => value is not null)
            .Select(value => value!.Value)
            .DefaultIfEmpty()
            .Min();
        var hasCue = firstReverseWord is not null || firstReverseMark is not null;

        if (!hasCue)
        {
            CandidateOffset = null;
            return;
        }

        var sentenceStart = SentenceStart(source, windowStart, cueStart, entries);
        var hasCurrentCue = recentSpans.Any(span =>
            span.Start >= sentenceStart
            && span.Start < cueStart
            && CueLanguage(source[span.Start..span.End]) == currentLanguage);
        var rawCandidate = hasCurrentCue ? cueStart : sentenceStart;
        CandidateOffset = MoveBackwardOverNewSidePrefix(rawCandidate, entries);
    }

    private static SpokenLanguage? FirstCueStartingAtOrAfter(
        string source,
        int offset,
        List<(int Offset, Rune Rune)> entries)
    {
        int? firstWordOffset = null;
        SpokenLanguage? firstWordLanguage = null;
        foreach (var span in SpokenLanguageDetector.WordSpans(source)
            .Where(span => span.Start >= offset))
        {
            var language = CueLanguage(source[span.Start..span.End]);
            if (language is not null)
            {
                firstWordOffset = span.Start;
                firstWordLanguage = language;
                break;
            }
        }
        var firstMark = FirstStandaloneSpanishMark(source, offset, source.Length, entries);
        if (firstWordLanguage is null && firstMark is null)
        {
            return null;
        }

        if (firstMark is null || (firstWordOffset is not null && firstWordOffset < firstMark))
        {
            return firstWordLanguage;
        }

        return SpokenLanguage.Spanish;
    }

    private static SpokenLanguage? CueLanguage(string word)
    {
        var lower = word.ToLowerInvariant();
        if (SpokenLanguageDetector.EnglishExclusiveWords.Contains(lower))
        {
            return SpokenLanguage.English;
        }

        if (SpokenLanguageDetector.SpanishExclusiveWords.Contains(lower)
            || word.EnumerateRunes().Any(IsSpanishAccentOrN))
        {
            return SpokenLanguage.Spanish;
        }

        return null;
    }

    private static int SentenceStart(
        string source,
        int windowStart,
        int before,
        List<(int Offset, Rune Rune)> entries)
    {
        var result = windowStart;
        foreach (var entry in entries
            .Where(entry => entry.Offset >= windowStart && entry.Offset < before))
        {
            if (IsSentenceTerminator(entry.Rune))
            {
                result = entry.Offset + entry.Rune.Utf16SequenceLength;
            }
        }

        return result;
    }

    private static int? FirstStandaloneSpanishMark(
        string source,
        int start,
        int end,
        List<(int Offset, Rune Rune)> entries)
    {
        foreach (var entry in entries)
        {
            if (entry.Offset >= start
                && entry.Offset < end
                && (entry.Rune.Value == 0x00BF || entry.Rune.Value == 0x00A1))
            {
                return entry.Offset;
            }
        }

        return null;
    }

    private static int MoveBackwardOverNewSidePrefix(
        int candidate,
        List<(int Offset, Rune Rune)> entries)
    {
        var result = candidate;
        var index = 0;
        while (index < entries.Count && entries[index].Offset < candidate)
        {
            index++;
        }

        while (index > 0)
        {
            var previous = entries[index - 1];
            if (!Rune.IsWhiteSpace(previous.Rune)
                && previous.Rune.Value != 0x00BF
                && previous.Rune.Value != 0x00A1)
            {
                break;
            }

            result = previous.Offset;
            index--;
        }

        return result;
    }

    private static List<(int Offset, Rune Rune)> ScalarEntries(string source)
    {
        var entries = new List<(int Offset, Rune Rune)>();
        var offset = 0;
        foreach (var rune in source.EnumerateRunes())
        {
            entries.Add((offset, rune));
            offset += rune.Utf16SequenceLength;
        }

        return entries;
    }

    private static bool IsJapanese(Rune rune) =>
        rune.Value is >= 0x3040 and <= 0x30FF
            or >= 0x3400 and <= 0x4DBF
            or >= 0x4E00 and <= 0x9FFF;

    private static bool IsSpanishAccentOrN(Rune rune) =>
        rune.Value is 0x00E1 or 0x00E9 or 0x00ED or 0x00F3 or 0x00FA or 0x00FC
            or 0x00C1 or 0x00C9 or 0x00CD or 0x00D3 or 0x00DA or 0x00DC
            or 0x00F1 or 0x00D1;

    private static bool IsSentenceTerminator(Rune rune) =>
        rune.Value is 0x002E or 0x0021 or 0x003F or 0x3002 or 0xFF01 or 0xFF1F;
}
