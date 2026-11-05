using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RealtimeTranslator.Core.Realtime;

/// <summary>
/// ストリーミング中の定期ティック。字幕 tick・イベントロス再確認・
/// ヘルス検知の evaluate を <see cref="SessionRuntimeContext.TickInterval"/> 周期で回す。
/// 呼び出し側が渡す CancellationToken のスコープ（接続試行）で生きる。
/// </summary>
internal sealed class SessionTicker
{
    private readonly SessionRuntimeContext _context;

    internal SessionTicker(SessionRuntimeContext context)
    {
        _context = context;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_context.TickInterval, _context.TimeProvider, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            RealtimeSubtitleUpdate? update;
            var feed = _context.GetActiveFeed();
            IReadOnlyList<SessionHealthDetection> healthDetections;
            lock (_context.Sync)
            {
                healthDetections = _context.HealthBookkeeper.Tick(feed);
                if (feed is { DeliveryState.DidLoseEvents: true })
                {
                    update = null;
                }
                else
                {
                    update = _context.SubtitlePipeline.Tick();
                }
            }

            foreach (var detection in healthDetections)
            {
                _context.OnHealthDetected?.Invoke(detection);
            }

            if (feed is { DeliveryState.DidLoseEvents: true })
            {
                _context.SubtitlePipeline.HandleEventLoss(feed);
                continue;
            }

            if (update is { } value)
            {
                _context.SubtitlePipeline.EmitSubtitleUpdate(value);
                _context.BeforeRoutingResetForTests?.Invoke();
                await _context.AudioRouting.ResetForNextSegmentAsync().ConfigureAwait(false);
            }
        }
    }
}
