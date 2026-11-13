using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using RealtimeTranslator.Core.OpenAI;

namespace RealtimeTranslator.Core.Realtime;

/// <summary>
/// <see cref="DualRealtimeTranslationClient"/> の終了・ドレイン処理。
/// 正常停止（未送信翻訳 frame の drain → 全接続 close → merge 停止）、ForceClose、
/// Dispose の後始末、drain 待ち、close drain 予算の解決を束ねる。
/// 状態はクライアントと共有する <c>_sync</c> 配下でのみ触れる（独自ロックは持たない）。
/// 実行中フラグ・選択 target・epoch はクライアントが持ち、lock 内で delegate 経由で読み書きする。
/// </summary>
internal sealed class DualClientShutdownCoordinator
{
    private readonly object _sync;
    private readonly RealtimeSourceTranscriptionConnection _sourceConnection;
    private readonly Dictionary<RealtimeTranslationOutputLanguage, RealtimeTranslationConnection> _connections;
    private readonly DualRealtimeTranslationClientTuning _clientTuning;
    private readonly TimeSpan _translationDrainTimeout;
    private readonly SemaphoreSlim _lifecycleGate;
    private readonly TranslationSendPipeline _sendPipeline;
    private readonly MergedEventBuffer _eventBuffer;
    private readonly EventMergePump _mergePump;
    private readonly Func<bool> _isRunningProvider;
    private readonly Action _markStoppedLocked;
    private readonly Action _clearRoutingLocked;
    private readonly Action _advanceEpochLocked;

    internal DualClientShutdownCoordinator(
        object sync,
        RealtimeSourceTranscriptionConnection sourceConnection,
        Dictionary<RealtimeTranslationOutputLanguage, RealtimeTranslationConnection> connections,
        DualRealtimeTranslationClientTuning clientTuning,
        TimeSpan translationDrainTimeout,
        SemaphoreSlim lifecycleGate,
        TranslationSendPipeline sendPipeline,
        MergedEventBuffer eventBuffer,
        EventMergePump mergePump,
        Func<bool> isRunningProvider,
        Action markStoppedLocked,
        Action clearRoutingLocked,
        Action advanceEpochLocked
    )
    {
        _sync = sync;
        _sourceConnection = sourceConnection;
        _connections = connections;
        _clientTuning = clientTuning;
        _translationDrainTimeout = translationDrainTimeout;
        _lifecycleGate = lifecycleGate;
        _sendPipeline = sendPipeline;
        _eventBuffer = eventBuffer;
        _mergePump = mergePump;
        _isRunningProvider = isRunningProvider;
        _markStoppedLocked = markStoppedLocked;
        _clearRoutingLocked = clearRoutingLocked;
        _advanceEpochLocked = advanceEpochLocked;
    }

    internal async Task CloseGracefullyAsync(CancellationToken cancellationToken)
    {
        ChannelWriter<RealtimeTranslationStreamEvent>? idleWriter = null;
        lock (_sync)
        {
            if (!_isRunningProvider())
            {
                // Start 前や ForceClose 後でも Events を完了させる。
                // 未完了のままだと InterpretationSession の stop drain が
                // WaitToReadAsync で Closing に固まり、次の録音を開始できない。
                idleWriter = _eventBuffer.Writer;
            }
        }

        if (idleWriter is not null)
        {
            idleWriter.TryComplete();
            return;
        }

        // 未送信の翻訳フレームを先に送り、停止時の訳文欠落を防ぐ。
        // preroll flush 直後は待ち行列が長いので pending 数に応じて予算を伸ばす。
        // 送信が長時間停滞しても cap で close 自体は進める。
        try
        {
            await WaitForTranslationDrainAsync(ResolveCloseDrainTimeout(), cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // drain できなくても session.close へ進む。
        }
        catch (OperationCanceledException)
        {
            throw;
        }

        Task? pump;
        CancellationTokenSource pumpCts;
        lock (_sync)
        {
            if (!_isRunningProvider())
            {
                return;
            }

            _markStoppedLocked();
            _sendPipeline.ClearPending();
            (pump, pumpCts) = _sendPipeline.Detach();
        }

        await pumpCts.CancelAsync().ConfigureAwait(false);
        await TranslationSendPipeline.AwaitPumpAsync(pump).ConfigureAwait(false);

        Exception? firstError = null;
        try
        {
            await Task.WhenAll(
                    new[] { _sourceConnection.CloseGracefullyAsync(cancellationToken) }.Concat(
                        _connections.Values.Select(connection => connection.CloseGracefullyAsync(cancellationToken))
                    )
                )
                .ConfigureAwait(false);
        }
#pragma warning disable CA1031 // 最初の close 失敗だけを呼び出し元へ返し、残りの解放は必ず行う。
        catch (Exception error)
#pragma warning restore CA1031
        {
            firstError = error;
        }

        await _mergePump.StopEventMergeAsync().ConfigureAwait(false);

        if (firstError is not null)
        {
            throw firstError;
        }
    }

    internal async Task ForceCloseAsync()
    {
        Task? pump;
        CancellationTokenSource pumpCts;
        lock (_sync)
        {
            _markStoppedLocked();
            _clearRoutingLocked();
            _sendPipeline.ClearAll();
            _sendPipeline.Reset();
            _advanceEpochLocked();
            (pump, pumpCts) = _sendPipeline.Detach();
        }

        await pumpCts.CancelAsync().ConfigureAwait(false);
        await TranslationSendPipeline.AwaitPumpAsync(pump).ConfigureAwait(false);

        Exception? firstError = null;
        foreach (
            var close in new Func<Task>[] { _sourceConnection.ForceCloseAsync }.Concat(
                _connections.Values.Select(connection => (Func<Task>)connection.ForceCloseAsync)
            )
        )
        {
            try
            {
                await close().ConfigureAwait(false);
            }
#pragma warning disable CA1031 // 最初の失敗だけを返し、残りの接続と merge は必ず解放する。
            catch (Exception error)
#pragma warning restore CA1031
            {
                firstError ??= error;
            }
        }

        await _mergePump.StopEventMergeAsync().ConfigureAwait(false);

        if (firstError is not null)
        {
            throw firstError;
        }
    }

    internal void Dispose()
    {
        CancellationTokenSource? mergeCts;
        CancellationTokenSource pumpCts;
        lock (_sync)
        {
            _markStoppedLocked();
            mergeCts = _eventBuffer.DetachMergeCts();
            pumpCts = _sendPipeline.Cancellation;
        }

        // Dispose 経路でも背景タスクを止める。Cancel せず Dispose だけだと loop が残る。
        try
        {
            mergeCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 二重 Dispose は無視する。
        }

        mergeCts?.Dispose();

        try
        {
            pumpCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 二重 Dispose は無視する。
        }

        pumpCts.Dispose();

        _lifecycleGate.Dispose();
        _sourceConnection.Dispose();
        foreach (var connection in _connections.Values.Distinct())
        {
            connection.Dispose();
        }
    }

    internal TimeSpan ResolveCloseDrainTimeout()
    {
        int pending;
        lock (_sync)
        {
            pending = _sendPipeline.PendingCount;
            // 送信中の 1 frame も予算に含め、preroll 直後の Stop で足りなくならないようにする。
            if (_sendPipeline.IsTracked)
            {
                pending += 1;
            }
        }

        return _clientTuning.ResolveDrainTimeout(_translationDrainTimeout, pending);
    }

    /// <summary>翻訳ポンプが現在の待ち行列を処理し終えるまで待つ。</summary>
    /// <remarks>送信が停滞しても timeout（既定5秒、Close時はpending比例）で打ち切る（ポンプTaskを無期限待ちしない）。</remarks>
    internal async Task WaitForTranslationDrainAsync(TimeSpan? timeout, CancellationToken cancellationToken)
    {
        var deadline = Environment.TickCount64 + (long)(timeout ?? TimeSpan.FromSeconds(5)).TotalMilliseconds;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Task? pump;
            lock (_sync)
            {
                if (!_sendPipeline.IsTracked && _sendPipeline.PendingCount == 0)
                {
                    return;
                }

                pump = _sendPipeline.PumpTask;
            }

            var remainingMs = deadline - Environment.TickCount64;
            if (remainingMs <= 0)
            {
                lock (_sync)
                {
                    if (!_sendPipeline.IsTracked && _sendPipeline.PendingCount == 0)
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
                    if (!_sendPipeline.IsTracked && _sendPipeline.PendingCount == 0)
                    {
                        return;
                    }
                }

                throw new TimeoutException("translation pump did not drain");
            }

            await pump.ConfigureAwait(false);
        }
    }
}
