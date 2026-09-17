using System;
using System.Threading;
using System.Threading.Tasks;
using RealtimeTranslator.Core.OpenAI;

namespace RealtimeTranslator.Core.Realtime;

/// <summary>
/// 音声ルーティング操作の直列化。<c>_routingGate</c> で transport 向けの
/// routing 変更（select/switch/next-segment/audio-loss reset）を 1 件ずつ通す。
/// <c>InterpretationSession</c> の <c>_sync</c> ロック契約を引き継ぎ、独自ロックは持たない。
/// </summary>
internal sealed class AudioRoutingCoordinator : IDisposable
{
    private readonly SemaphoreSlim _routingGate = new(1, 1);

    private readonly object _sync;
    private readonly IDualRealtimeTranslationClient _dualClient;
    private readonly SessionSubtitlePipeline _subtitlePipeline;
    private readonly SessionHealthBookkeeper _healthBookkeeper;

    public AudioRoutingCoordinator(
        object sync,
        IDualRealtimeTranslationClient dualClient,
        SessionSubtitlePipeline subtitlePipeline,
        SessionHealthBookkeeper healthBookkeeper)
    {
        ArgumentNullException.ThrowIfNull(sync);
        ArgumentNullException.ThrowIfNull(dualClient);
        ArgumentNullException.ThrowIfNull(subtitlePipeline);
        ArgumentNullException.ThrowIfNull(healthBookkeeper);
        _sync = sync;
        _dualClient = dualClient;
        _subtitlePipeline = subtitlePipeline;
        _healthBookkeeper = healthBookkeeper;
    }

    public void Dispose()
    {
        // `_routingGate` は同期 Dispose では破棄しない。
        // in-flight の Update/ResetAudioRouting が Wait/Release 中に ObjectDisposedException へ
        // 落ちないようにする。StopAsync 後は参照が切れ、SemaphoreSlim は GC で回収される
        // （AvailableWaitHandle 未使用）。
    }

    public async Task ResetForNextSegmentAsync()
    {
        await _routingGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await ResetForNextSegmentCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _routingGate.Release();
        }
    }

    /// <param name="hook">テスト差し込み（<c>BeforeAudioLossRoutingResetForTests</c>）。</param>
    public async Task ResetAfterAudioLossAsync(Func<Task>? hook)
    {
        await _routingGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (hook is not null)
            {
                await hook().ConfigureAwait(false);
            }

            bool skip;
            lock (_sync)
            {
                skip = _subtitlePipeline.HasSelectedTranslationTarget;
            }

            if (!skip)
            {
                await _dualClient.ResetAudioRoutingAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _routingGate.Release();
        }
    }

    public async Task ApplyAsync(
        RealtimeSubtitleRoutingAction action,
        CancellationToken cancellationToken)
    {
        await _routingGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            switch (action)
            {
                case RealtimeSubtitleRoutingAction.Select select:
                    lock (_sync)
                    {
                        _healthBookkeeper.SetSelectedLane(
                            select.Target is { } selectTarget
                                ? RealtimeTranslationLane.Translation(selectTarget)
                                : null);
                    }

                    await _dualClient.SelectTranslationTargetAsync(
                        select.Target,
                        cancellationToken).ConfigureAwait(false);
                    break;
                case RealtimeSubtitleRoutingAction.Switch @switch:
                    lock (_sync)
                    {
                        _healthBookkeeper.SetSelectedLane(
                            @switch.Target is { } switchTarget
                                ? RealtimeTranslationLane.Translation(switchTarget)
                                : null);
                    }

                    await _dualClient.ResetAudioRoutingAsync().ConfigureAwait(false);
                    await _dualClient.SelectTranslationTargetAsync(
                        @switch.Target,
                        cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
        finally
        {
            _routingGate.Release();
        }
    }

    private async Task ResetForNextSegmentCoreAsync()
    {
        bool skip;
        lock (_sync)
        {
            skip = _subtitlePipeline.CurrentSourceLength > 0;
            if (!skip)
            {
                _subtitlePipeline.ResetRoutingForNextSegment();
            }
        }

        if (skip)
        {
            return;
        }

        lock (_sync)
        {
            _healthBookkeeper.SetSelectedLane(null);
        }

        await _dualClient.ResetAudioRoutingAsync().ConfigureAwait(false);
    }
}
