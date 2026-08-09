using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using RealtimeTranslator.Core.Audio;
using RealtimeTranslator.Core.OpenAI;

namespace RealtimeTranslator.Core.Realtime;

/// <summary>原文 1 本 + 翻訳 2 本を束ね、音声を検出言語の逆側 target だけへ流す。</summary>
public interface IDualRealtimeTranslationClient
{
    ChannelReader<RealtimeTranslationStreamEvent> Events { get; }

    int ConnectionEpoch { get; }

    Task StartAsync(string apiKey, RealtimeSessionTuning tuning, CancellationToken cancellationToken = default);

    Task AppendAudioFrameAsync(ReadOnlyMemory<byte> pcm16LittleEndian, CancellationToken cancellationToken = default);

    Task SetSpokenLanguageAsync(SpokenLanguage language, CancellationToken cancellationToken = default);

    Task UpdateTranscriptionTuningAsync(RealtimeSessionTuning tuning, CancellationToken cancellationToken = default);

    Task ResetAudioRoutingAsync();

    Task CloseGracefullyAsync(CancellationToken cancellationToken = default);

    Task ForceCloseAsync();
}

public sealed class DualRealtimeTranslationClient : IDualRealtimeTranslationClient, IDisposable
{
    /// <summary>100 ms frame × 40 = 直近 4 秒。言語判定の遅れがあっても発話冒頭を翻訳へ届ける。</summary>
    public const int TranslationPrerollFrameLimit = 40;

    public const int ConsecutiveTranslationFailureLimit = 3;

    public const string TransportErrorMessage = "翻訳サーバーへの音声送信が失敗しました";

    public const string TransportErrorCode = "transport";

    private readonly RealtimeSourceTranscriptionConnection _sourceConnection;
    private readonly RealtimeTranslationConnection _englishConnection;
    private readonly RealtimeTranslationConnection _japaneseConnection;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly Queue<PendingTranslationFrame> _pendingTranslationFrames = new();
    private readonly Queue<ReadOnlyMemory<byte>> _translationPrerollFrames = new();

    private Channel<RealtimeTranslationStreamEvent> _events = RealtimeEventChannel.Create();
    private Task? _mergeTask;
    private Task? _translationPumpTask;
    private CancellationTokenSource? _mergeCts;
    private CancellationTokenSource _translationPumpCts = new();
    private int _connectionEpoch;
    private bool _isRunning;
    private int _consecutiveTranslationFailures;

    /// <summary>transport failure 後は再接続まで翻訳ポンプを再開しない。</summary>
    private bool _translationPumpHaltedForTransportFailure;

    private RealtimeTranslationOutputLanguage? _selectedTranslationTarget;

    public DualRealtimeTranslationClient(
        RealtimeSourceTranscriptionConnection sourceConnection,
        RealtimeTranslationConnection englishConnection,
        RealtimeTranslationConnection japaneseConnection)
    {
        ArgumentNullException.ThrowIfNull(sourceConnection);
        ArgumentNullException.ThrowIfNull(englishConnection);
        ArgumentNullException.ThrowIfNull(japaneseConnection);

        _sourceConnection = sourceConnection;
        _englishConnection = englishConnection;
        _japaneseConnection = japaneseConnection;
    }

    public ChannelReader<RealtimeTranslationStreamEvent> Events
    {
        get
        {
            lock (_sync)
            {
                return _events.Reader;
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

    public async Task StartAsync(
        string apiKey,
        RealtimeSessionTuning tuning,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tuning);

        await ForceCloseAsync().ConfigureAwait(false);

        int epoch;
        lock (_sync)
        {
            _events = RealtimeEventChannel.Create();
            _connectionEpoch += 1;
            epoch = _connectionEpoch;
            _isRunning = true;
            _consecutiveTranslationFailures = 0;
            _translationPumpHaltedForTransportFailure = false;
            _selectedTranslationTarget = null;
            _translationPrerollFrames.Clear();
            _pendingTranslationFrames.Clear();
            _translationPumpCts.Dispose();
            _translationPumpCts = new CancellationTokenSource();
        }

        try
        {
            await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await Task.WhenAll(
                    _sourceConnection.StartAsync(apiKey, tuning, cancellationToken),
                    _englishConnection.StartAsync(
                        apiKey,
                        RealtimeTranslationSessionConfig.EnglishTargetWithoutSourceTranscription(
                            tuning.NoiseReduction),
                        cancellationToken),
                    _japaneseConnection.StartAsync(
                        apiKey,
                        RealtimeTranslationSessionConfig.JapaneseTargetWithoutSourceTranscription(
                            tuning.NoiseReduction),
                        cancellationToken)).ConfigureAwait(false);
            }
            finally
            {
                _lifecycleGate.Release();
            }
        }
        catch
        {
            await ForceCloseAsync().ConfigureAwait(false);
            throw;
        }

        lock (_sync)
        {
            if (epoch != _connectionEpoch || !_isRunning)
            {
                throw new RealtimeTranslationException(RealtimeTranslationErrorKind.Cancelled);
            }
        }

        StartEventMerge(epoch);
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
            _translationPrerollFrames.Enqueue(retained);
            while (_translationPrerollFrames.Count > TranslationPrerollFrameLimit)
            {
                _translationPrerollFrames.Dequeue();
            }

            if (_selectedTranslationTarget is { } target)
            {
                EnqueueTranslationFrameLocked(retained, target);
            }
        }
    }

    public Task SetSpokenLanguageAsync(SpokenLanguage language, CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;

        lock (_sync)
        {
            if (!_isRunning)
            {
                throw new RealtimeTranslationException(RealtimeTranslationErrorKind.NotConnected);
            }

            RealtimeTranslationOutputLanguage target;
            switch (language)
            {
                case SpokenLanguage.Japanese:
                    target = RealtimeTranslationOutputLanguage.English;
                    break;
                case SpokenLanguage.English:
                    target = RealtimeTranslationOutputLanguage.Japanese;
                    break;
                default:
                    return Task.CompletedTask;
            }

            if (_selectedTranslationTarget == target)
            {
                return Task.CompletedTask;
            }

            _selectedTranslationTarget = target;

            // 旧 target 向けの未送信 frame は破棄し、rolling preroll を新 target へ flush する。
            _pendingTranslationFrames.Clear();
            foreach (var frame in _translationPrerollFrames)
            {
                EnqueueTranslationFrameLocked(frame, target);
            }
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
            // rolling preroll は維持し、次の SetSpokenLanguageAsync で flush できるようにする。
            _selectedTranslationTarget = null;
            _pendingTranslationFrames.Clear();
            _consecutiveTranslationFailures = 0;
        }

        return Task.CompletedTask;
    }

    public async Task CloseGracefullyAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (!_isRunning)
            {
                return;
            }
        }

        // 未送信の翻訳フレームを先に送り、停止時の訳文欠落を防ぐ。
        // 送信が停滞しても close 自体は進める。
        try
        {
            await WaitForTranslationDrainAsync(TimeSpan.FromSeconds(5), cancellationToken)
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
            _pendingTranslationFrames.Clear();
            pump = _translationPumpTask;
            pumpCts = _translationPumpCts;
        }

        await pumpCts.CancelAsync().ConfigureAwait(false);
        await AwaitPumpAsync(pump).ConfigureAwait(false);

        Exception? firstError = null;
        try
        {
            await Task.WhenAll(
                _sourceConnection.CloseGracefullyAsync(cancellationToken),
                _englishConnection.CloseGracefullyAsync(cancellationToken),
                _japaneseConnection.CloseGracefullyAsync(cancellationToken)).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // 最初の close 失敗だけを呼び出し元へ返し、残りの解放は必ず行う。
        catch (Exception error)
#pragma warning restore CA1031
        {
            firstError = error;
        }

        await StopEventMergeAsync().ConfigureAwait(false);

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
            _translationPrerollFrames.Clear();
            _pendingTranslationFrames.Clear();
            _consecutiveTranslationFailures = 0;
            _translationPumpHaltedForTransportFailure = false;
            _connectionEpoch += 1;
            pump = _translationPumpTask;
            pumpCts = _translationPumpCts;
        }

        await pumpCts.CancelAsync().ConfigureAwait(false);
        await AwaitPumpAsync(pump).ConfigureAwait(false);

        Exception? firstError = null;
        foreach (var close in new Func<Task>[]
                 {
                     _sourceConnection.ForceCloseAsync,
                     _englishConnection.ForceCloseAsync,
                     _japaneseConnection.ForceCloseAsync,
                 })
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

        await StopEventMergeAsync().ConfigureAwait(false);

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
            mergeCts = _mergeCts;
            _mergeCts = null;
            pumpCts = _translationPumpCts;
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
        _englishConnection.Dispose();
        _japaneseConnection.Dispose();
    }

    private static async Task AwaitPumpAsync(Task? pump)
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

    /// <summary>翻訳ポンプが現在の待ち行列を処理し終えるまで待つ。決定的なテストのために使う。</summary>
    /// <remarks>送信が停滞しても timeout（既定5秒）で打ち切る（ポンプTaskを無期限待ちしない）。</remarks>
    internal async Task WaitForTranslationDrainAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var deadline = Environment.TickCount64
            + (long)(timeout ?? TimeSpan.FromSeconds(5)).TotalMilliseconds;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Task? pump;
            lock (_sync)
            {
                if (_translationPumpTask is null && _pendingTranslationFrames.Count == 0)
                {
                    return;
                }

                pump = _translationPumpTask;
            }

            var remainingMs = deadline - Environment.TickCount64;
            if (remainingMs <= 0)
            {
                lock (_sync)
                {
                    if (_translationPumpTask is null && _pendingTranslationFrames.Count == 0)
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
                    if (_translationPumpTask is null && _pendingTranslationFrames.Count == 0)
                    {
                        return;
                    }
                }

                throw new TimeoutException("translation pump did not drain");
            }

            await pump.ConfigureAwait(false);
        }
    }

    private void EnqueueTranslationFrameLocked(
        ReadOnlyMemory<byte> frame,
        RealtimeTranslationOutputLanguage target)
    {
        // transport failure 後は enqueue 自体を止め、ポンプ再起動の隙を残さない。
        if (_translationPumpHaltedForTransportFailure)
        {
            return;
        }

        _pendingTranslationFrames.Enqueue(new PendingTranslationFrame(frame, target));
        _translationPumpTask ??= Task.Run(PumpTranslationFramesAsync, CancellationToken.None);
    }

    private async Task PumpTranslationFramesAsync()
    {
        CancellationToken pumpToken;
        lock (_sync)
        {
            pumpToken = _translationPumpCts.Token;
        }

        while (true)
        {
            PendingTranslationFrame pending;
            lock (_sync)
            {
                if (!_isRunning
                    || _translationPumpHaltedForTransportFailure
                    || _pendingTranslationFrames.Count == 0)
                {
                    _translationPumpTask = null;
                    return;
                }

                pending = _pendingTranslationFrames.Dequeue();
            }

            try
            {
                var connection = pending.Target == RealtimeTranslationOutputLanguage.English
                    ? _englishConnection
                    : _japaneseConnection;
                await connection.AppendAudioFrameAsync(pending.Frame, pumpToken).ConfigureAwait(false);

                lock (_sync)
                {
                    _consecutiveTranslationFailures = 0;
                }
            }
            catch (OperationCanceledException)
            {
                lock (_sync)
                {
                    _translationPumpTask = null;
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
                    _consecutiveTranslationFailures += 1;
                    halted = _consecutiveTranslationFailures >= ConsecutiveTranslationFailureLimit;
                    epoch = _connectionEpoch;
                    if (halted)
                    {
                        // 再接続待ちの間、死にかけの socket へ送り続けない。
                        _translationPumpHaltedForTransportFailure = true;
                        _pendingTranslationFrames.Clear();
                    }
                }

                if (halted)
                {
                    // drain 待ちが復帰する前に transport error を確実に発行する。
                    PublishTransportError(pending.Target, epoch);
                    lock (_sync)
                    {
                        _translationPumpTask = null;
                    }

                    return;
                }
            }
        }
    }

    private void PublishTransportError(RealtimeTranslationOutputLanguage target, int epoch)
    {
        ChannelWriter<RealtimeTranslationStreamEvent> writer;
        lock (_sync)
        {
            writer = _events.Writer;
        }

        writer.TryWrite(new RealtimeTranslationStreamEvent(
            target,
            new RealtimeTranslationServerEvent.ServerError(TransportErrorMessage, TransportErrorCode),
            epoch));
    }

    private void StartEventMerge(int epoch)
    {
        var cts = new CancellationTokenSource();

        // Dispose 済み CTS へ触れないよう、Task 開始前に token を確定させる。
        var token = cts.Token;
        ChannelWriter<RealtimeTranslationStreamEvent> writer;
        lock (_sync)
        {
            _mergeCts = cts;
            writer = _events.Writer;
        }

        var readers = new[]
        {
            _sourceConnection.Events,
            _englishConnection.Events,
            _japaneseConnection.Events,
        };

        _mergeTask = Task.Run(
            async () =>
            {
                var pumps = new Task[readers.Length];
                for (var index = 0; index < readers.Length; index += 1)
                {
                    pumps[index] = MergeOneAsync(readers[index], writer, epoch, token);
                }

                await Task.WhenAll(pumps).ConfigureAwait(false);

                // 全接続のイベント流が終わったら購読側を解放する。
                if (ConnectionEpoch == epoch)
                {
                    writer.TryComplete();
                }
            },
            CancellationToken.None);
    }

    private async Task MergeOneAsync(
        ChannelReader<RealtimeTranslationStreamEvent> reader,
        ChannelWriter<RealtimeTranslationStreamEvent> writer,
        int epoch,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var streamEvent in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (ConnectionEpoch != epoch)
                {
                    return;
                }

                // Dual 側の epoch で貼り直し、接続内部の epoch と揃える。
                writer.TryWrite(streamEvent with { Epoch = epoch });
            }
        }
        catch (OperationCanceledException)
        {
            // 停止時のキャンセルは正常終了として扱う。
        }
    }

    private async Task StopEventMergeAsync()
    {
        CancellationTokenSource? cts;
        Task? mergeTask;
        ChannelWriter<RealtimeTranslationStreamEvent> writer;
        lock (_sync)
        {
            cts = _mergeCts;
            _mergeCts = null;
            mergeTask = _mergeTask;
            _mergeTask = null;
            writer = _events.Writer;
        }

        if (cts is not null)
        {
            await cts.CancelAsync().ConfigureAwait(false);
            cts.Dispose();
        }

        if (mergeTask is not null)
        {
            try
            {
                await mergeTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // cancel 済みの merge は正常終了として扱う。
            }
        }

        writer.TryComplete();
    }

    private readonly record struct PendingTranslationFrame(
        ReadOnlyMemory<byte> Frame,
        RealtimeTranslationOutputLanguage Target);
}
