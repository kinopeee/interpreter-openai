using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RealtimeTranslator.Core.OpenAI;

namespace RealtimeTranslator.Core.Realtime;

/// <summary>
/// 翻訳送信ポンプの帳簿。<see cref="TranslationFrameQueues"/> と
/// <see cref="TranslationPumpSupervisor"/> を所有し、enqueue・ポンプループ・
/// transport error 発行を束ねる。drain 待ちと close drain 予算は
/// <see cref="DualClientShutdownCoordinator"/> が持つ。
/// すべての状態は呼び出し側と共有する <c>_sync</c> 配下でのみ触れる
/// （独自ロックは持たない）。接続・epoch・実行中フラグ・merge writer は
/// provider 経由で lock 内で読む。
/// </summary>
internal sealed class TranslationSendPipeline
{
    private readonly object _sync;
    private readonly TranslationFrameQueues _queues;
    private readonly TranslationPumpSupervisor _pump;
    private readonly Func<RealtimeTranslationOutputLanguage, RealtimeTranslationConnection> _connectionProvider;
    private readonly Func<int> _connectionEpochProvider;
    private readonly Func<bool> _isRunningProvider;
    private readonly Func<EventDeliveryWriter?> _mergeWriterProvider;

    internal TranslationSendPipeline(
        object sync,
        DualRealtimeTranslationClientTuning clientTuning,
        Func<RealtimeTranslationOutputLanguage, RealtimeTranslationConnection> connectionProvider,
        Func<int> connectionEpochProvider,
        Func<bool> isRunningProvider,
        Func<EventDeliveryWriter?> mergeWriterProvider
    )
    {
        _sync = sync;
        _queues = new TranslationFrameQueues(clientTuning);
        _pump = new TranslationPumpSupervisor(clientTuning);
        _connectionProvider = connectionProvider;
        _connectionEpochProvider = connectionEpochProvider;
        _isRunningProvider = isRunningProvider;
        _mergeWriterProvider = mergeWriterProvider;
    }

    internal int PendingCount => _queues.PendingCount;

    internal bool HaltedForTransportFailure => _pump.HaltedForTransportFailure;

    internal bool IsTracked => _pump.IsTracked;

    internal Task? PumpTask => _pump.PumpTask;

    internal CancellationTokenSource Cancellation => _pump.Cancellation;

    internal IEnumerable<ReadOnlyMemory<byte>> PrerollFrames => _queues.PrerollFrames;

    internal void Reset() => _pump.Reset();

    internal void RecycleCancellation() => _pump.RecycleCancellation();

    internal void ResetFailures() => _pump.ResetFailures();

    internal void ClearAll() => _queues.ClearAll();

    internal void ClearPending() => _queues.ClearPending();

    internal void AppendPreroll(ReadOnlyMemory<byte> frame) => _queues.AppendPreroll(frame);

    internal (Task? PumpTask, CancellationTokenSource Cancellation) Detach() => _pump.Detach();

    internal bool TryEnqueueTranslationFrameLocked(ReadOnlyMemory<byte> frame, RealtimeTranslationOutputLanguage target)
    {
        // transport failure 後は enqueue 自体を止め、ポンプ再起動の隙を残さない。
        if (_pump.HaltedForTransportFailure)
        {
            return true;
        }

        if (!_queues.HasPendingCapacity)
        {
            _pump.HaltForTransportFailure();
            _queues.ClearPending();
            return false;
        }

        _queues.EnqueuePending(frame, target);
        if (!_pump.IsTracked)
        {
            _pump.Start(Task.Run(PumpTranslationFramesAsync, CancellationToken.None));
        }

        return true;
    }

    private async Task PumpTranslationFramesAsync()
    {
        CancellationToken pumpToken;
        int pumpEpoch;
        int generation;
        lock (_sync)
        {
            pumpToken = _pump.Cancellation.Token;
            pumpEpoch = _connectionEpochProvider();
            generation = _pump.Generation;
        }

        while (true)
        {
            PendingTranslationFrame pending;
            lock (_sync)
            {
                if (!_isRunningProvider() || _pump.HaltedForTransportFailure || _queues.PendingCount == 0)
                {
                    _pump.FinishIfCurrent(generation);
                    return;
                }

                pending = _queues.DequeuePending();
            }

            try
            {
                var connection = _connectionProvider(pending.Target);
                await connection.AppendAudioFrameAsync(pending.Frame, pumpToken).ConfigureAwait(false);

                lock (_sync)
                {
                    if (!_pump.HaltedForTransportFailure && _connectionEpochProvider() == pumpEpoch)
                    {
                        _pump.ResetFailures();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                lock (_sync)
                {
                    _pump.FinishIfCurrent(generation);
                }

                return;
            }
#pragma warning disable CA1031 // 送信失敗の種類に関わらず連続失敗として数え、上限で transport error を出す。
            catch (Exception)
#pragma warning restore CA1031
            {
                bool halted;
                int epoch;
                lock (_sync)
                {
                    if (_pump.HaltedForTransportFailure || _connectionEpochProvider() != pumpEpoch)
                    {
                        _pump.FinishIfCurrent(generation);
                        return;
                    }

                    _pump.RecordFailure();
                    halted = _pump.ReachedFailureLimit;
                    epoch = _connectionEpochProvider();
                    if (halted)
                    {
                        // 再接続待ちの間、死にかけの socket へ送り続けない。
                        _pump.HaltForTransportFailure();
                        _queues.ClearPending();
                    }
                }

                if (halted)
                {
                    // drain 待ちが復帰する前に transport error を確実に発行する。
                    PublishTransportError(pending.Target, epoch, DualRealtimeTranslationClient.TransportErrorMessage);
                    lock (_sync)
                    {
                        _pump.FinishIfCurrent(generation);
                    }

                    return;
                }
            }
        }
    }

    internal void PublishTransportError(RealtimeTranslationOutputLanguage target, int epoch, string message)
    {
        EventDeliveryWriter? writer;
        lock (_sync)
        {
            writer = _mergeWriterProvider();
        }

        writer?.TryDeliver(
            new RealtimeTranslationStreamEvent(
                target,
                new RealtimeTranslationServerEvent.ServerError(
                    message,
                    DualRealtimeTranslationClient.TransportErrorCode
                ),
                epoch
            )
        );
    }

    internal static async Task AwaitPumpAsync(Task? pump)
    {
        if (pump is null)
        {
            return;
        }

        try
        {
            await pump.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 停止時のキャンセルは正常終了として扱う。
        }
    }
}
