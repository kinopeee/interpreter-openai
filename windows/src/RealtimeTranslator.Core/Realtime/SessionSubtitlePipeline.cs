using System;
using System.Threading.Tasks;
using RealtimeTranslator.Core.Audio;
using RealtimeTranslator.Core.OpenAI;

namespace RealtimeTranslator.Core.Realtime;

/// <summary>
/// 字幕パイプラインの帳簿。<see cref="RealtimeSubtitleProcessor"/> と
/// <see cref="AudioLossTracker"/>、subtitle sequence、loss epoch を束ねる。
/// <c>InterpretationSession</c> の <c>_sync</c> ロック契約を引き継ぎ、独自ロックは持たない。
/// processor 呼び出しは呼び出し側が <c>_sync</c> を保持する前提。
/// </summary>
internal sealed class SessionSubtitlePipeline
{
    private readonly object _sync;
    private readonly TimeProvider _timeProvider;
    private readonly RealtimeSubtitleProcessor _processor = new();
    private readonly AudioLossTracker _audioLossTracker = new();
    private readonly Func<RealtimeEventFeed?> _activeFeedProvider;
    private readonly Func<RealtimeEventFeed> _fallbackFeedProvider;
    private readonly Action<RealtimeSubtitleUpdate> _emitUpdate;
    private readonly Func<Action?> _afterFailedSourceFallbackProvider;
    private int? _handledLossEpoch;
    private long _subtitleSequence;

    public SessionSubtitlePipeline(
        object sync,
        TimeProvider timeProvider,
        Func<RealtimeEventFeed?> activeFeedProvider,
        Func<RealtimeEventFeed> fallbackFeedProvider,
        Action<RealtimeSubtitleUpdate> emitUpdate,
        Func<Action?> afterFailedSourceFallbackProvider)
    {
        ArgumentNullException.ThrowIfNull(sync);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(activeFeedProvider);
        ArgumentNullException.ThrowIfNull(fallbackFeedProvider);
        ArgumentNullException.ThrowIfNull(emitUpdate);
        ArgumentNullException.ThrowIfNull(afterFailedSourceFallbackProvider);
        _sync = sync;
        _timeProvider = timeProvider;
        _activeFeedProvider = activeFeedProvider;
        _fallbackFeedProvider = fallbackFeedProvider;
        _emitUpdate = emitUpdate;
        _afterFailedSourceFallbackProvider = afterFailedSourceFallbackProvider;
    }

    public AudioLossMetrics AudioLossMetrics => _audioLossTracker.Metrics;

    public LanguagePair? ActiveLanguagePair => _processor.ActiveLanguagePair;

    public bool HasSelectedTranslationTarget => _processor.HasSelectedTranslationTarget;

    public int CurrentSourceLength => _processor.CurrentSourceLength;

    public bool IsCurrentSegmentTainted => _processor.IsCurrentSegmentTainted;

    /// <summary>新しい epoch を開始し、欠落処理済み epoch をリセットする（_sync 下で呼ぶ）。</summary>
    public void BeginEpoch(int epoch, LanguagePair languagePair)
    {
        _processor.BeginEpoch(epoch, languagePair);
        _handledLossEpoch = null;
    }

    public void DeactivateLanguagePair() => _processor.DeactivateLanguagePair();

    public void ClearBoundaryCandidate() => _processor.ClearBoundaryCandidate();

    public void ResetRoutingForNextSegment() => _processor.ResetRoutingForNextSegment();

    public void ResetAudioLoss() => _audioLossTracker.Reset();

    public AudioLossObservation ObserveAudio(
        int generation,
        long sequence,
        int discardedMilliseconds,
        int queueWaitMilliseconds,
        long atMilliseconds) =>
        _audioLossTracker.Observe(
            generation,
            sequence,
            discardedMilliseconds,
            queueWaitMilliseconds,
            atMilliseconds);

    public RealtimeSubtitleUpdate MarkAudioLoss() =>
        _processor.MarkAudioLoss(_timeProvider.GetUtcNow());

    public RealtimeSubtitleUpdate? DiscardFailedSource(string? itemId, string? eventId) =>
        _processor.DiscardFailedSource(itemId, eventId);

    public RealtimeSubtitleUpdate DiscardUnconfirmed() => _processor.DiscardUnconfirmed();

    public RealtimeSubtitleUpdate? Tick() => _processor.Tick(_timeProvider.GetUtcNow());

    public RealtimeSubtitleProcessingResult? Process(RealtimeTranslationStreamEvent streamEvent) =>
        _processor.Process(streamEvent, _timeProvider.GetUtcNow());

    /// <summary>
    /// 完全な原文+訳文ペアが assembler に残っていれば idle 待ちを飛ばして確定する。
    /// 停止・再接続・致命エラーで epoch/buffer を捨てる直前に呼び、字幕記録の欠落を防ぐ。
    /// </summary>
    public void FlushPendingFinalizeIfNeeded()
    {
        var feed = _activeFeedProvider();
        if (feed is { DeliveryState.DidLoseEvents: true })
        {
            HandleEventLoss(feed);
            return;
        }
        if (feed is { DeliveryState.HasPendingSourceFailure: true })
        {
            DiscardFailedSourceIfNeeded(feed);
            return;
        }

        RealtimeSubtitleUpdate? pending;
        lock (_sync)
        {
            pending = _processor.IsCurrentSegmentTainted
                ? _processor.DiscardUnconfirmed()
                : _processor.Tick(
                    _timeProvider.GetUtcNow() + RealtimeSubtitleAssembler.IdleFinalizeInterval);
        }

        if (pending is { } update)
        {
            EmitSubtitleUpdate(update);
        }
    }

    public void DiscardFailedSourceIfNeeded(RealtimeEventFeed feed)
    {
        if (!feed.DeliveryState.HasPendingSourceFailure)
        {
            return;
        }

        RealtimeSubtitleUpdate? invalidation;
        lock (_sync)
        {
            invalidation = _processor.DiscardFailedSource(null, null);
        }

        if (invalidation is { } failedUpdate)
        {
            EmitSubtitleUpdate(failedUpdate);
        }

        while (feed.DeliveryState.HasPendingSourceFailure)
        {
            feed.DeliveryState.NoteSourceFailureConsumed();
        }
        _afterFailedSourceFallbackProvider()?.Invoke();
    }

    /// <summary>
    /// 正常停止の close drain で channel に残った字幕イベントを assembler へ取り込む。
    /// session consumer は世代更新で既に止まっている前提。
    /// </summary>
    public async Task IngestStopDrainEventsAsync()
    {
        var feed = _activeFeedProvider();
        if (feed is { DeliveryState.DidLoseEvents: true })
        {
            HandleEventLoss(feed);
            return;
        }

        var events = feed?.Events ?? _fallbackFeedProvider().Events;
        while (await events.WaitToReadAsync().ConfigureAwait(false))
        {
            IngestAlreadyQueuedEvents();
        }
    }

    /// <summary>
    /// すでに channel にあるイベントだけを取り込む。WaitToRead しないので、
    /// ForceClose 前の再接続 teardown から呼んでも開いたままの channel で止まらない。
    /// </summary>
    public void IngestAlreadyQueuedEvents()
    {
        var feed = _activeFeedProvider();
        if (feed is { DeliveryState.DidLoseEvents: true })
        {
            HandleEventLoss(feed);
            return;
        }

        var currentFeed = feed ?? _fallbackFeedProvider();
        var events = currentFeed.Events;
        while (events.TryRead(out var streamEvent))
        {
            if (streamEvent.Event is RealtimeTranslationServerEvent.ServerError)
            {
                continue;
            }

            if (streamEvent.Event is RealtimeTranslationServerEvent.InputTranscriptFailed failed)
            {
                if (streamEvent.Epoch != currentFeed.Epoch)
                {
                    continue;
                }

                RealtimeSubtitleUpdate? invalidation;
                lock (_sync)
                {
                    invalidation = _processor.DiscardFailedSource(failed.ItemId, failed.EventId);
                }
                currentFeed.DeliveryState.NoteSourceFailureConsumed();

                if (invalidation is { } failedUpdate)
                {
                    EmitSubtitleUpdate(failedUpdate);
                }

                continue;
            }

            RealtimeSubtitleProcessingResult? result;
            lock (_sync)
            {
                if (feed is { DeliveryState.DidLoseEvents: true })
                {
                    result = null;
                }
                else
                {
                    result = _processor.Process(streamEvent, _timeProvider.GetUtcNow());
                }
            }

            if (result is { } processed)
            {
                foreach (var update in processed.Updates)
                {
                    EmitSubtitleUpdate(update);
                }
            }
        }
    }

    public bool HandleEventLoss(RealtimeEventFeed feed)
    {
        RealtimeSubtitleUpdate? invalidation = null;
        lock (_sync)
        {
            if (!feed.DeliveryState.DidLoseEvents
                || _handledLossEpoch == feed.Epoch)
            {
                return false;
            }

            _handledLossEpoch = feed.Epoch;
            invalidation = _processor.DiscardUnconfirmed();
        }

        EmitSubtitleUpdate(invalidation.Value);
        return true;
    }

    public void EmitSubtitleUpdate(RealtimeSubtitleUpdate update)
    {
        RealtimeSubtitleUpdate stamped;
        lock (_sync)
        {
            stamped = update.Sequence == 0
                ? update with { Sequence = ++_subtitleSequence }
                : update;
        }

        _emitUpdate(stamped);
    }
}
