using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RealtimeTranslator.Core.OpenAI;

namespace RealtimeTranslator.Core.Realtime;

/// <summary>
/// 翻訳送信ポンプの帳簿。<see cref="TranslationFrameQueues"/> と
/// <see cref="TranslationPumpSupervisor"/> を所有し、enqueue・ポンプループ・
/// drain 待ち・transport error 発行を束ねる。
/// すべての状態は呼び出し側と共有する <c>_sync</c> 配下でのみ触れる
/// （独自ロックは持たない）。接続・epoch・実行中フラグ・merge writer は
/// provider 経由で lock 内で読む。
/// </summary>
internal sealed class TranslationSendPipeline
{
    private readonly object _sync;
    private readonly DualRealtimeTranslationClientTuning _clientTuning;
    private readonly TimeSpan _translationDrainTimeout;
    private readonly TranslationFrameQueues _queues;
    private readonly TranslationPumpSupervisor _pump;
    private readonly Func<RealtimeTranslationOutputLanguage, RealtimeTranslationConnection> _connectionProvider;
    private readonly Func<int> _connectionEpochProvider;
    private readonly Func<bool> _isRunningProvider;
    private readonly Func<EventDeliveryWriter?> _mergeWriterProvider;

    internal TranslationSendPipeline(
        object sync,
        DualRealtimeTranslationClientTuning clientTuning,
        TimeSpan translationDrainTimeout,
        Func<RealtimeTranslationOutputLanguage, RealtimeTranslationConnection> connectionProvider,
        Func<int> connectionEpochProvider,
        Func<bool> isRunningProvider,
        Func<EventDeliveryWriter?> mergeWriterProvider
    )
    {
        _sync = sync;
        _clientTuning = clientTuning;
        _translationDrainTimeout = translationDrainTimeout;
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

    internal TimeSpan ResolveCloseDrainTimeout()
    {
        int pending;
        lock (_sync)
        {
            pending = _queues.PendingCount;
            // 送信中の 1 frame も予算に含め、preroll 直後の Stop で足りなくならないようにする。
            if (_pump.IsTracked)
            {
                pending += 1;
            }
        }

        return _clientTuning.ResolveDrainTimeout(_translationDrainTimeout, pending);
    }

    /// <summary>翻訳ポンプが現在の待ち行列を処理し終えるまで待つ。決定的なテストのために使う。</summary>
    /// <remarks>送信が停滞しても timeout（既定5秒、Close時はpending比例）で打ち切る（ポンプTaskを無期限待ちしない）。</remarks>
    internal async Task WaitForTranslationDrainAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default
    )
    {
        var deadline = Environment.TickCount64 + (long)(timeout ?? TimeSpan.FromSeconds(5)).TotalMilliseconds;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Task? pump;
            lock (_sync)
            {
                if (!_pump.IsTracked && _queues.PendingCount == 0)
                {
                    return;
                }

                pump = _pump.PumpTask;
            }

            var remainingMs = deadline - Environment.TickCount64;
            if (remainingMs <= 0)
            {
                lock (_sync)
                {
                    if (!_pump.IsTracked && _queues.PendingCount == 0)
                    {
                        return;
                    }
                }

                throw new TimeoutException("translation pump did not drain");
            }

            if (pump is null)
            {
                await Task.Yield();
                continue;
            }

            // ポンプ完了とdeadlineを競わせ、停滞したsendで無期限待ちにしない。
            var delay = Task.Delay((int)Math.Min(remainingMs, int.MaxValue), cancellationToken);
            var completed = await Task.WhenAny(pump, delay).ConfigureAwait(false);
            if (completed != pump)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // timeout と完了が競合したとき、すでに空なら成功扱いにする。
                lock (_sync)
                {
                    if (!_pump.IsTracked && _queues.PendingCount == 0)
                    {
                        return;
                    }
                }

                throw new TimeoutException("translation pump did not drain");
            }

            await pump.ConfigureAwait(false);
        }
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
