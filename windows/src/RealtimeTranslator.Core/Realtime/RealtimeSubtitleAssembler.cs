using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RealtimeTranslator.Core.Audio;
using RealtimeTranslator.Core.OpenAI;

namespace RealtimeTranslator.Core.Realtime;

public readonly record struct RealtimeSubtitleUpdate(
    string SourceText,
    string TranslatedText,
    bool IsTranslationCurrent,
    bool ShouldFinalize,
    int SegmentGeneration,
    bool IsInvalidation = false,
    long Sequence = 0);

public readonly record struct LanguageSwitchSplit(
    RealtimeSubtitleUpdate? Finalized,
    RealtimeSubtitleUpdate Current);

/// <summary>原文 authority と複数出力言語を時間整列し、自動 lane 選択する。</summary>
public sealed class RealtimeSubtitleAssembler
{
    /// <summary>
    /// Realtime Translation は同一文の途中で 5 秒以上出力を止めることがある。
    /// 短い idle cutoff は訳文を切り落とすため 8 秒を使う。
    /// </summary>
    public static readonly TimeSpan IdleFinalizeInterval = TimeSpan.FromSeconds(8);
    /// <summary><c>shared/fixtures/v1/audio.json</c> の loss.taintedSegmentWindowMs と一致する。</summary>
    public static readonly TimeSpan AudioLossTaintWindow = TimeSpan.FromSeconds(8);

    private int _epoch;
    private int _segmentGeneration;
    private string _sourceText = string.Empty;
    private readonly Dictionary<RealtimeTranslationOutputLanguage, string> _translationText = new();
    private readonly Dictionary<RealtimeTranslationOutputLanguage, int> _translationSourceEnd = new();
    private RealtimeTranslationOutputLanguage? _selectedLane;
    private RealtimeTranslationOutputLanguage? _expectedLane;
    private readonly HashSet<string> _seenEventIds = new(StringComparer.Ordinal);
    private LanguagePair _languagePair;
    private DateTimeOffset _lastActivityAt = DateTimeOffset.MinValue;
    // elapsed_ms は接続ごとに独立した時計。lane 間で比較すると切替直後の
    // 新 lane の delta をすべて捨ててしまうため、確定カットオフと観測最大値は lane ごとに保持する。
    private readonly Dictionary<RealtimeTranslationLane, int> _finalizedCutoffElapsedMs = new();
    private readonly Dictionary<RealtimeTranslationLane, int> _maxElapsedMs = new();
    private bool _awaitingSourceAfterFinalize;
    private bool _boundaryCandidatePending;
    private bool _translationIsCurrent;
    private DateTimeOffset? _audioLossTaintedUntil;
    private bool _currentSegmentTainted;

    public RealtimeSubtitleAssembler(LanguagePair languagePair = LanguagePair.JaEn)
    {
        _languagePair = languagePair;
    }

    public void SetLanguagePair(LanguagePair languagePair) => _languagePair = languagePair;

    public void Reset(int epoch)
    {
        _epoch = epoch;
        _segmentGeneration = 0;
        ClearSegmentBuffers(advancingGeneration: false);
        _expectedLane = null;
        _seenEventIds.Clear();
        _finalizedCutoffElapsedMs.Clear();
        _maxElapsedMs.Clear();
        _awaitingSourceAfterFinalize = false;
        _boundaryCandidatePending = false;
        _translationIsCurrent = false;
        _audioLossTaintedUntil = null;
        _currentSegmentTainted = false;
    }

    public void BeginNewEpoch(int epoch) => Reset(epoch);

    public int SegmentGeneration => _segmentGeneration;

    public string CurrentSourceText => _sourceText;

    public int CurrentSourceLength => _sourceText.Length;

    public bool HasUnconfirmedContent =>
        _sourceText.Length > 0 || _translationText.Values.Any(value => value.Length > 0);

    public void DiscardUnconfirmed()
    {
        ClearSegmentBuffers(advancingGeneration: true);
        _expectedLane = null;
        _awaitingSourceAfterFinalize = false;
        _boundaryCandidatePending = false;
        _audioLossTaintedUntil = null;
        _currentSegmentTainted = false;
    }

    public bool IsCurrentSegmentTainted => _currentSegmentTainted;

    public void MarkAudioLoss(DateTimeOffset now)
    {
        DiscardUnconfirmed();
        _audioLossTaintedUntil = now + AudioLossTaintWindow;
    }

    public void SetBoundaryCandidatePending(bool pending) =>
        _boundaryCandidatePending = pending;

    /// <summary>セッションが判定した期待翻訳 lane。同言語 echo より優先する。</summary>
    public void ExpectLane(RealtimeTranslationOutputLanguage? lane)
    {
        _expectedLane = lane;
        if (lane is { } expectedLane)
        {
            // 一次信号: first-output / echo で lock 済みでも期待 lane へ付け替える。
            if (_translationText.GetValueOrDefault(expectedLane, string.Empty).Length > 0)
            {
                var alreadySelected = _selectedLane == expectedLane;
                _selectedLane = expectedLane;
                if (!alreadySelected)
                {
                    _translationIsCurrent = true;
                }
            }
            else if (_selectedLane != expectedLane)
            {
                _selectedLane = null;
                _translationIsCurrent = false;
            }

            return;
        }

        if (_selectedLane is null)
        {
            ResolveLaneIfNeeded();
        }
    }

    /// <summary>
    /// 言語切替時に現行ペアを確定する。完全ペアがなければ buffer だけクリアする。
    /// 訳文の受理位置が境界に到達済み（>= offset）の場合のみ prefix ペアを確定し、未到達の stale 訳文は破棄する。
    /// </summary>
    public LanguageSwitchSplit SplitForLanguageSwitch(int offset, DateTimeOffset now)
    {
        var splitOffset = BoundaryOffsetMovingWhitespaceToNewSide(offset);
        var prefix = _sourceText[..splitOffset];
        var suffix = _sourceText[splitOffset..];
        var hasCompletePair = prefix.Length > 0
            && _selectedLane is not null
            && CurrentTranslation.Length > 0
            && _translationSourceEnd.GetValueOrDefault(_selectedLane.Value) >= splitOffset;
        RealtimeSubtitleUpdate? finalized = null;
        if (hasCompletePair)
        {
            ApplyFinalizedCutoffs();
            if (!_currentSegmentTainted)
            {
                finalized = new RealtimeSubtitleUpdate(
                    prefix,
                    CurrentTranslation,
                    IsTranslationCurrent: true,
                    ShouldFinalize: true,
                    SegmentGeneration: _segmentGeneration);
            }
        }

        var keepTaint = _currentSegmentTainted && suffix.Length > 0;
        ClearSegmentBuffers(advancingGeneration: true);
        _sourceText = suffix;
        _currentSegmentTainted = keepTaint;
        _awaitingSourceAfterFinalize = suffix.Length == 0;
        _boundaryCandidatePending = false;
        _lastActivityAt = now;
        return new LanguageSwitchSplit(finalized, Snapshot());
    }

    public RealtimeSubtitleUpdate? Ingest(RealtimeTranslationStreamEvent streamEvent, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(streamEvent);

        if (streamEvent.Epoch != _epoch)
        {
            return null;
        }

        switch (streamEvent.Event)
        {
            case RealtimeTranslationServerEvent.InputTranscriptDelta source:
                // 原文 transcription 接続の source lane だけを authority とする。
                return streamEvent.Lane.IsSource
                    ? AppendSource(source.Delta, source.EventId, source.ElapsedMs, now)
                    : null;

            case RealtimeTranslationServerEvent.OutputTranscriptDelta translation:
                return AppendTranslation(
                    translation.Delta,
                    streamEvent.Target,
                    translation.EventId,
                    translation.ElapsedMs,
                    now);

            default:
                return null;
        }
    }

    public RealtimeSubtitleUpdate? Tick(DateTimeOffset now) => EvaluateFinalize(now);

    private RealtimeSubtitleUpdate? AppendSource(string delta, string? eventId, int? elapsedMs, DateTimeOffset now)
    {
        // 実 API の原文 delta は elapsed_ms を持たない（shared/protocol/endpoints.md）。
        // source lane の cutoff は fixture 契約（late source delta）との互換のためだけに評価する。
        if (delta.Length == 0 || IsDuplicateOrStale(eventId, elapsedMs, RealtimeTranslationLane.Source))
        {
            return null;
        }

        var extendingExistingSource = _sourceText.Length > 0;
        if (_awaitingSourceAfterFinalize)
        {
            _awaitingSourceAfterFinalize = false;
        }
        else if (ShouldStartNewSegmentForSourceUpdate())
        {
            ClearSegmentBuffers(advancingGeneration: true);
            extendingExistingSource = false;
        }

        if (_sourceText.Length == 0)
        {
            _currentSegmentTainted = _audioLossTaintedUntil is { } deadline
                && now <= deadline;
            _audioLossTaintedUntil = null;
        }

        _sourceText += delta;
        RememberElapsed(elapsedMs, RealtimeTranslationLane.Source);
        _lastActivityAt = now;
        if (extendingExistingSource && CurrentTranslation.Length > 0)
        {
            // 原文が伸びた間の旧訳文は表示用に残すが、現行でも確定対象でもない。
            _translationIsCurrent = false;
        }

        ResolveLaneIfNeeded();
        return Snapshot();
    }

    private RealtimeSubtitleUpdate? AppendTranslation(
        string delta,
        RealtimeTranslationOutputLanguage target,
        string? eventId,
        int? elapsedMs,
        DateTimeOffset now)
    {
        // 確定後に届いた旧 segment の訳文で、保持中の完全ペアを上書きしない。
        // 次の source delta が来るまで target delta は破棄する。
        if (delta.Length == 0
            || _awaitingSourceAfterFinalize
            || IsDuplicateOrStale(eventId, elapsedMs, RealtimeTranslationLane.Translation(target)))
        {
            return null;
        }

        _translationText[target] = _translationText.GetValueOrDefault(target, string.Empty) + delta;
        _translationSourceEnd[target] = _sourceText.Length;
        RememberElapsed(elapsedMs, RealtimeTranslationLane.Translation(target));

        _lastActivityAt = now;

        if (_selectedLane is null)
        {
            if (_expectedLane is { } expectedLane && target == expectedLane)
            {
                // 期待 lane の出力を優先。旧 target からの同言語 echo で誤選択しない。
                _selectedLane = expectedLane;
            }
            else if (_expectedLane is null && _translationText.Count(text => text.Value.Length > 0) == 1)
            {
                _selectedLane = _translationText.First(text => text.Value.Length > 0).Key;
            }
            else
            {
                ResolveLaneIfNeeded();
            }
        }

        if (_selectedLane == target && CurrentTranslation.Length > 0)
        {
            _translationIsCurrent = true;
        }

        // 非選択 lane は buffer のみ。表示中の選択 lane の現行フラグは維持する。
        return Snapshot();
    }

    private bool IsDuplicateOrStale(string? eventId, int? elapsedMs, RealtimeTranslationLane lane)
    {
        if (eventId is not null && !_seenEventIds.Add(eventId))
        {
            return true;
        }

        return elapsedMs is { } elapsed
            && _finalizedCutoffElapsedMs.TryGetValue(lane, out var cutoff)
            && elapsed <= cutoff;
    }

    private void ResolveLaneIfNeeded()
    {
        if (_selectedLane is not null)
        {
            return;
        }

        if (_expectedLane is { } expectedLane)
        {
            // 期待 lane がまだ出力していない間は、他 lane の first-output で確定しない。
            if (_translationText.GetValueOrDefault(expectedLane, string.Empty).Length > 0)
            {
                _selectedLane = expectedLane;
                _translationIsCurrent = true;
            }

            return;
        }

        // 一次: 片側だけが出力していればそれを選ぶ。
        var populated = _translationText.Where(text => text.Value.Length > 0).ToArray();
        if (populated.Length == 1)
        {
            _selectedLane = populated[0].Key;
            _translationIsCurrent = true;
            return;
        }

        // 補助: 原文の文字種。
        _selectedLane = _languagePair.TranslationTarget(
            SpokenLanguageDetector.Detect(_sourceText, _languagePair));
        if (_selectedLane is { } detected
            && _translationText.GetValueOrDefault(detected, string.Empty).Length > 0)
        {
            _translationIsCurrent = true;
        }
    }

    private RealtimeSubtitleUpdate? EvaluateFinalize(DateTimeOffset now)
    {
        if (_sourceText.Length == 0 || _selectedLane is null)
        {
            return null;
        }

        if (now - _lastActivityAt < IdleFinalizeInterval)
        {
            return null;
        }

        if (CurrentTranslation.Length > 0 && _translationIsCurrent)
        {
            // 未確定の境界候補（文末の製品名など）は、切替未確定のまま idle した完全ペアを止めない。
            if (_currentSegmentTainted)
            {
                return AbandonStaleSegment(now);
            }

            return FinalizeCurrent(elapsedHint: null, now);
        }

        if (_boundaryCandidatePending)
        {
            return null;
        }

        if (CurrentTranslation.Length > 0)
        {
            // 旧訳文は確定しないが、次発話の原文が同一セグメントへ連結しないよう境界だけ進める。
            if (_currentSegmentTainted)
            {
                return AbandonStaleSegment(now);
            }

            AbandonStaleSegment(now);
        }

        return null;
    }

    private RealtimeSubtitleUpdate FinalizeCurrent(int? elapsedHint, DateTimeOffset now)
    {
        ApplyFinalizedCutoffs();
        if (elapsedHint is { } hint && _selectedLane is { } selectedLane)
        {
            _finalizedCutoffElapsedMs[RealtimeTranslationLane.Translation(selectedLane)] = hint;
        }

        var update = new RealtimeSubtitleUpdate(
            _sourceText,
            CurrentTranslation,
            IsTranslationCurrent: true,
            ShouldFinalize: true,
            _segmentGeneration);

        // 次の source 開始まで表示内容は aggregator 側で保持する。
        ClearSegmentBuffers(advancingGeneration: true);
        _awaitingSourceAfterFinalize = true;
        _currentSegmentTainted = false;
        _lastActivityAt = now;
        return update;
    }

    private string CurrentTranslation => _selectedLane switch
    {
        { } lane => _translationText.GetValueOrDefault(lane, string.Empty),
        _ => string.Empty,
    };

    private RealtimeSubtitleUpdate Snapshot()
    {
        var translation = _selectedLane is null ? string.Empty : CurrentTranslation;
        return new RealtimeSubtitleUpdate(
            _sourceText,
            translation,
            _translationIsCurrent && translation.Length > 0,
            ShouldFinalize: false,
            _segmentGeneration);
    }

    private RealtimeSubtitleUpdate AbandonStaleSegment(DateTimeOffset now)
    {
        ApplyFinalizedCutoffs();
        ClearSegmentBuffers(advancingGeneration: true);
        _awaitingSourceAfterFinalize = true;
        _currentSegmentTainted = false;
        _lastActivityAt = now;
        return new RealtimeSubtitleUpdate(
            string.Empty,
            string.Empty,
            IsTranslationCurrent: false,
            ShouldFinalize: false,
            _segmentGeneration,
            IsInvalidation: true);
    }

    private void ApplyFinalizedCutoffs()
    {
        foreach (var pair in _maxElapsedMs)
        {
            _finalizedCutoffElapsedMs[pair.Key] = pair.Value;
        }
    }

    private void RememberElapsed(int? elapsedMs, RealtimeTranslationLane lane)
    {
        if (elapsedMs is not { } elapsed)
        {
            return;
        }

        _maxElapsedMs[lane] = _maxElapsedMs.TryGetValue(lane, out var max)
            ? Math.Max(max, elapsed)
            : elapsed;
    }

    private void ClearSegmentBuffers(bool advancingGeneration)
    {
        _sourceText = string.Empty;
        _translationText.Clear();
        _translationSourceEnd.Clear();
        _selectedLane = null;
        _translationIsCurrent = false;
        _boundaryCandidatePending = false;
        if (advancingGeneration)
        {
            _segmentGeneration += 1;
        }
    }

    private int AlignedSourceOffset(int offset)
    {
        var bounded = Math.Clamp(offset, 0, CurrentSourceLength);
        var current = 0;
        foreach (var rune in _sourceText.EnumerateRunes())
        {
            var next = current + rune.Utf16SequenceLength;
            if (next > bounded)
            {
                return current;
            }

            current = next;
        }

        return current;
    }

    /// <summary>空白と ¿ / ¡ は新側へ付ける。tracker の candidate が次語先頭でもプレフィックスを合わせる。</summary>
    private int BoundaryOffsetMovingWhitespaceToNewSide(int offset)
    {
        var bounded = AlignedSourceOffset(offset);
        var entries = new List<(int Offset, Rune Rune)>();
        var cursor = 0;
        foreach (var rune in _sourceText.EnumerateRunes())
        {
            entries.Add((cursor, rune));
            cursor += rune.Utf16SequenceLength;
        }

        var result = bounded;
        var index = 0;
        while (index < entries.Count && entries[index].Offset < bounded)
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

    /// <summary>直前 segment 確定後、空のまま次の原文が来たら新 segment として扱う。</summary>
    private bool ShouldStartNewSegmentForSourceUpdate() =>
        _sourceText.Length == 0
        && _selectedLane is null
        && _translationText.Any(text => text.Value.Length > 0);
}
