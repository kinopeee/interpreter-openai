using System;
using System.Collections.Generic;
using RealtimeTranslator.Core.Audio;
using RealtimeTranslator.Core.OpenAI;

namespace RealtimeTranslator.Core.Realtime;

public readonly record struct RealtimeSubtitleUpdate(
    string SourceText,
    string TranslatedText,
    bool IsTranslationCurrent,
    bool ShouldFinalize,
    int SegmentGeneration);

/// <summary>原文 authority と日英 2 出力を時間整列し、自動 lane 選択する。</summary>
public sealed class RealtimeSubtitleAssembler
{
    /// <summary>
    /// Realtime Translation は同一文の途中で 5 秒以上出力を止めることがある。
    /// 短い idle cutoff は訳文を切り落とすため 8 秒を使う。
    /// </summary>
    public static readonly TimeSpan IdleFinalizeInterval = TimeSpan.FromSeconds(8);

    private int _epoch;
    private int _segmentGeneration;
    private string _sourceText = string.Empty;
    private string _englishText = string.Empty;
    private string _japaneseText = string.Empty;
    private RealtimeTranslationOutputLanguage? _selectedLane;
    private RealtimeTranslationOutputLanguage? _expectedLane;
    private readonly HashSet<string> _seenEventIds = new(StringComparer.Ordinal);
    private DateTimeOffset _lastActivityAt = DateTimeOffset.MinValue;
    private int? _finalizedCutoffElapsedMs;
    private bool _awaitingSourceAfterFinalize;

    public void Reset(int epoch)
    {
        _epoch = epoch;
        _segmentGeneration = 0;
        ClearSegmentBuffers(advancingGeneration: false);
        _expectedLane = null;
        _seenEventIds.Clear();
        _finalizedCutoffElapsedMs = null;
        _awaitingSourceAfterFinalize = false;
    }

    public void BeginNewEpoch(int epoch) => Reset(epoch);

    /// <summary>セッションが判定した期待翻訳 lane。同言語 echo より優先する。</summary>
    public void ExpectLane(RealtimeTranslationOutputLanguage? lane)
    {
        _expectedLane = lane;
        if (_selectedLane is null)
        {
            ResolveLaneIfNeeded();
        }
    }

    /// <summary>言語切替時に現行ペアを確定する。完全ペアがなければ buffer だけクリアする。</summary>
    public RealtimeSubtitleUpdate? FinalizeForLanguageSwitch(DateTimeOffset now)
    {
        var hasCompletePair = _sourceText.Length > 0
            && _selectedLane is not null
            && CurrentTranslation.Length > 0;
        if (hasCompletePair)
        {
            return FinalizeCurrent(elapsedHint: null, now);
        }

        ClearSegmentBuffers(advancingGeneration: true);
        _awaitingSourceAfterFinalize = true;
        _lastActivityAt = now;
        return null;
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
                // 原文は英語 target 接続 (原文 transcription) だけを authority とする。
                return streamEvent.Target == RealtimeTranslationOutputLanguage.English
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
        if (delta.Length == 0 || IsDuplicateOrStale(eventId, elapsedMs))
        {
            return null;
        }

        if (_awaitingSourceAfterFinalize)
        {
            _awaitingSourceAfterFinalize = false;
        }
        else if (ShouldStartNewSegmentForSourceUpdate())
        {
            ClearSegmentBuffers(advancingGeneration: true);
        }

        _sourceText += delta;
        _lastActivityAt = now;
        ResolveLaneIfNeeded();
        return Snapshot(_selectedLane is not null && CurrentTranslation.Length > 0);
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
        if (delta.Length == 0 || _awaitingSourceAfterFinalize || IsDuplicateOrStale(eventId, elapsedMs))
        {
            return null;
        }

        if (target == RealtimeTranslationOutputLanguage.English)
        {
            _englishText += delta;
        }
        else
        {
            _japaneseText += delta;
        }

        _lastActivityAt = now;

        if (_selectedLane is null)
        {
            if (_expectedLane is { } expectedLane && target == expectedLane)
            {
                // 期待 lane の出力を優先。旧 target からの同言語 echo で誤選択しない。
                _selectedLane = expectedLane;
            }
            else if (_expectedLane is null && (_englishText.Length == 0) != (_japaneseText.Length == 0))
            {
                // 一次信号: どちらのセッションが訳文を出したか。
                _selectedLane = _englishText.Length == 0
                    ? RealtimeTranslationOutputLanguage.Japanese
                    : RealtimeTranslationOutputLanguage.English;
            }
            else
            {
                ResolveLaneIfNeeded();
            }
        }

        // 非選択 lane は buffer のみ。表示中の選択 lane の現行フラグは維持する。
        return Snapshot(_selectedLane is not null && _sourceText.Length > 0 && CurrentTranslation.Length > 0);
    }

    private bool IsDuplicateOrStale(string? eventId, int? elapsedMs)
    {
        if (eventId is not null && !_seenEventIds.Add(eventId))
        {
            return true;
        }

        return elapsedMs is { } elapsed && _finalizedCutoffElapsedMs is { } cutoff && elapsed <= cutoff;
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
            if (expectedLane == RealtimeTranslationOutputLanguage.English && _englishText.Length > 0)
            {
                _selectedLane = RealtimeTranslationOutputLanguage.English;
            }
            else if (expectedLane == RealtimeTranslationOutputLanguage.Japanese && _japaneseText.Length > 0)
            {
                _selectedLane = RealtimeTranslationOutputLanguage.Japanese;
            }

            return;
        }

        // 一次: 片側だけが出力していればそれを選ぶ。
        if (_englishText.Length > 0 && _japaneseText.Length == 0)
        {
            _selectedLane = RealtimeTranslationOutputLanguage.English;
            return;
        }

        if (_japaneseText.Length > 0 && _englishText.Length == 0)
        {
            _selectedLane = RealtimeTranslationOutputLanguage.Japanese;
            return;
        }

        // 補助: 原文の文字種。
        _selectedLane = SpokenLanguageDetector.Detect(_sourceText).TranslationTarget();
    }

    private RealtimeSubtitleUpdate? EvaluateFinalize(DateTimeOffset now)
    {
        if (_sourceText.Length == 0 || _selectedLane is null || CurrentTranslation.Length == 0)
        {
            return null;
        }

        return now - _lastActivityAt >= IdleFinalizeInterval ? FinalizeCurrent(elapsedHint: null, now) : null;
    }

    private RealtimeSubtitleUpdate FinalizeCurrent(int? elapsedHint, DateTimeOffset now)
    {
        if (elapsedHint is { } hint)
        {
            _finalizedCutoffElapsedMs = hint;
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
        _lastActivityAt = now;
        return update;
    }

    private string CurrentTranslation => _selectedLane switch
    {
        RealtimeTranslationOutputLanguage.English => _englishText,
        RealtimeTranslationOutputLanguage.Japanese => _japaneseText,
        _ => string.Empty,
    };

    private RealtimeSubtitleUpdate Snapshot(bool isTranslationCurrent)
    {
        var translation = _selectedLane is null ? string.Empty : CurrentTranslation;
        return new RealtimeSubtitleUpdate(
            _sourceText,
            translation,
            isTranslationCurrent && translation.Length > 0,
            ShouldFinalize: false,
            _segmentGeneration);
    }

    private void ClearSegmentBuffers(bool advancingGeneration)
    {
        _sourceText = string.Empty;
        _englishText = string.Empty;
        _japaneseText = string.Empty;
        _selectedLane = null;
        if (advancingGeneration)
        {
            _segmentGeneration += 1;
        }
    }

    /// <summary>直前 segment 確定後、空のまま次の原文が来たら新 segment として扱う。</summary>
    private bool ShouldStartNewSegmentForSourceUpdate() =>
        _sourceText.Length == 0
        && _selectedLane is null
        && (_englishText.Length > 0 || _japaneseText.Length > 0);
}
