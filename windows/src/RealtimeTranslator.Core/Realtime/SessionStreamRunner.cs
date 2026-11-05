using System;
using System.Threading;
using System.Threading.Tasks;
using RealtimeTranslator.Core.Audio;
using RealtimeTranslator.Core.Localization;
using RealtimeTranslator.Core.OpenAI;

namespace RealtimeTranslator.Core.Realtime;

/// <summary>
/// 1 回の接続試行。handshake → feed/epoch 確定 → routing reset → 音声開始 →
/// Listening 遷移 → 音声/イベント/ティックのポンプ起動と first-result race を担う。
/// 試行の成否は例外で呼び出し側（SessionLoopRunner）へ返す。
/// </summary>
internal sealed class SessionStreamRunner
{
    private readonly SessionRuntimeContext _context;
    private readonly SessionAudioFeeder _audioFeeder;
    private readonly SessionEventConsumer _eventConsumer;
    private readonly SessionTicker _ticker;

    internal SessionStreamRunner(SessionRuntimeContext context)
    {
        _context = context;
        _audioFeeder = new SessionAudioFeeder(context);
        _eventConsumer = new SessionEventConsumer(context);
        _ticker = new SessionTicker(context);
    }

    public async Task RunAsync(int generation, CancellationToken cancellationToken)
    {
        // _context.DualClient.ReservedEpoch は Dual 側の lock を取るため _context.Sync 保持中には読まない。
        var entryAttemptEpoch = _context.DualClient.ReservedEpoch;
        lock (_context.Sync)
        {
            // RequireApiKey 失敗（missing key）も試行の終了診断へ乗せるため先に記録する。
            _context.HealthBookkeeper.BeginAttempt(_context.LifecycleGeneration, entryAttemptEpoch);
        }

        var apiKey = RequireApiKey();
        _context.SetState(TranslationState.Connecting);

        LanguagePair languagePair;
        lock (_context.Sync)
        {
            languagePair = _context.SessionLanguagePair ?? _context.LanguagePairProvider();
        }

        try
        {
            await _context
                .DualClient.StartAsync(
                    apiKey,
                    _context.TuningProvider().ForPair(languagePair),
                    languagePair,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch
        {
            // StartAsync は network 処理の前に connectionEpoch を予約済み。失敗した
            // handshake の epoch で診断するため読み直す。
            var reservedEpoch = _context.DualClient.ReservedEpoch;
            lock (_context.Sync)
            {
                _context.HealthBookkeeper.UpdateAttemptEpoch(reservedEpoch);
            }

            throw;
        }
        var reservedAfterStart = _context.DualClient.ReservedEpoch;
        lock (_context.Sync)
        {
            _context.HealthBookkeeper.UpdateAttemptEpoch(reservedAfterStart);
        }
        if (!_context.IsCurrentGeneration(generation))
        {
            await _context.DualClient.ForceCloseAsync().ConfigureAwait(false);
            return;
        }

        var feed = _context.DualClient.Feed;
        var epoch = feed.Epoch;
        // 再接続時 BeginNewEpoch は buffer を捨てる。idle finalize 前の完全ペアを
        // 先に確定しないと、オプトイン字幕記録へ ShouldFinalize が届かない。
        _context.SubtitlePipeline.FlushPendingFinalizeIfNeeded();
        lock (_context.Sync)
        {
            _context.HealthBookkeeper.UpdateAttemptEpoch(epoch);
            _context.SubtitlePipeline.BeginEpoch(epoch, languagePair);
            _context.ActiveFeed = feed;
        }

        await _context.DualClient.ResetAudioRoutingAsync().ConfigureAwait(false);
        await _context.AudioCapture.StartAsync(cancellationToken).ConfigureAwait(false);
        if (!_context.IsCurrentGeneration(generation))
        {
            await _context.AudioCapture.StopAsync().ConfigureAwait(false);
            await _context.DualClient.ForceCloseAsync().ConfigureAwait(false);
            return;
        }

        _context.SetState(TranslationState.Listening);
        _context.BeforeHealthGenerationBeginForTests?.Invoke();
        var staleGeneration = false;
        lock (_context.Sync)
        {
            if (generation != _context.LifecycleGeneration)
            {
                // SetState とこの lock の間に StopAsync が世代を進めた場合、
                // 止められた世代の monitor を再活性化しない。
                staleGeneration = true;
            }
            else
            {
                _context.ReconnectBudget.RecordListening();
                _context.HealthBookkeeper.BeginGeneration(generation, epoch, feed.DeliveryState);
            }
        }
        if (staleGeneration)
        {
            await _context.AudioCapture.StopAsync().ConfigureAwait(false);
            await _context.DualClient.ForceCloseAsync().ConfigureAwait(false);
            return;
        }

        using var streamCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var audioTask = _audioFeeder.RunAsync(generation, streamCts.Token);
        var consume = _eventConsumer.RunAsync(generation, feed, streamCts.Token);
        var tick = _ticker.RunAsync(streamCts.Token);

        var first = await Task.WhenAny(audioTask, consume).ConfigureAwait(false);
        await streamCts.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(SuppressCancellation(audioTask), SuppressCancellation(consume), SuppressCancellation(tick))
            .ConfigureAwait(false);
        await first.ConfigureAwait(false);
    }

    private static async Task SuppressCancellation(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
#pragma warning disable CA1031 // 敗者側の失敗は勝者側の結果で報告するため握り潰す。
        catch (Exception)
#pragma warning restore CA1031
        {
            // 勝者の例外だけを再接続判定に使う。
        }
    }

    private string RequireApiKey() => RealtimeApiKey.Require(_context.ApiKeyStore.Load());
}
