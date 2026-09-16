using System;
using System.Collections.Generic;
using RealtimeTranslator.Core.Audio;
using RealtimeTranslator.Core.OpenAI;

namespace RealtimeTranslator.Core.Realtime;

internal abstract record RealtimeSubtitleRoutingAction
{
    internal sealed record None : RealtimeSubtitleRoutingAction;

    internal sealed record Select(RealtimeTranslationOutputLanguage? Target)
        : RealtimeSubtitleRoutingAction;

    internal sealed record Switch(RealtimeTranslationOutputLanguage? Target)
        : RealtimeSubtitleRoutingAction;
}

internal sealed record RealtimeSubtitleProcessingResult(
    IReadOnlyList<RealtimeSubtitleUpdate> Updates,
    RealtimeSubtitleRoutingAction RoutingAction,
    RealtimeSubtitleUpdate IngestedUpdate,
    bool IsSourceUpdate);

internal sealed class RealtimeSubtitleProcessor
{
    private readonly RealtimeSubtitleAssembler _assembler = new();
    private readonly SourceBoundaryTracker _sourceBoundaryTracker = new();

    internal LanguagePair? ActiveLanguagePair { get; private set; }

    internal string RoutingSourceText { get; private set; } = string.Empty;

    private RealtimeTranslationOutputLanguage? _selectedTranslationTarget;
    private int _reverseEvidenceCount;
    private readonly HashSet<string> _handledFailedSourceKeys = new(StringComparer.Ordinal);

    internal int CurrentSourceLength => _assembler.CurrentSourceLength;

    internal int SegmentGeneration => _assembler.SegmentGeneration;

    internal void BeginEpoch(int epoch, LanguagePair pair)
    {
        _assembler.SetLanguagePair(pair);
        _assembler.BeginNewEpoch(epoch);
        ClearBoundaryCandidate();
        RoutingSourceText = string.Empty;
        ActiveLanguagePair = pair;
        _selectedTranslationTarget = null;
        _reverseEvidenceCount = 0;
        _handledFailedSourceKeys.Clear();
    }

    internal void DeactivateLanguagePair()
    {
        ActiveLanguagePair = null;
    }

    internal void ClearBoundaryCandidate()
    {
        _sourceBoundaryTracker.Reset();
        _assembler.SetBoundaryCandidatePending(false);
    }

    internal void ResetRoutingForNextSegment()
    {
        RoutingSourceText = string.Empty;
        _selectedTranslationTarget = null;
        _reverseEvidenceCount = 0;
        ClearBoundaryCandidate();
        _assembler.ExpectLane(null);
    }

    internal RealtimeSubtitleUpdate DiscardUnconfirmed()
    {
        _assembler.DiscardUnconfirmed();
        ClearBoundaryCandidate();
        RoutingSourceText = string.Empty;
        _selectedTranslationTarget = null;
        _reverseEvidenceCount = 0;
        return new RealtimeSubtitleUpdate(
            string.Empty,
            string.Empty,
            IsTranslationCurrent: false,
            ShouldFinalize: false,
            _assembler.SegmentGeneration,
            IsInvalidation: true);
    }

    internal RealtimeSubtitleUpdate? DiscardFailedSource(string? itemId, string? eventId)
    {
        var key = itemId ?? eventId;
        if (key is not null && _handledFailedSourceKeys.Contains(key))
        {
            return null;
        }

        if (!_assembler.HasUnconfirmedContent)
        {
            return null;
        }

        if (key is not null)
        {
            _handledFailedSourceKeys.Add(key);
        }

        return DiscardUnconfirmed();
    }

    internal RealtimeSubtitleUpdate? Tick(DateTimeOffset now) => _assembler.Tick(now);

    internal RealtimeSubtitleProcessingResult? Process(
        RealtimeTranslationStreamEvent streamEvent,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(streamEvent);

        if (streamEvent.Event is RealtimeTranslationServerEvent.InputTranscriptFailed)
        {
            return null;
        }

        var deltaStart = _assembler.CurrentSourceLength;
        var sourceDelta = streamEvent.Event
            is RealtimeTranslationServerEvent.InputTranscriptDelta source
            && streamEvent.Lane.IsSource
            ? source.Delta
            : null;
        var update = _assembler.Ingest(streamEvent, now);
        if (update is null)
        {
            return null;
        }

        var result = new RealtimeSubtitleProcessingResult(
            [update.Value],
            new RealtimeSubtitleRoutingAction.None(),
            update.Value,
            sourceDelta is not null);
        if (sourceDelta is null)
        {
            return result;
        }

        return EvaluateSourceRouting(sourceDelta, deltaStart, now, result);
    }

    /// <summary>原文 delta の文字種から発話言語を決め、逆側 target へ音声を切り替える。</summary>
    private RealtimeSubtitleProcessingResult EvaluateSourceRouting(
        string delta,
        int deltaStart,
        DateTimeOffset now,
        RealtimeSubtitleProcessingResult result)
    {
        if (ActiveLanguagePair is not { } pair)
        {
            return result;
        }

        RoutingSourceText = RoutingSourceTextWindow.Trim(RoutingSourceText + delta, pair);
        var evidence = SpokenLanguageDetector.RecentEvidence(RoutingSourceText, pair);
        var selection = TranslationTargetSelector.Select(
            pair,
            _selectedTranslationTarget,
            _reverseEvidenceCount,
            evidence);
        _reverseEvidenceCount = selection.ReverseEvidenceCount;

        if (_selectedTranslationTarget is not { } currentTarget)
        {
            if (selection.Target is not { } target)
            {
                return result;
            }

            _selectedTranslationTarget = target;
            _sourceBoundaryTracker.Reset();
            _assembler.SetBoundaryCandidatePending(false);
            _assembler.ExpectLane(target);
            return result with { RoutingAction = new RealtimeSubtitleRoutingAction.Select(target) };
        }

        if (selection.Target == currentTarget)
        {
            if (pair.Counterpart(currentTarget) is { } currentLanguage)
            {
                _sourceBoundaryTracker.Observe(
                    _assembler.CurrentSourceText,
                    deltaStart,
                    _assembler.SegmentGeneration,
                    pair,
                    currentLanguage,
                    selection.ReverseEvidenceCount);
                _assembler.SetBoundaryCandidatePending(
                    _sourceBoundaryTracker.CandidateOffset is not null);
            }

            return result;
        }

        if (pair != LanguagePair.EnEs
            && pair.Counterpart(currentTarget) is { } currentLanguageForSwitch)
        {
            _sourceBoundaryTracker.Observe(
                _assembler.CurrentSourceText,
                deltaStart,
                _assembler.SegmentGeneration,
                pair,
                currentLanguageForSwitch,
                0);
        }

        var offset = _sourceBoundaryTracker.CandidateOffset ?? deltaStart;
        var split = _assembler.SplitForLanguageSwitch(offset, now);
        _sourceBoundaryTracker.Reset();
        RoutingSourceText = RoutingSourceTextWindow.Trim(
            _assembler.CurrentSourceText,
            pair);
        _selectedTranslationTarget = selection.Target;
        _reverseEvidenceCount = 0;
        _assembler.ExpectLane(selection.Target);
        return result with
        {
            Updates = split.Finalized is { } finalized
                ? [finalized, split.Current]
                : [split.Current],
            RoutingAction = new RealtimeSubtitleRoutingAction.Switch(selection.Target),
        };
    }
}
