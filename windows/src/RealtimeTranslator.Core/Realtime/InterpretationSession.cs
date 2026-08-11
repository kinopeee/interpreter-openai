using System;
using System.Collections.Generic;
using System.Text;
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
    ChannelReader<ReadOnlyMemory<byte>> Frames { get; }

    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync();
}

/// <summary>録音・3 接続・字幕組み立てを束ねるセッション。UI 非依存。</summary>
public sealed class InterpretationSession : IDisposable
{
    public const int MaxReconnectAttempts = 5;

    /// <summary>
    /// ルーティング判定用に保持する原文の上限 (UTF-16 char)。
    /// 通常は末尾の非空白 scalar ウィンドウへ切り詰めるが、ウィンドウ内の空白が異常に長い場合の
    /// 安全弁として使い、空白 run を圧縮してこの長さへ収める。
    /// </summary>
    internal const int RoutingSourceTextMaxLength = 16 * SpokenLanguageDetector.RecentEvidenceWindow;

    private static readonly TimeSpan DefaultInitialReconnectDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan DefaultTickInterval = TimeSpan.FromMilliseconds(200);

    private readonly IApiKeyStore _apiKeyStore;
    private readonly IRealtimeAudioCapture _audioCapture;
    private readonly IDualRealtimeTranslationClient _dualClient;
    private readonly Func<RealtimeSessionTuning> _tuningProvider;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _initialReconnectDelay;
    private readonly TimeSpan _tickInterval;
    private readonly Func<LanguagePair> _languagePairProvider;
    private readonly RealtimeSubtitleAssembler _assembler = new();
    private readonly object _sync = new();
    private readonly SemaphoreSlim _routingGate = new(1, 1);

    private CancellationTokenSource? _sessionCts;
    private Task? _sessionTask;
    private int _lifecycleGeneration;
    private int _reconnectAttempt;
    private TranslationState _state = TranslationState.Idle;
    private string _routingSourceText = string.Empty;
    private RealtimeTranslationOutputLanguage? _selectedTranslationTarget;
    private int _reverseEvidenceCount;

    /// <summary>テスト用。generation 確認後・assembler 更新前に差し込む。</summary>
    internal Action? BeforeAssemblerIngestForTests { get; set; }

    /// <summary>テスト用。ルーティング判定バッファの保持長。</summary>
    internal int RoutingSourceTextLengthForTests
    {
        get
        {
            lock (_sync)
            {
                return _routingSourceText.Length;
            }
        }
    }

    public InterpretationSession(
        IApiKeyStore apiKeyStore,
        IRealtimeAudioCapture audioCapture,
        IDualRealtimeTranslationClient dualClient,
        Func<RealtimeSessionTuning>? tuningProvider = null,
        TimeProvider? timeProvider = null,
        TimeSpan? initialReconnectDelay = null,
        TimeSpan? tickInterval = null,
        LanguagePair languagePair = LanguagePair.JaEn,
        Func<LanguagePair>? languagePairProvider = null)
    {
        ArgumentNullException.ThrowIfNull(apiKeyStore);
        ArgumentNullException.ThrowIfNull(audioCapture);
        ArgumentNullException.ThrowIfNull(dualClient);

        _apiKeyStore = apiKeyStore;
        _audioCapture = audioCapture;
        _dualClient = dualClient;
        _tuningProvider = tuningProvider ?? (() => RealtimeSessionTuning.Default);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _initialReconnectDelay = initialReconnectDelay ?? DefaultInitialReconnectDelay;
        _tickInterval = tickInterval ?? DefaultTickInterval;
        _languagePairProvider = languagePairProvider ?? (() => languagePair);
    }

    public event EventHandler<TranslationState>? StateChanged;

    public event EventHandler<RealtimeSubtitleUpdate>? SubtitleUpdated;

    /// <summary>ユーザー向け文言。サーバー文言は必ずサニタイズ済みのものを渡す。</summary>
    public event EventHandler<string>? MessageEncountered;

    public TranslationState State
    {
        get
        {
            lock (_sync)
            {
                return _state;
            }
        }
    }

    public async Task StartAsync()
    {
        Task? previous;
        CancellationTokenSource? previousCts;
        lock (_sync)
        {
            if (_state is not (TranslationState.Idle or TranslationState.Error))
            {
                return;
            }

            // await をまたぐ再入を防ぐため、受理直後に Connecting へ進める。
            _state = TranslationState.Connecting;
            previous = _sessionTask;
            previousCts = _sessionCts;
        }

        StateChanged?.Invoke(this, TranslationState.Connecting);

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
        lock (_sync)
        {
            previousCts?.Dispose();
            _lifecycleGeneration += 1;
            generation = _lifecycleGeneration;
            _reconnectAttempt = 0;
            _sessionCts = cts;
        }

        SetState(TranslationState.Connecting);
        var sessionTask = Task.Run(() => RunSessionLoopAsync(generation, token), CancellationToken.None);
        lock (_sync)
        {
            _sessionTask = sessionTask;
        }
    }

    public async Task StopAsync()
    {
        Task? sessionTask;
        CancellationTokenSource? cts;
        lock (_sync)
        {
            if (_state == TranslationState.Idle)
            {
                return;
            }

            _lifecycleGeneration += 1;
            sessionTask = _sessionTask;
            _sessionTask = null;
            cts = _sessionCts;
            _sessionCts = null;
        }

        SetState(TranslationState.Closing);

        // 先に音声と session consumer を止め、close drain イベントを破棄されないようにする。
        // generation を上げたまま consumer が生きていると、commit/session.close の
        // 最終 delta を読んで捨ててしまい、オプトイン字幕記録が欠ける。
        await _audioCapture.StopAsync().ConfigureAwait(false);
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
            await _dualClient.CloseGracefullyAsync().ConfigureAwait(false);
        }
#pragma warning disable CA1031 // graceful close が失敗しても force close で必ず解放する。
        catch (Exception)
#pragma warning restore CA1031
        {
            await _dualClient.ForceCloseAsync().ConfigureAwait(false);
        }

        // commit / session.close 中に届いた最終 delta を assembler へ取り込む。
        await IngestStopDrainEventsAsync().ConfigureAwait(false);

        // 停止時点で完全ペアが残っていれば確定して見せる（オプトイン字幕記録も含む）。
        FlushPendingFinalizeIfNeeded();

        SetState(TranslationState.Idle);
    }

    /// <summary>録音中の prompt/keywords/delay 変更を原文接続へ反映する。</summary>
    public async Task ApplyTuningChangeAsync()
    {
        if (State != TranslationState.Listening)
        {
            return;
        }

        try
        {
            await _dualClient.UpdateTranscriptionTuningAsync(_tuningProvider()).ConfigureAwait(false);
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
        lock (_sync)
        {
            _lifecycleGeneration += 1;
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
            FlushPendingFinalizeIfNeeded();
        }
#pragma warning disable CA1031 // Dispose 経路では例外を外へ出さない。
        catch (Exception)
#pragma warning restore CA1031
        {
            // flush 失敗でも破棄完了は継続する。
        }

        // `_routingGate` は同期 Dispose では破棄しない。
        // in-flight の Update/ResetAudioRouting が Wait/Release 中に ObjectDisposedException へ落ちないようにする。
        // StopAsync 後は参照が切れ、SemaphoreSlim は GC で回収される (AvailableWaitHandle 未使用)。
    }

    private async Task RunSessionLoopAsync(int generation, CancellationToken cancellationToken)
    {
        while (IsCurrentGeneration(generation) && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ConnectAndStreamAsync(generation, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (RealtimeTranslationException error) when (!error.IsRecoverable)
            {
                if (!IsCurrentGeneration(generation))
                {
                    // stop 中の teardown が起こした失敗を、ユーザー向けエラーに昇格させない。
                    return;
                }

                await TearDownStreamingAsync().ConfigureAwait(false);
                // epoch を捨てる前に完全ペアを確定し、オプトイン字幕記録へ渡す。
                FlushPendingFinalizeIfNeeded();
                EnterError(error.Message);
                return;
            }
#pragma warning disable CA1031 // 想定外の失敗でも session task を落とさず再接続へ倒す。
            catch (Exception)
#pragma warning restore CA1031
            {
                // recoverable transport failure / 音声デバイス失敗。下の再接続へ進む。
            }

            if (!IsCurrentGeneration(generation) || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            int attempt;
            lock (_sync)
            {
                if (_reconnectAttempt >= MaxReconnectAttempts)
                {
                    attempt = -1;
                }
                else
                {
                    _reconnectAttempt += 1;
                    attempt = _reconnectAttempt;
                }
            }

            if (attempt < 0)
            {
                await TearDownStreamingAsync().ConfigureAwait(false);
                FlushPendingFinalizeIfNeeded();
                EnterError("再接続上限に達しました");
                return;
            }

            SetState(TranslationState.Reconnecting);
            await TearDownStreamingAsync().ConfigureAwait(false);

            // 指数バックオフ。5 回目以降は頭打ちにする。
            var delay = _initialReconnectDelay * Math.Pow(2, Math.Min(attempt - 1, 4));
            try
            {
                await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task ConnectAndStreamAsync(int generation, CancellationToken cancellationToken)
    {
        var apiKey = RequireApiKey();
        SetState(TranslationState.Connecting);

        var languagePair = _languagePairProvider();
        await _dualClient.StartAsync(apiKey, _tuningProvider(), languagePair, cancellationToken).ConfigureAwait(false);
        if (!IsCurrentGeneration(generation))
        {
            await _dualClient.ForceCloseAsync().ConfigureAwait(false);
            return;
        }

        var epoch = _dualClient.ConnectionEpoch;
        // 再接続時 BeginNewEpoch は buffer を捨てる。idle finalize 前の完全ペアを
        // 先に確定しないと、オプトイン字幕記録へ ShouldFinalize が届かない。
        FlushPendingFinalizeIfNeeded();
        lock (_sync)
        {
            _assembler.BeginNewEpoch(epoch);
            _routingSourceText = string.Empty;
            _selectedTranslationTarget = null;
            _reverseEvidenceCount = 0;
        }

        await _dualClient.ResetAudioRoutingAsync().ConfigureAwait(false);
        await _audioCapture.StartAsync(cancellationToken).ConfigureAwait(false);
        if (!IsCurrentGeneration(generation))
        {
            await _audioCapture.StopAsync().ConfigureAwait(false);
            await _dualClient.ForceCloseAsync().ConfigureAwait(false);
            return;
        }

        SetState(TranslationState.Listening);
        lock (_sync)
        {
            _reconnectAttempt = 0;
        }

        using var streamCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var feed = FeedAudioAsync(generation, streamCts.Token);
        var consume = ConsumeEventsAsync(generation, epoch, streamCts.Token);
        var tick = RunTickerAsync(streamCts.Token);

        var first = await Task.WhenAny(feed, consume).ConfigureAwait(false);
        await streamCts.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(SuppressCancellation(feed), SuppressCancellation(consume), SuppressCancellation(tick))
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

    private async Task FeedAudioAsync(int generation, CancellationToken cancellationToken)
    {
        await foreach (var frame in _audioCapture.Frames.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!IsCurrentGeneration(generation) || State != TranslationState.Listening)
            {
                return;
            }

            await _dualClient.AppendAudioFrameAsync(frame, cancellationToken).ConfigureAwait(false);
        }

        if (!IsCurrentGeneration(generation))
        {
            return;
        }

        throw new RealtimeTranslationException(
            RealtimeTranslationErrorKind.RecoverableTransportFailure,
            "音声入力が停止しました");
    }

    private async Task ConsumeEventsAsync(int generation, int epoch, CancellationToken cancellationToken)
    {
        var events = _dualClient.Events;
        await foreach (var streamEvent in events.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!IsCurrentGeneration(generation))
            {
                return;
            }

            if (streamEvent.Epoch != epoch)
            {
                continue;
            }

            if (streamEvent.Event is RealtimeTranslationServerEvent.ServerError error)
            {
                throw ClassifyError(error);
            }

            if (streamEvent.Event is RealtimeTranslationServerEvent.InputTranscriptDelta source
                && streamEvent.Lane.IsSource)
            {
                await UpdateAudioRoutingAsync(source.Delta, cancellationToken).ConfigureAwait(false);
            }

            BeforeAssemblerIngestForTests?.Invoke();

            RealtimeSubtitleUpdate? update;
            lock (_sync)
            {
                // Dispose/Stop が generation を進めたあとに、取り出し済みイベントで
                // assembler を更新しない（flush 後の完全ペア欠落を防ぐ）。
                if (_lifecycleGeneration != generation)
                {
                    return;
                }

                update = _assembler.Ingest(streamEvent, _timeProvider.GetUtcNow());
            }

            if (update is { } value)
            {
                SubtitleUpdated?.Invoke(this, value);
                if (value.ShouldFinalize)
                {
                    await ResetAudioRoutingForNextSegmentAsync().ConfigureAwait(false);
                }
            }
        }

        if (!IsCurrentGeneration(generation))
        {
            return;
        }

        throw new RealtimeTranslationException(
            RealtimeTranslationErrorKind.RecoverableTransportFailure,
            "イベント受信が停止しました");
    }

    private static RealtimeTranslationException ClassifyError(RealtimeTranslationServerEvent.ServerError error)
    {
        if (error.Code == DualRealtimeTranslationClient.TransportErrorCode)
        {
            return new RealtimeTranslationException(RealtimeTranslationErrorKind.RecoverableTransportFailure);
        }

        return RealtimeTranslationException.IsAuthenticationFailure(error.Code, error.Message)
            ? new RealtimeTranslationException(RealtimeTranslationErrorKind.AuthenticationFailed)
            : new RealtimeTranslationException(
                RealtimeTranslationErrorKind.FatalServerError,
                RealtimeTranslationException.SanitizeServerMessage(error.Message));
    }

    private async Task RunTickerAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_tickInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            RealtimeSubtitleUpdate? update;
            lock (_sync)
            {
                update = _assembler.Tick(_timeProvider.GetUtcNow());
            }

            if (update is { } value)
            {
                SubtitleUpdated?.Invoke(this, value);
                await ResetAudioRoutingForNextSegmentAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>原文 delta の文字種から発話言語を決め、逆側 target へ音声を切り替える。</summary>
    private async Task UpdateAudioRoutingAsync(string delta, CancellationToken cancellationToken)
    {
        await _routingGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SpokenLanguageEvidence evidence;
            RealtimeTranslationOutputLanguage? currentTarget;
            int reverseEvidenceCount;
            lock (_sync)
            {
                _routingSourceText = TrimRoutingSourceText(_routingSourceText + delta);
                evidence = SpokenLanguageDetector.RecentEvidence(_routingSourceText, _languagePairProvider());
                currentTarget = _selectedTranslationTarget;
                reverseEvidenceCount = _reverseEvidenceCount;
            }

            var selection = TranslationTargetSelector.Select(
                _languagePairProvider(),
                currentTarget,
                reverseEvidenceCount,
                evidence);
            if (selection.Target == currentTarget)
            {
                lock (_sync)
                {
                    _reverseEvidenceCount = selection.ReverseEvidenceCount;
                }
                if (currentTarget is null)
                {
                    if (selection.Target is null)
                    {
                        return;
                    }

                    await _dualClient.SelectTranslationTargetAsync(selection.Target, cancellationToken)
                        .ConfigureAwait(false);
                    lock (_sync)
                    {
                        _selectedTranslationTarget = selection.Target;
                        _assembler.ExpectLane(selection.Target);
                    }
                }
                return;
            }

            RealtimeSubtitleUpdate? finalized;
            lock (_sync)
            {
                finalized = _assembler.FinalizeForLanguageSwitch(_timeProvider.GetUtcNow());
            }

            if (finalized is { } value)
            {
                SubtitleUpdated?.Invoke(this, value);
            }

            await ResetAudioRoutingForNextSegmentCoreAsync().ConfigureAwait(false);

            lock (_sync)
            {
                // 切替を起こした delta は新しい segment の先頭として持ち越す。
                _routingSourceText = TrimRoutingSourceText(delta);
                _selectedTranslationTarget = selection.Target;
                _reverseEvidenceCount = selection.ReverseEvidenceCount;
                _assembler.ExpectLane(selection.Target);
            }

            await _dualClient.SelectTranslationTargetAsync(selection.Target, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _routingGate.Release();
        }
    }

    /// <summary>
    /// <see cref="SpokenLanguageDetector.RecentEvidence"/> と同じ末尾非空白 scalar ウィンドウを残す。
    /// ウィンドウ内の空白が異常に長く上限を超える場合だけ空白 run を U+0020 1 個へ圧縮し、語境界を保ったまま収める。
    /// </summary>
    private static string TrimRoutingSourceText(string text)
    {
        if (text.Length == 0)
        {
            return text;
        }

        var start = RecentEvidenceWindowStart(text, SpokenLanguageDetector.RecentEvidenceWindow);
        var window = text[start..];
        if (window.Length <= RoutingSourceTextMaxLength)
        {
            return window;
        }

        return CollapseWhitespaceRuns(window);
    }

    /// <summary>
    /// 末尾から空白以外の Unicode scalar を <paramref name="window"/> 個含む範囲の開始 UTF-16 オフセット。
    /// <see cref="SpokenLanguageDetector.RecentEvidence"/> と同じ走査契約。
    /// </summary>
    private static int RecentEvidenceWindowStart(string text, int window)
    {
        if (window <= 0 || text.Length == 0)
        {
            return 0;
        }

        var starts = new List<int>(text.Length);
        var offset = 0;
        while (offset < text.Length)
        {
            starts.Add(offset);
            offset += Rune.GetRuneAt(text, offset).Utf16SequenceLength;
        }

        var nonWhitespaceCount = 0;
        var position = starts.Count;
        var start = 0;
        while (position > 0 && nonWhitespaceCount < window)
        {
            position -= 1;
            start = starts[position];
            if (!Rune.IsWhiteSpace(Rune.GetRuneAt(text, start)))
            {
                nonWhitespaceCount += 1;
            }
        }

        return start;
    }

    /// <summary>連続する空白 scalar を U+0020 1 個へ潰す。ラテン語境界を残しつつ保持長を抑える。</summary>
    private static string CollapseWhitespaceRuns(string text)
    {
        var builder = new StringBuilder(Math.Min(text.Length, RoutingSourceTextMaxLength));
        var previousWasWhitespace = false;
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                if (previousWasWhitespace)
                {
                    continue;
                }

                previousWasWhitespace = true;
                builder.Append(' ');
                continue;
            }

            previousWasWhitespace = false;
            builder.Append(rune.ToString());
        }

        return builder.ToString();
    }

    private async Task ResetAudioRoutingForNextSegmentAsync()
    {
        await _routingGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await ResetAudioRoutingForNextSegmentCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _routingGate.Release();
        }
    }

    private async Task ResetAudioRoutingForNextSegmentCoreAsync()
    {
        lock (_sync)
        {
            _routingSourceText = string.Empty;
            _selectedTranslationTarget = null;
            _reverseEvidenceCount = 0;
            _assembler.ExpectLane(null);
        }

        await _dualClient.ResetAudioRoutingAsync().ConfigureAwait(false);
    }

    private async Task TearDownStreamingAsync()
    {
        try
        {
            await _audioCapture.StopAsync().ConfigureAwait(false);
        }
        finally
        {
            await _dualClient.ForceCloseAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 完全な原文+訳文ペアが assembler に残っていれば idle 待ちを飛ばして確定する。
    /// 停止・再接続・致命エラーで epoch/buffer を捨てる直前に呼び、字幕記録の欠落を防ぐ。
    /// </summary>
    private void FlushPendingFinalizeIfNeeded()
    {
        RealtimeSubtitleUpdate? pending;
        lock (_sync)
        {
            pending = _assembler.Tick(
                _timeProvider.GetUtcNow() + RealtimeSubtitleAssembler.IdleFinalizeInterval);
        }

        if (pending is { } update)
        {
            SubtitleUpdated?.Invoke(this, update);
        }
    }

    /// <summary>
    /// 正常停止の close drain で channel に残った字幕イベントを assembler へ取り込む。
    /// session consumer は世代更新で既に止まっている前提。
    /// </summary>
    private async Task IngestStopDrainEventsAsync()
    {
        var events = _dualClient.Events;
        while (await events.WaitToReadAsync().ConfigureAwait(false))
        {
            while (events.TryRead(out var streamEvent))
            {
                if (streamEvent.Event is RealtimeTranslationServerEvent.ServerError)
                {
                    continue;
                }

                RealtimeSubtitleUpdate? update;
                lock (_sync)
                {
                    update = _assembler.Ingest(streamEvent, _timeProvider.GetUtcNow());
                }

                if (update is { } value)
                {
                    SubtitleUpdated?.Invoke(this, value);
                }
            }
        }
    }

    private string RequireApiKey()
    {
        var key = _apiKeyStore.Load();
        return string.IsNullOrWhiteSpace(key)
            ? throw new RealtimeTranslationException(RealtimeTranslationErrorKind.MissingApiKey)
            : key;
    }

    private bool IsCurrentGeneration(int generation)
    {
        lock (_sync)
        {
            return _lifecycleGeneration == generation;
        }
    }

    private void EnterError(string message)
    {
        SetState(TranslationState.Error);
        MessageEncountered?.Invoke(this, message);
    }

    private void SetState(TranslationState state)
    {
        lock (_sync)
        {
            if (_state == state)
            {
                return;
            }

            _state = state;
        }

        StateChanged?.Invoke(this, state);
    }
}
