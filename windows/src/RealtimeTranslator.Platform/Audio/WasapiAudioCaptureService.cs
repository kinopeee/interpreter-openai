using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using RealtimeTranslator.Core.Audio;
using RealtimeTranslator.Core.Localization;
using RealtimeTranslator.Core.Realtime;
using RealtimeTranslator.Platform.Logging;

namespace RealtimeTranslator.Platform.Audio;

public sealed class AudioCaptureException : Exception
{
    public AudioCaptureException()
        : this(UserCopy.Current.Text("error.micStartFailed")) { }

    public AudioCaptureException(string message)
        : base(message) { }

    public AudioCaptureException(string message, Exception innerException)
        : base(message, innerException) { }
}

/// <summary>WASAPI 共有モードのマイク入力を 100 ms / 24 kHz / PCM16 mono frame として供給する。</summary>
public sealed class WasapiAudioCaptureService : IRealtimeAudioCapture, IDisposable
{
    /// <summary>
    /// macOS 版 <c>AsyncStream(bufferingNewest: 32)</c> と同じ上限。
    /// 送信遅延時は古い frame を捨て、無制限にメモリを伸ばさない。
    /// </summary>
    internal const int FrameChannelCapacity = AudioLossPolicy.SendQueueFrameCapacity;

    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(
        Pcm16FramePacketizer.FrameDurationMilliseconds
    );

    private readonly Func<MMDevice>? _deviceFactory;

    /// <summary>録音開始時に一度だけ読み、適応マイクゲインの有効/無効を決める。</summary>
    private readonly Func<bool>? _automaticGainProvider;
    private readonly object _sync = new();

    private Channel<CapturedAudioFrame> _frames = CreateFrameChannel();
    private readonly TimeProvider _timeProvider;
    private WasapiCapture? _capture;
    private MMDevice? _ownedDevice;
    private CancellationTokenSource? _pumpCts;
    private Task? _pumpTask;
    private bool _stopRequested = true;
    private int _captureGeneration;

    public WasapiAudioCaptureService(
        Func<MMDevice>? deviceFactory = null,
        TimeProvider? timeProvider = null,
        Func<bool>? automaticGainProvider = null
    )
    {
        _deviceFactory = deviceFactory;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _automaticGainProvider = automaticGainProvider;
    }

    public ChannelReader<CapturedAudioFrame> Frames
    {
        get
        {
            lock (_sync)
            {
                return _frames.Reader;
            }
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        // 前回の pump を必ず待ち、二重 pump / 古い writer 完了の競合を避ける。
        await StopAsync().ConfigureAwait(false);

        MMDevice? ownedDevice = null;
        MMDevice device;
        WasapiCapture capture;
        try
        {
            if (_deviceFactory is not null)
            {
                device = _deviceFactory();
            }
            else
            {
                ownedDevice = new MMDeviceEnumerator().GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
                device = ownedDevice;
            }

            capture = new WasapiCapture(device);
        }
        catch (Exception error) when (error is COMException or InvalidOperationException or ArgumentException)
        {
            ownedDevice?.Dispose();
            throw new AudioCaptureException(UserCopy.Current.Text("error.micNotFound"), error);
        }

        var automaticGainEnabled = _automaticGainProvider?.Invoke() ?? true;
        var pipeline = new CapturedAudioFramePipeline(
            capture.WaveFormat,
            new AdaptiveMicrophoneGain(isEnabled: automaticGainEnabled)
        );
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // Dispose 後の cts.Token 参照で ObjectDisposedException にならないよう、先に捕捉する。
        var pumpToken = cts.Token;
        var frames = CreateFrameChannel();
        int captureGeneration;
        lock (_sync)
        {
            captureGeneration = ++_captureGeneration;
        }

        capture.DataAvailable += (_, args) => pipeline.Push(args.Buffer, args.BytesRecorded);
        capture.RecordingStopped += (_, _) => OnRecordingStopped(capture);

        lock (_sync)
        {
            _stopRequested = false;
            _ownedDevice = ownedDevice;
            _capture = capture;
            _pumpCts = cts;
            _frames = frames;
            // StartRecording より先に pump を登録し、StopAsync が orphan writer を
            // 先に閉じて FlushRemainder を落とす隙間を作らない。
            _pumpTask = Task.Run(
                () => PumpAsync(pipeline, frames.Writer, captureGeneration, _timeProvider, pumpToken),
                CancellationToken.None
            );
        }

        try
        {
            capture.StartRecording();
        }
        catch (Exception error) when (error is COMException or InvalidOperationException)
        {
            await StopAsync().ConfigureAwait(false);
            throw new AudioCaptureException(UserCopy.Current.Text("error.micStartFailed"), error);
        }
    }

    public async Task StopAsync()
    {
        Task? pump;
        ChannelWriter<CapturedAudioFrame>? orphanWriter;
        lock (_sync)
        {
            pump = _pumpTask;
            _pumpTask = null;
            // pump が一度も起動していない経路では Writer が未完了のまま残るため、ここで閉じる。
            orphanWriter = pump is null ? _frames.Writer : null;
        }

        StopCore();
        orphanWriter?.TryComplete();

        if (pump is not null)
        {
            try
            {
                await pump.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 停止時の pump 例外は握り潰し、呼び出しを完了させる。
            }
        }
    }

    public void Dispose() => StopAsync().GetAwaiter().GetResult();

    /// <summary>macOS の bufferingNewest(32) 相当。満杯時は oldest を捨てて最新を優先する。</summary>
    internal static Channel<CapturedAudioFrame> CreateFrameChannel(Action<CapturedAudioFrame>? onDropped = null) =>
        CreateFrameChannelCore(onDropped ?? CreateLoggingDropCallback());

    private static Action<CapturedAudioFrame> CreateLoggingDropCallback()
    {
        long droppedCount = 0;
        return _ =>
            AppLogger.Debug(
                LogCategory.Audio,
                $"DBG_CAPTURE_QUEUE_DROP count={Interlocked.Increment(ref droppedCount)}"
            );
    }

    private static Channel<CapturedAudioFrame> CreateFrameChannelCore(Action<CapturedAudioFrame> onDropped) =>
        AudioFrameChannel.CreateBounded(onDropped);

    private static async Task PumpAsync(
        CapturedAudioFramePipeline pipeline,
        ChannelWriter<CapturedAudioFrame> writer,
        int captureGeneration,
        TimeProvider timeProvider,
        CancellationToken cancellationToken
    )
    {
        long sequence = 0;
        using var timer = new PeriodicTimer(FrameInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                // 溜まり分はまとめて channel へ渡し、満杯時は DropOldest で遅延を落とす。
                // 端数の直後は無音を混ぜない。完全飢餓または端数タイムアウト時だけ無音 1 frame。
                var frames = pipeline.TakeTickFrames(Pcm16FramePacketizer.SamplesPerFrame);
                foreach (var frame in frames)
                {
                    writer.TryWrite(
                        new CapturedAudioFrame(
                            captureGeneration,
                            sequence++,
                            frame,
                            checked((int)pipeline.DiscardedMilliseconds),
                            timeProvider.GetTimestamp()
                        )
                    );
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 停止要求。frame stream を閉じて feeder を終わらせる。
        }
        finally
        {
            foreach (var frame in pipeline.FlushRemainder())
            {
                writer.TryWrite(
                    new CapturedAudioFrame(
                        captureGeneration,
                        sequence++,
                        frame,
                        checked((int)pipeline.DiscardedMilliseconds),
                        timeProvider.GetTimestamp()
                    )
                );
            }

            writer.TryComplete();
        }
    }

    /// <summary>
    /// デバイス取り外しや障害で録音が止まった場合、pump を終わらせて frame stream を閉じる。
    /// 無音を流し続けるとセッション側が異常に気付けないため、再接続経路へ倒す。
    /// </summary>
    private void OnRecordingStopped(WasapiCapture capture)
    {
        CancellationTokenSource? cts;
        lock (_sync)
        {
            if (_stopRequested || !ReferenceEquals(_capture, capture))
            {
                return;
            }

            // capture 自体は StopAsync/Dispose 側で解放する。ここでは pump だけ畳む。
            cts = _pumpCts;
            _pumpCts = null;
        }

        cts?.Cancel();
        cts?.Dispose();
    }

    private void StopCore()
    {
        WasapiCapture? capture;
        MMDevice? ownedDevice;
        CancellationTokenSource? cts;
        lock (_sync)
        {
            _stopRequested = true;
            capture = _capture;
            _capture = null;
            ownedDevice = _ownedDevice;
            _ownedDevice = null;
            cts = _pumpCts;
            _pumpCts = null;
        }

        // DataAvailable は capture スレッドから同期で来る。pump を先に cancel すると
        // FlushRemainder の後に Push が入り、停止時の端数が落ちる。
        // Dispose は capture スレッドを join するので、その後に pump を止める。
        if (capture is not null)
        {
            try
            {
                capture.StopRecording();
            }
            catch (Exception error) when (error is COMException or InvalidOperationException)
            {
                // 既に停止済みのデバイスは無視してよい。
            }

            capture.Dispose();
        }

        ownedDevice?.Dispose();

        cts?.Cancel();
        cts?.Dispose();
    }
}
