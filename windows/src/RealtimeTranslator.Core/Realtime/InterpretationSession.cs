using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using RealtimeTranslator.Core.Audio;
using RealtimeTranslator.Core.OpenAI;

namespace RealtimeTranslator.Core.Realtime;

public enum TranslationState
{
    Idle,
    Connecting,
    Listening,
    Reconnecting,
    Closing,
    Error,
}

/// <summary>BYOK の API キー取得。保管先 (Credential Manager 等) は Windows 層が実装する。</summary>
public interface IApiKeyStore
{
    string? Load();
}

/// <summary>24kHz / PCM16 / mono / 100ms frame を供給する録音源。</summary>
public interface IRealtimeAudioCapture
{
    ChannelReader<CapturedAudioFrame> Frames { get; }

    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync();
}

/// <summary>
/// 録音・3 接続・字幕組み立てを束ねるセッション。UI 非依存。
/// ライフサイクルと正常停止プロトコルを担い、再接続ループ・接続試行・
/// 各ポンプは <see cref="SessionRuntimeContext"/> 経由のランナーへ委譲する。
/// </summary>
public sealed class InterpretationSession : IDisposable
{
    /// <summary><see cref="ReconnectPolicy.Default"/> と同じ値。fixture との整合はテストで確認する。</summary>
    public const int MaxReconnectAttempts = 5;

    private readonly SessionRuntimeContext _context;
    private readonly SessionLoopRunner _loopRunner;

    private CancellationTokenSource? _sessionCts;
    private Task? _sessionTask;

    /// <summary>進行中の Stop。二重 Stop を macOS の stopTask と同様に合流させる。</summary>
    private Task? _stopTask;

    /// <summary>テスト用。フックは Context に直接設定する。</summary>
    internal SessionRuntimeContext ContextForTests => _context;

    public InterpretationSession(
        IApiKeyStore apiKeyStore,
        IRealtimeAudioCapture audioCapture,
        IDualRealtimeTranslationClient dualClient,
        Func<RealtimeSessionTuning>? tuningProvider = null,
        TimeProvider? timeProvider = null,
        TimeSpan? initialReconnectDelay = null,
        TimeSpan? tickInterval = null,
        Func<LanguagePair>? languagePairProvider = null,
        ReconnectPolicy? reconnectPolicy = null,
        Func<TimeSpan, TimeSpan>? reconnectJitter = null,
        SessionHealthThresholds? healthThresholds = null
    )
    {
        ArgumentNullException.ThrowIfNull(apiKeyStore);
        ArgumentNullException.ThrowIfNull(audioCapture);
        ArgumentNullException.ThrowIfNull(dualClient);

        _context = new SessionRuntimeContext(
            apiKeyStore,
            audioCapture,
            dualClient,
            tuningProvider ?? (() => RealtimeSessionTuning.Default),
            timeProvider ?? TimeProvider.System,
            initialReconnectDelay,
            tickInterval,
            languagePairProvider ?? (() => LanguagePair.JaEn),
            reconnectPolicy,
            reconnectJitter,
            healthThresholds
        );
        _context.OnStateChanged = state => StateChanged?.Invoke(this, state);
        _context.OnSubtitleUpdated = update => SubtitleUpdated?.Invoke(this, update);
        _context.OnMessageEncountered = message => MessageEncountered?.Invoke(this, message);
        _context.OnHealthDetected = detection => HealthDetected?.Invoke(this, detection);
        _loopRunner = new SessionLoopRunner(_context);
    }

    public event EventHandler<TranslationState>? StateChanged;

    public event EventHandler<RealtimeSubtitleUpdate>? SubtitleUpdated;

    /// <summary>ユーザー向け文言。サーバー文言は必ずサニタイズ済みのものを渡す。</summary>
    public event EventHandler<string>? MessageEncountered;

    /// <summary>受信停止監視の検知（診断のみ）。content は含まない。</summary>
    public event EventHandler<SessionHealthDetection>? HealthDetected;

    /// <summary>テスト・診断用の最新 snapshot（検知には使わない）。</summary>
    public SessionHealthSnapshot? LatestHealthSnapshot => _context.HealthBookkeeper.LatestSnapshot;

    /// <summary>テスト・診断用の最新 termination diagnostic（各試行で最大 1 件）。</summary>
    public SessionTerminationDiagnostic? LatestTerminationDiagnostic => _context.HealthBookkeeper.LatestTermination;

    public TranslationState State => _context.State;

    public AudioLossMetrics AudioLossMetrics
    {
        get
        {
            lock (_context.Sync)
            {
                return _context.SubtitlePipeline.AudioLossMetrics;
            }
        }
    }

    public async Task StartAsync()
    {
        Task? previous = null;
        CancellationTokenSource? previousCts = null;
        // await をまたぐ再入を防ぐため、受理と同時に Connecting へ進める。
        // 旧世代の task/cts は受理と同じロック内で取り出す（Stop が先に null 化しないように）。
        if (
            !_context.TryBeginConnecting(() =>
            {
                previous = _sessionTask;
                previousCts = _sessionCts;
            })
        )
        {
            return;
        }

        // 旧世代の teardown が新しい接続や録音を落とさないよう、先に排水する。
        if (previousCts is not null)
        {
            await previousCts.CancelAsync().ConfigureAwait(false);
        }

        if (previous is not null)
        {
            try
            {
                await previous.ConfigureAwait(false);
            }
#pragma warning disable CA1031 // 旧世代の失敗があっても新しいセッション開始を妨げない。
            catch (Exception)
#pragma warning restore CA1031
            {
                // 旧 session task の例外はここで吸収する。
            }
        }

        int generation;
        CancellationTokenSource cts = new();
        var token = cts.Token;
        lock (_context.Sync)
        {
            previousCts?.Dispose();
            _context.AdvanceLifecycleGeneration();
            generation = _context.LifecycleGeneration;
            _context.HealthBookkeeper.BeginRecordingGeneration();
            _context.ReconnectBudget.Reset();
            _context.SubtitlePipeline.ResetAudioLoss();
            // 録音開始時点のペアを世代全体で固定する。録音中の設定変更は再接続でも反映しない
            // （VALIDATION: 停止→次の録音開始後にだけ新しいペアが反映される）。
            _context.SessionLanguagePair = _context.LanguagePairProvider();
            _sessionCts = cts;
        }

        // 排水の await 中に Stop が Closing / Idle へ進めた場合だけ Connecting へ戻して通知する（通常は重複で no-op）。
        _context.SetState(TranslationState.Connecting);
        var sessionTask = Task.Run(() => _loopRunner.RunAsync(generation, token), CancellationToken.None);
        lock (_context.Sync)
        {
            _sessionTask = sessionTask;
        }
    }

    public Task StopAsync()
    {
        lock (_context.Sync)
        {
            if (_context.State == TranslationState.Idle)
            {
                return Task.CompletedTask;
            }

            // Closing 中の再入は進行中 Stop へ合流する。
            // Idle 以外で都度 CloseGracefully すると、先に Idle へ戻ったあと
            // 後続の ForceClose が次の録音の WebSocket を落とす。
            if (_stopTask is { } inFlight)
            {
                return inFlight;
            }

            _stopTask = StopCoreAsync();
            return _stopTask;
        }
    }

    private async Task StopCoreAsync()
    {
        // StopAsync が _context.Sync を握ったまま同期実行しないよう、一度外へ出す。
        await Task.Yield();

        try
        {
            Task? sessionTask;
            CancellationTokenSource? cts;
            lock (_context.Sync)
            {
                if (_context.State == TranslationState.Idle)
                {
                    return;
                }

                _context.AdvanceLifecycleGeneration();
                sessionTask = _sessionTask;
                _sessionTask = null;
                cts = _sessionCts;
                _sessionCts = null;
            }

            _context.RecordHealthTermination(SessionTerminationKind.UserStopped);
            _context.SetState(TranslationState.Closing);

            // 先に音声と session consumer を止め、close drain イベントを破棄されないようにする。
            // generation を上げたまま consumer が生きていると、commit/session.close の
            // 最終 delta を読んで捨ててしまい、オプトイン字幕記録が欠ける。
            await _context.AudioCapture.StopAsync().ConfigureAwait(false);
            if (cts is not null)
            {
                await cts.CancelAsync().ConfigureAwait(false);
            }

            if (sessionTask is not null)
            {
                try
                {
                    await sessionTask.ConfigureAwait(false);
                }
#pragma warning disable CA1031 // stop 中の旧世代失敗は Idle 遷移を妨げない。
                catch (Exception)
#pragma warning restore CA1031
                {
                    // session loop の例外は停止完了を阻まない。
                }
            }

            cts?.Dispose();

            try
            {
                await _context.DualClient.CloseGracefullyAsync().ConfigureAwait(false);
            }
#pragma warning disable CA1031 // graceful close が失敗しても force close で必ず解放する。
            catch (Exception)
#pragma warning restore CA1031
            {
                await _context.DualClient.ForceCloseAsync().ConfigureAwait(false);
            }

            // commit / session.close 中に届いた最終 delta を assembler へ取り込む。
            await _context.SubtitlePipeline.IngestStopDrainEventsAsync().ConfigureAwait(false);

            // 停止時点で完全ペアが残っていれば確定して見せる（オプトイン字幕記録も含む）。
            _context.SubtitlePipeline.FlushPendingFinalizeIfNeeded();

            lock (_context.Sync)
            {
                _context.SessionLanguagePair = null;
                _context.SubtitlePipeline.DeactivateLanguagePair();
            }

            _context.SetState(TranslationState.Idle);
        }
        finally
        {
            lock (_context.Sync)
            {
                _stopTask = null;
            }
        }
    }

    /// <summary>録音中の prompt/keywords/delay 変更を原文接続へ反映する。</summary>
    public async Task ApplyTuningChangeAsync()
    {
        if (_context.State != TranslationState.Listening)
        {
            return;
        }

        try
        {
            LanguagePair? activePair;
            lock (_context.Sync)
            {
                activePair = _context.SubtitlePipeline.ActiveLanguagePair;
            }

            await _context
                .DualClient.UpdateTranscriptionTuningAsync(
                    _context.TuningProvider().ForPair(activePair ?? LanguagePair.JaEn)
                )
                .ConfigureAwait(false);
        }
        catch (RealtimeTranslationException)
        {
            // 反映失敗は録音を止めるほどではない。次の再接続で新しい tuning が乗る。
        }
    }

    public void Dispose()
    {
        // OnExit / プロセス終了は StopAsync を経由しないことがある。
        // flush より先に generation を進め CTS を切って取り込みをフェンスし、
        // その時点の assembler 状態だけを ShouldFinalize する。
        CancellationTokenSource? cts;
        lock (_context.Sync)
        {
            _context.AdvanceLifecycleGeneration();
            cts = _sessionCts;
            _sessionCts = null;
        }

        if (cts is not null)
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 二重 Dispose は無視する。
            }

            cts.Dispose();
        }

        try
        {
            _context.SubtitlePipeline.FlushPendingFinalizeIfNeeded();
        }
#pragma warning disable CA1031 // Dispose 経路では例外を外へ出さない。
        catch (Exception)
#pragma warning restore CA1031
        {
            // flush 失敗でも破棄完了は継続する。
        }

        _context.AudioRouting.Dispose();
    }
}
