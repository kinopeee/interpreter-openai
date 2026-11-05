using System;
using System.Threading;
using System.Threading.Tasks;
using RealtimeTranslator.Core.Localization;
using RealtimeTranslator.Core.OpenAI;

namespace RealtimeTranslator.Core.Realtime;

/// <summary>
/// 接続試行中のイベント消費。feed のイベントを epoch フィルタ・欠落検査しながら
/// 字幕パイプラインへ流し、ServerError / InputTranscriptFailed の分類と
/// routing action（select/switch/next-segment reset）を適用する。
/// 契約の中核であり、判定・記録・acknowledge の順序は変更しない。
/// </summary>
internal sealed class SessionEventConsumer
{
    private readonly SessionRuntimeContext _context;

    internal SessionEventConsumer(SessionRuntimeContext context)
    {
        _context = context;
    }

    public async Task RunAsync(int generation, RealtimeEventFeed feed, CancellationToken cancellationToken)
    {
        var events = feed.Events;
        while (true)
        {
            var waitToRead = events.WaitToReadAsync(cancellationToken).AsTask();
            await Task.WhenAny(waitToRead, feed.DeliveryState.Completion).ConfigureAwait(false);

            if (feed.DeliveryState.DidLoseEvents)
            {
                HandleEventLossAndThrowIfCurrent(generation, feed);
                return;
            }

            while (events.TryRead(out var streamEvent))
            {
                if (!_context.IsCurrentGeneration(generation))
                {
                    return;
                }

                if (streamEvent.Epoch != feed.Epoch)
                {
                    continue;
                }

                if (feed.DeliveryState.DidLoseEvents)
                {
                    HandleEventLossAndThrowIfCurrent(generation, feed);
                    return;
                }

                if (streamEvent.Event is RealtimeTranslationServerEvent.ServerError error)
                {
                    var classification = EventDeliveryState.Classify(error);
                    if (classification.Disposition == RealtimeServerErrorDisposition.KeepAlive)
                    {
                        continue;
                    }

                    feed.DeliveryState.TryRecordTermination(classification);
                    throw feed.DeliveryState.ToException();
                }

                // delta 文字列は monitor へ渡さない（検知は到着・進捗の事実だけを見る）。
                lock (_context.Sync)
                {
                    switch (streamEvent.Event)
                    {
                        case RealtimeTranslationServerEvent.InputTranscriptDelta input
                            when streamEvent.Lane.IsSource && input.Delta.Length > 0:
                            _context.HealthBookkeeper.RecordSourceProgress();
                            break;
                        case RealtimeTranslationServerEvent.OutputTranscriptDelta output when output.Delta.Length > 0:
                            _context.HealthBookkeeper.RecordTranslationProgress(streamEvent.Lane);
                            break;
                    }
                }

                if (streamEvent.Event is RealtimeTranslationServerEvent.InputTranscriptFailed failed)
                {
                    RealtimeServerErrorClassification classification =
                        RealtimeServerErrorClassification.ClassifyTranscriptionFailure(failed.ErrorType, failed.Code);
                    RealtimeSubtitleUpdate? invalidation;
                    lock (_context.Sync)
                    {
                        invalidation = _context.SubtitlePipeline.DiscardFailedSource(failed.ItemId, failed.EventId);
                    }
                    feed.DeliveryState.NoteSourceFailureConsumed();

                    if (invalidation is { } failedUpdate)
                    {
                        _context.SubtitlePipeline.EmitSubtitleUpdate(failedUpdate);
                        await _context.AudioRouting.ResetForNextSegmentAsync().ConfigureAwait(false);
                    }

                    if (classification.Disposition == RealtimeServerErrorDisposition.KeepAlive)
                    {
                        continue;
                    }

                    feed.DeliveryState.TryRecordTermination(classification);
                    throw feed.DeliveryState.ToException();
                }

                _context.BeforeAssemblerIngestForTests?.Invoke();

                RealtimeSubtitleProcessingResult? result = null;
                var lostAfterRouting = false;
                lock (_context.Sync)
                {
                    // Dispose/Stop が generation を進めたあとに、取り出し済みイベントで
                    // assembler を更新しない（flush 後の完全ペア欠落を防ぐ）。
                    if (!_context.IsCurrentGeneration(generation))
                    {
                        return;
                    }

                    if (feed.DeliveryState.DidLoseEvents)
                    {
                        lostAfterRouting = true;
                    }
                    else
                    {
                        result = _context.SubtitlePipeline.Process(streamEvent);
                    }
                }

                if (lostAfterRouting)
                {
                    HandleEventLossAndThrowIfCurrent(generation, feed);
                    return;
                }

                if (result is { } processed)
                {
                    foreach (var update in processed.Updates)
                    {
                        _context.SubtitlePipeline.EmitSubtitleUpdate(update);
                    }

                    switch (processed.RoutingAction)
                    {
                        case RealtimeSubtitleRoutingAction.None:
                            if (processed.IngestedUpdate.ShouldFinalize)
                            {
                                await _context.AudioRouting.ResetForNextSegmentAsync().ConfigureAwait(false);
                            }

                            break;
                        case RealtimeSubtitleRoutingAction.Select select:
                            await _context.AudioRouting.ApplyAsync(select, cancellationToken).ConfigureAwait(false);
                            break;
                        case RealtimeSubtitleRoutingAction.Switch @switch:
                            await _context.AudioRouting.ApplyAsync(@switch, cancellationToken).ConfigureAwait(false);
                            break;
                    }
                }
            }

            if (feed.DeliveryState.Completion.IsCompleted || !await waitToRead.ConfigureAwait(false))
            {
                break;
            }
        }

        if (!_context.IsCurrentGeneration(generation))
        {
            return;
        }

        _context.SubtitlePipeline.DiscardFailedSourceIfNeeded(feed);
        if (feed.DeliveryState.Termination != EventDeliveryTermination.None)
        {
            throw feed.DeliveryState.ToException();
        }

        throw new RealtimeTranslationException(
            RealtimeTranslationErrorKind.RecoverableTransportFailure,
            UserCopy.Current.Text("error.eventStreamStopped")
        );
    }

    /// <summary>受信欠落した feed の未確定字幕を無効化し、現世代なら欠落例外を投げる。旧世代なら何もせず戻る。</summary>
    private void HandleEventLossAndThrowIfCurrent(int generation, RealtimeEventFeed feed)
    {
        _context.SubtitlePipeline.HandleEventLoss(feed);
        if (_context.IsCurrentGeneration(generation))
        {
            throw feed.DeliveryState.ToException();
        }
    }
}
