using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using RealtimeTranslator.Core.OpenAI;

namespace RealtimeTranslator.Core.Realtime;

/// <summary>
/// 接続イベントを <see cref="MergedEventBuffer"/> へ集約する merge ポンプ。
/// merge ポリシー（OutputAudioDelta 廃棄・翻訳 lane の InputTranscriptDelta 廃棄・
/// epoch 再貼付・開始済み target フィルタ）をここに閉じ込める。
/// 状態は呼び出し側と共有する <c>_sync</c> 配下でのみ触れる（独自ロックは持たない）。
/// </summary>
internal sealed class EventMergePump
{
    private readonly object _sync;
    private readonly MergedEventBuffer _eventBuffer;
    private readonly Func<int> _connectionEpochProvider;
    private readonly Func<ChannelReader<RealtimeTranslationStreamEvent>> _sourceReaderProvider;
    private readonly Func<
        RealtimeTranslationOutputLanguage,
        ChannelReader<RealtimeTranslationStreamEvent>
    > _translationReaderProvider;
    private readonly Func<RealtimeTranslationOutputLanguage[]> _startedTargetsProvider;

    internal EventMergePump(
        object sync,
        MergedEventBuffer eventBuffer,
        Func<int> connectionEpochProvider,
        Func<ChannelReader<RealtimeTranslationStreamEvent>> sourceReaderProvider,
        Func<
            RealtimeTranslationOutputLanguage,
            ChannelReader<RealtimeTranslationStreamEvent>
        > translationReaderProvider,
        Func<RealtimeTranslationOutputLanguage[]> startedTargetsProvider
    )
    {
        _sync = sync;
        _eventBuffer = eventBuffer;
        _connectionEpochProvider = connectionEpochProvider;
        _sourceReaderProvider = sourceReaderProvider;
        _translationReaderProvider = translationReaderProvider;
        _startedTargetsProvider = startedTargetsProvider;
    }

    internal void StartEventMerge(int epoch)
    {
        EventDeliveryWriter writer;
        EventDeliveryState deliveryState;
        RealtimeTranslationOutputLanguage[] startedTargets;
        CancellationToken token;
        lock (_sync)
        {
            // Dispose 済み CTS へ触れないよう、Task 開始前に token を確定させる。
            writer = _eventBuffer.ArmMerge();
            token = _eventBuffer.MergeCts!.Token;
            deliveryState = _eventBuffer.DeliveryState;
            startedTargets = _startedTargetsProvider();
        }

        _eventBuffer.MergeTask = Task.Run(
            async () =>
            {
                // 原文 connection だけ input transcript を通し、翻訳側は接続フィルタと二重化する。
                var pumps = new List<Task>
                {
                    MergeOneAsync(_sourceReaderProvider(), writer, epoch, acceptInputTranscript: true, token),
                };
                // コンストラクタで用意した未使用 leftover lane は merge しない。
                // ForceClose が epoch を先に進めると merge が残りを読まず、
                // 完了済み Channel に残った訳文 / transport error が次世代へ混線する。
                pumps.AddRange(
                    startedTargets.Select(target =>
                        MergeOneAsync(
                            _translationReaderProvider(target),
                            writer,
                            epoch,
                            acceptInputTranscript: false,
                            token
                        )
                    )
                );

                await Task.WhenAll(pumps).ConfigureAwait(false);

                // 全接続のイベント流が終わったら購読側を解放する。
                if (_connectionEpochProvider() == epoch)
                {
                    writer.Complete();
                    deliveryState.CompleteNormally();
                }
            },
            CancellationToken.None
        );
    }

    private async Task MergeOneAsync(
        ChannelReader<RealtimeTranslationStreamEvent> reader,
        EventDeliveryWriter writer,
        int epoch,
        bool acceptInputTranscript,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await foreach (var streamEvent in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (_connectionEpochProvider() != epoch)
                {
                    return;
                }

                // MVP は翻訳音声を再生しない。念のため merge でも落とす（接続側のフィルタと二重化）。
                if (streamEvent.Event is RealtimeTranslationServerEvent.OutputAudioDelta)
                {
                    continue;
                }

                // 翻訳接続の input_transcript は原文 authority にしない。
                if (!acceptInputTranscript && streamEvent.Event is RealtimeTranslationServerEvent.InputTranscriptDelta)
                {
                    continue;
                }

                // Dual 側の epoch で貼り直し、接続内部の epoch と揃える。
                if (!writer.TryDeliver(streamEvent with { Epoch = epoch }))
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 停止時のキャンセルは正常終了として扱う。
        }
    }

    internal async Task StopEventMergeAsync()
    {
        CancellationTokenSource? cts;
        Task? mergeTask;
        EventDeliveryWriter? writer;
        EventDeliveryState deliveryState;
        ChannelWriter<RealtimeTranslationStreamEvent> eventsWriter;
        lock (_sync)
        {
            (cts, mergeTask, writer, deliveryState) = _eventBuffer.DetachMerge();
            eventsWriter = _eventBuffer.Writer;
        }

        if (cts is not null)
        {
            await cts.CancelAsync().ConfigureAwait(false);
            cts.Dispose();
        }

        if (mergeTask is not null)
        {
            try
            {
                await mergeTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // cancel 済みの merge は正常終了として扱う。
            }
        }

        if (writer is not null)
        {
            writer.Complete();
        }
        else
        {
            eventsWriter.TryComplete();
        }

        deliveryState.CompleteNormally();
    }
}
