using System;
using System.Threading;
using System.Threading.Tasks;
using RealtimeTranslator.Core.Localization;
using RealtimeTranslator.Core.OpenAI;

namespace RealtimeTranslator.Core.Realtime;

/// <summary>
/// セッションループ。接続試行を繰り返し、失敗を回復可否で分類して
/// 再接続（backoff + teardown）か致命エラー（error 遷移）へ振り分ける。
/// 試行間の強制 teardown <see cref="TearDownStreamingAsync"/> もここが担う。
/// </summary>
internal sealed class SessionLoopRunner
{
    private readonly SessionRuntimeContext _context;
    private readonly SessionStreamRunner _streamRunner;

    internal SessionLoopRunner(SessionRuntimeContext context)
    {
        _context = context;
        _streamRunner = new SessionStreamRunner(context);
    }

    public async Task RunAsync(int generation, CancellationToken cancellationToken)
    {
        while (_context.IsCurrentGeneration(generation) && !cancellationToken.IsCancellationRequested)
        {
            Exception? recoverableError = null;
            try
            {
                await _streamRunner.RunAsync(generation, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (RealtimeTranslationException error) when (!error.IsRecoverable)
            {
                if (!_context.IsCurrentGeneration(generation))
                {
                    // stop 中の teardown が起こした失敗を、ユーザー向けエラーに昇格させない。
                    return;
                }

                _context.RecordHealthTermination(SessionTerminationKindMapping.FromException(error));
                await TearDownStreamingAsync().ConfigureAwait(false);
                // epoch を捨てる前に完全ペアを確定し、オプトイン字幕記録へ渡す。
                _context.SubtitlePipeline.FlushPendingFinalizeIfNeeded();
                _context.EnterError(error.Message);
                return;
            }
#pragma warning disable CA1031 // 想定外の失敗でも session task を落とさず再接続へ倒す。
            catch (Exception error)
#pragma warning restore CA1031
            {
                // recoverable transport failure / 音声デバイス失敗。予算判定が wait を
                // 返す場合だけ終了診断として記録する（予算超過ならそちらの kind で記録）。
                recoverableError = error;
            }

            if (!_context.IsCurrentGeneration(generation) || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            ReconnectDecision decision;
            lock (_context.Sync)
            {
                decision = _context.ReconnectBudget.RecordFailure();
            }

            if (decision.Kind != ReconnectDecisionKind.Wait)
            {
                _context.RecordHealthTermination(
                    decision.Kind == ReconnectDecisionKind.BudgetExhausted
                        ? SessionTerminationKind.ReconnectBudgetExhausted
                        : SessionTerminationKind.ReconnectAttemptLimit
                );
                await TearDownStreamingAsync().ConfigureAwait(false);
                _context.SubtitlePipeline.FlushPendingFinalizeIfNeeded();
                _context.EnterError(
                    UserCopy.Current.Text(
                        decision.Kind == ReconnectDecisionKind.BudgetExhausted
                            ? "error.reconnectBudgetExhausted"
                            : "error.reconnectLimit"
                    )
                );
                return;
            }

            // recoverable 失敗も終了診断として記録する（診断のみ、挙動は変えない）。
            if (recoverableError is { } recoverable)
            {
                _context.RecordHealthTermination(SessionTerminationKindMapping.FromException(recoverable));
            }

            _context.SetState(TranslationState.Reconnecting);
            await TearDownStreamingAsync().ConfigureAwait(false);

            try
            {
                await Task.Delay(decision.Delay, _context.TimeProvider, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task TearDownStreamingAsync()
    {
        // consumer が ServerError で抜けたあとの未読 delta を先に取り込む。
        // Complete 後の Stop drain だけに頼ると、再接続 Start が channel を
        // 張り替えたときに訳文が消える。
        var feed = _context.GetActiveFeed();
        if (feed is { DeliveryState.DidLoseEvents: true })
        {
            _context.SubtitlePipeline.HandleEventLoss(feed);
        }

        _context.SubtitlePipeline.IngestAlreadyQueuedEvents();

        lock (_context.Sync)
        {
            // recoverable・正常終了を問わず接続 teardown で健康世代を閉じる。
            _context.HealthBookkeeper.EndGeneration();
        }

        try
        {
            await _context.AudioCapture.StopAsync().ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await _context.DualClient.ForceCloseAsync().ConfigureAwait(false);
            }
            finally
            {
                // merge pump 停止後に connection から遅れて乗った delta も回収する。
                // StartAsync が channel を差し替える前に読まないと消える。
                // ForceClose 中の遅延 source でも言語境界を分割できるよう、
                // ペアと tracker は二度目の回収が終わるまで残す。通信先は変えない。
                _context.SubtitlePipeline.IngestAlreadyQueuedEvents();
                if (feed is { DeliveryState.HasPendingSourceFailure: true })
                {
                    _context.SubtitlePipeline.DiscardFailedSourceIfNeeded(feed);
                }
                lock (_context.Sync)
                {
                    _context.SubtitlePipeline.DeactivateLanguagePair();
                    _context.SubtitlePipeline.ClearBoundaryCandidate();
                }
            }
        }
    }
}
