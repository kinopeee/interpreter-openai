using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using RealtimeTranslator.Core.Audio;
using RealtimeTranslator.Core.Localization;
using RealtimeTranslator.Core.OpenAI;

namespace RealtimeTranslator.Core.Realtime;

/// <summary>原文 1 本 + 翻訳 2 本を束ね、音声を検出言語の逆側 target だけへ流す。</summary>
public interface IDualRealtimeTranslationClient
{
    ChannelReader<RealtimeTranslationStreamEvent> Events { get; }

    int ConnectionEpoch { get; }

    /// StartAsync で予約した接続 epoch。失敗後の ForceClose で ConnectionEpoch が
    /// 進んでも、失敗した handshake の予約値を保持する。
    int ReservedEpoch { get; }

    RealtimeEventFeed Feed { get; }

    Task StartAsync(
        string apiKey,
        RealtimeSessionTuning tuning,
        LanguagePair pair = LanguagePair.JaEn,
        CancellationToken cancellationToken = default);

    Task AppendAudioFrameAsync(ReadOnlyMemory<byte> pcm16LittleEndian, CancellationToken cancellationToken = default);

    Task SelectTranslationTargetAsync(
        RealtimeTranslationOutputLanguage? target,
        CancellationToken cancellationToken = default);

    Task UpdateTranscriptionTuningAsync(RealtimeSessionTuning tuning, CancellationToken cancellationToken = default);

    Task ResetAudioRoutingAsync();

    Task CloseGracefullyAsync(CancellationToken cancellationToken = default);

    Task ForceCloseAsync();
}

public sealed class DualRealtimeTranslationClient : IDualRealtimeTranslationClient, IDisposable
{
    public static string TransportErrorMessage => UserCopy.Current.Text("error.audioSendFailed");

    public static string TranslationBacklogErrorMessage => UserCopy.Current.Text("error.translationBacklog");

    public const string TransportErrorCode = "transport";

    private readonly RealtimeSourceTranscriptionConnection _sourceConnection;
    private readonly Dictionary<RealtimeTranslationOutputLanguage, RealtimeTranslationConnection> _connections;
    private readonly DualRealtimeTranslationClientTuning _clientTuning;
    private readonly TimeSpan _translationDrainTimeout;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly TranslationSendPipeline _sendPipeline;
    private readonly MergedEventBuffer _eventBuffer = new();
    private readonly EventMergePump _mergePump;

    private int _connectionEpoch;
    private int _reservedEpoch;
    private bool _isRunning;

    private RealtimeTranslationOutputLanguage? _selectedTranslationTarget;

    /// <summary>この世代で handshake した翻訳 lane。未使用 leftover 接続は merge しない。</summary>
    private RealtimeTranslationOutputLanguage[] _startedTranslationTargets = [];

    /// <summary>テスト用。StartEventMerge 直前に差し込む。</summary>
    internal Action? BeforeStartEventMergeForTests { get; set; }

    public DualRealtimeTranslationClient(
        RealtimeSourceTranscriptionConnection sourceConnection,
        RealtimeTranslationConnection englishConnection,
        RealtimeTranslationConnection japaneseConnection,
        TimeSpan? translationDrainTimeout = null,
        RealtimeTranslationConnection? spanishConnection = null,
        DualRealtimeTranslationClientTuning? clientTuning = null)
    {
        ArgumentNullException.ThrowIfNull(sourceConnection);
        ArgumentNullException.ThrowIfNull(englishConnection);
        ArgumentNullException.ThrowIfNull(japaneseConnection);

        _sourceConnection = sourceConnection;
        _connections = new()
        {
            [RealtimeTranslationOutputLanguage.English] = englishConnection,
            [RealtimeTranslationOutputLanguage.Japanese] = japaneseConnection,
        };
        if (spanishConnection is not null)
        {
            _connections[RealtimeTranslationOutputLanguage.Spanish] = spanishConnection;
        }

        _clientTuning = clientTuning ?? DualRealtimeTranslationClientTuning.Default;
        _clientTuning.EnsureValid();
        // 既定 5 秒。送信停滞でも CloseGracefully が session.close へ進める上限。
        _translationDrainTimeout = translationDrainTimeout ?? _clientTuning.DefaultCloseDrainTimeout;
        _sendPipeline = new TranslationSendPipeline(
            _sync,
            _clientTuning,
            _translationDrainTimeout,
            target => _connections[target],
            () => _connectionEpoch,
            () => _isRunning,
            () => _eventBuffer.MergeWriter);
        _mergePump = new EventMergePump(
            _sync,
            _eventBuffer,
            () => ConnectionEpoch,
            () => _sourceConnection.Events,
            target => _connections[target].Events,
            () => _startedTranslationTargets);
    }

    public ChannelReader<RealtimeTranslationStreamEvent> Events
    {
        get
        {
            lock (_sync)
            {
                return _eventBuffer.Reader;
            }
        }
    }

    public int ConnectionEpoch
    {
        get
        {
            lock (_sync)
            {
                return _connectionEpoch;
            }
        }
    }

    public int ReservedEpoch
    {
        get
        {
            lock (_sync)
            {
                return _reservedEpoch;
            }
        }
    }

    public RealtimeEventFeed Feed
    {
        get
        {
            lock (_sync)
            {
                return new RealtimeEventFeed(_eventBuffer.Reader, _connectionEpoch, _eventBuffer.DeliveryState);
            }
        }
    }

    internal int PendingTranslationFrameCountForTests
    {
        get
        {
            lock (_sync)
            {
                return _sendPipeline.PendingCount;
            }
        }
    }

    internal bool IsTranslationPumpHaltedForTests
    {
        get
        {
            lock (_sync)
            {
                return _sendPipeline.HaltedForTransportFailure;
            }
        }
    }

    public async Task StartAsync(
        string apiKey,
        RealtimeSessionTuning tuning,
        LanguagePair pair = LanguagePair.JaEn,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tuning);

        await ForceCloseAsync().ConfigureAwait(false);

        // pair に必要な接続が無いときは running に入らない。
        // Select / Append は未開始 Dual と同じ NotConnected になり、Events も完了したまま。
        EnsureConnectionsForPair(pair);

        int epoch;
        EventDeliveryState deliveryState;
        lock (_sync)
        {
            _connectionEpoch += 1;
            _reservedEpoch = _connectionEpoch;
            epoch = _connectionEpoch;
            _eventBuffer.Recreate(epoch);
            _isRunning = true;
            _sendPipeline.Reset();
            _selectedTranslationTarget = null;
            _sendPipeline.ClearAll();
            _sendPipeline.RecycleCancellation();
            deliveryState = _eventBuffer.DeliveryState;
        }

        using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            await _lifecycleGate.WaitAsync(handshakeCts.Token).ConfigureAwait(false);
            try
            {
                var starts = new List<Task>
                {
                    _sourceConnection.StartAsync(
                        apiKey,
                        tuning,
                        pair,
                        deliveryState,
                        handshakeCts.Token),
                };
                starts.AddRange(pair.Languages().Select(language =>
                    {
                        var target = language.ToOutputLanguage();
                        return _connections[target].StartAsync(
                            apiKey,
                            new RealtimeTranslationSessionConfig(
                                target,
                                null,
                                tuning.NoiseReduction),
                            deliveryState,
                            handshakeCts.Token);
                    }));

                await ConnectionHandshake.StartAllAsync(starts, handshakeCts).ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    _lifecycleGate.Release();
                }
                catch (ObjectDisposedException)
                {
                    // Dispose 済みなら解放は不要。
                }
            }
        }
        catch
        {
            try
            {
                await ForceCloseAsync().ConfigureAwait(false);
            }
#pragma warning disable CA1031 // leftover close 失敗で handshake / cancel の例外を消さない。
            catch (Exception)
#pragma warning restore CA1031
            {
                // Swift の forceClose は throw しない。cleanup 失敗で元例外を置換しない。
            }

            throw;
        }

        lock (_sync)
        {
            if (epoch != _connectionEpoch || !_isRunning)
            {
                throw new RealtimeTranslationException(RealtimeTranslationErrorKind.Cancelled);
            }

            _startedTranslationTargets = pair.Languages()
                .Select(language => language.ToOutputLanguage())
                .ToArray();
        }

        BeforeStartEventMergeForTests?.Invoke();
        _mergePump.StartEventMerge(epoch);
    }

    /// <summary>
    /// テスト用。未使用 lane の Events に完了済み leftover を埋め、pair 切替後の merge 混線を再現する。
    /// </summary>
    internal void SeedCompletedTranslationEventForTests(
        RealtimeTranslationOutputLanguage target,
        RealtimeTranslationServerEvent serverEvent)
    {
        ArgumentNullException.ThrowIfNull(serverEvent);

        if (!_connections.TryGetValue(target, out var connection))
        {
            throw new ArgumentOutOfRangeException(nameof(target), target, "unknown translation target");
        }

        connection.SeedCompletedEventForTests(serverEvent);
    }

    public async Task AppendAudioFrameAsync(
        ReadOnlyMemory<byte> pcm16LittleEndian,
        CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (!_isRunning)
            {
                throw new RealtimeTranslationException(RealtimeTranslationErrorKind.NotConnected);
            }
        }

        // 原文送信は単独で完了させ、翻訳側の停滞に巻き込まない。
        await _sourceConnection.AppendAudioFrameAsync(pcm16LittleEndian, cancellationToken).ConfigureAwait(false);

        var overflow = false;
        RealtimeTranslationOutputLanguage overflowTarget = default;
        int overflowEpoch = 0;
        lock (_sync)
        {
            // stop/force-close が await 中に走った場合、停止済みへは enqueue しない。
            if (!_isRunning)
            {
                return;
            }

            // 呼び出し側バッファ再利用に備え、保持前に所有コピーを取る。
            ReadOnlyMemory<byte> retained = pcm16LittleEndian.ToArray();

            // 言語切替検出の遅延を吸収するため、選択後も直近 4 秒を rolling 保持する。
            _sendPipeline.AppendPreroll(retained);

            if (_selectedTranslationTarget is { } target)
            {
                if (!_sendPipeline.TryEnqueueTranslationFrameLocked(retained, target))
                {
                    overflow = true;
                    overflowTarget = target;
                    overflowEpoch = _connectionEpoch;
                }
            }
        }

        if (overflow)
        {
            _sendPipeline.PublishTransportError(overflowTarget, overflowEpoch, TranslationBacklogErrorMessage);
        }
    }

    public Task SelectTranslationTargetAsync(
        RealtimeTranslationOutputLanguage? target,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;

        var overflow = false;
        RealtimeTranslationOutputLanguage overflowTarget = default;
        int overflowEpoch = 0;
        lock (_sync)
        {
            if (!_isRunning)
            {
                throw new RealtimeTranslationException(RealtimeTranslationErrorKind.NotConnected);
            }

            if (_selectedTranslationTarget == target)
            {
                return Task.CompletedTask;
            }

            // 旧 target 向けの未送信 frame は破棄し、rolling preroll を新 target へ flush する。
            _sendPipeline.ClearPending();
            if (target is not { } selected)
            {
                _selectedTranslationTarget = null;
                return Task.CompletedTask;
            }

            if (!_connections.ContainsKey(selected))
            {
                throw new ArgumentException(
                    $"Translation connection for '{selected.ToWireValue()}' is not configured.",
                    nameof(target));
            }

            _selectedTranslationTarget = selected;
            foreach (var frame in _sendPipeline.PrerollFrames)
            {
                if (!_sendPipeline.TryEnqueueTranslationFrameLocked(frame, selected))
                {
                    overflow = true;
                    overflowTarget = selected;
                    overflowEpoch = _connectionEpoch;
                    break;
                }
            }
        }

        if (overflow)
        {
            _sendPipeline.PublishTransportError(overflowTarget, overflowEpoch, TranslationBacklogErrorMessage);
        }

        return Task.CompletedTask;
    }

    public Task UpdateTranscriptionTuningAsync(
        RealtimeSessionTuning tuning,
        CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (!_isRunning)
            {
                throw new RealtimeTranslationException(RealtimeTranslationErrorKind.NotConnected);
            }
        }

        return _sourceConnection.UpdateTuningAsync(tuning, cancellationToken);
    }

    public Task ResetAudioRoutingAsync()
    {
        lock (_sync)
        {
            // rolling preroll は維持し、次の target 選択で flush できるようにする。
            _selectedTranslationTarget = null;
            _sendPipeline.ClearPending();
            _sendPipeline.ResetFailures();
        }

        return Task.CompletedTask;
    }

    public async Task CloseGracefullyAsync(CancellationToken cancellationToken = default)
    {
        ChannelWriter<RealtimeTranslationStreamEvent>? idleWriter = null;
        lock (_sync)
        {
            if (!_isRunning)
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
            await _sendPipeline
                .WaitForTranslationDrainAsync(_sendPipeline.ResolveCloseDrainTimeout(), cancellationToken)
                .ConfigureAwait(false);
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
            if (!_isRunning)
            {
                return;
            }

            _isRunning = false;
            _sendPipeline.ClearPending();
            (pump, pumpCts) = _sendPipeline.Detach();
        }

        await pumpCts.CancelAsync().ConfigureAwait(false);
        await TranslationSendPipeline.AwaitPumpAsync(pump).ConfigureAwait(false);

        Exception? firstError = null;
        try
        {
            await Task.WhenAll(
                new[] { _sourceConnection.CloseGracefullyAsync(cancellationToken) }
                    .Concat(_connections.Values.Select(connection => connection.CloseGracefullyAsync(cancellationToken))))
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

    public async Task ForceCloseAsync()
    {
        Task? pump;
        CancellationTokenSource pumpCts;
        lock (_sync)
        {
            _isRunning = false;
            _selectedTranslationTarget = null;
            _startedTranslationTargets = [];
            _sendPipeline.ClearAll();
            _sendPipeline.Reset();
            _connectionEpoch += 1;
            (pump, pumpCts) = _sendPipeline.Detach();
        }

        await pumpCts.CancelAsync().ConfigureAwait(false);
        await TranslationSendPipeline.AwaitPumpAsync(pump).ConfigureAwait(false);

        Exception? firstError = null;
        foreach (var close in new Func<Task>[] { _sourceConnection.ForceCloseAsync }
                     .Concat(_connections.Values.Select(connection => (Func<Task>)connection.ForceCloseAsync)))
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

    public void Dispose()
    {
        CancellationTokenSource? mergeCts;
        CancellationTokenSource pumpCts;
        lock (_sync)
        {
            _isRunning = false;
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

    /// <summary>テスト用。停止時 drain 予算（送信中 frame の +1 を含む）。</summary>
    internal TimeSpan CloseDrainTimeoutForTests => _sendPipeline.ResolveCloseDrainTimeout();

    /// <summary>翻訳ポンプが現在の待ち行列を処理し終えるまで待つ。決定的なテストのために使う。</summary>
    internal Task WaitForTranslationDrainAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default) =>
        _sendPipeline.WaitForTranslationDrainAsync(timeout, cancellationToken);

    private void EnsureConnectionsForPair(LanguagePair pair)
    {
        foreach (var language in pair.Languages())
        {
            var target = language.ToOutputLanguage();
            if (!_connections.ContainsKey(target))
            {
                throw new ArgumentException(
                    $"Translation connection for '{target.ToWireValue()}' is required for pair '{pair.ToWireValue()}'.",
                    nameof(pair));
            }
        }
    }
}
