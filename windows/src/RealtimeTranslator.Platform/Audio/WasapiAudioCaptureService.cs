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

namespace RealtimeTranslator.Platform.Audio;

public sealed class AudioCaptureException : Exception
{
    public AudioCaptureException()
        : this(UserCopy.Current.Text("error.micStartFailed"))
    {
    }

    public AudioCaptureException(string message)
        : base(message)
    {
    }

    public AudioCaptureException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>WASAPI 共有モードのマイク入力を 100 ms / 24 kHz / PCM16 mono frame として供給する。</summary>
public sealed class WasapiAudioCaptureService : IRealtimeAudioCapture, IDisposable
{
    /// <summary>
    /// macOS 版 <c>AsyncStream(bufferingNewest: 32)</c> と同じ上限。
    /// 送信遅延時は古い frame を捨て、無制限にメモリを伸ばさない。
    /// </summary>
    internal const int FrameChannelCapacity = 32;

    private static readonly TimeSpan FrameInterval =
        TimeSpan.FromMilliseconds(Pcm16FramePacketizer.FrameDurationMilliseconds);

    private readonly Func<MMDevice>? _deviceFactory;
    private readonly object _sync = new();

    private Channel<ReadOnlyMemory<byte>> _frames = CreateFrameChannel();
    private WasapiCapture? _capture;
    private MMDevice? _ownedDevice;
    private CancellationTokenSource? _pumpCts;
    private Task? _pumpTask;
    private bool _stopRequested = true;

    public WasapiAudioCaptureService(Func<MMDevice>? deviceFactory = null)
    {
        _deviceFactory = deviceFactory;
    }

    public ChannelReader<ReadOnlyMemory<byte>> Frames
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
                ownedDevice = new MMDeviceEnumerator().GetDefaultAudioEndpoint(
                    DataFlow.Capture,
                    Role.Communications);
                device = ownedDevice;
            }

            capture = new WasapiCapture(device);
        }
        catch (Exception error) when (error is COMException or InvalidOperationException or ArgumentException)
        {
            ownedDevice?.Dispose();
            throw new AudioCaptureException(UserCopy.Current.Text("error.micNotFound"), error);
        }

        var pipeline = new CapturedAudioFramePipeline(capture.WaveFormat);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // Dispose 後の cts.Token 参照で ObjectDisposedException にならないよう、先に捕捉する。
        var pumpToken = cts.Token;
        var frames = CreateFrameChannel();

        capture.DataAvailable += (_, args) => pipeline.Push(args.Buffer, args.BytesRecorded);
        capture.RecordingStopped += (_, _) => OnRecordingStopped(capture);

        lock (_sync)
        {
            _stopRequested = false;
            _ownedDevice = ownedDevice;
            _capture = capture;
            _pumpCts = cts;
            _frames = frames;
        }

        try
        {
            capture.StartRecording();
        }
        catch (Exception error) when (error is COMException or InvalidOperationException)
        {
            // pump 未起動のため StopAsync 側で writer を完了させる。
            await StopAsync().ConfigureAwait(false);
            throw new AudioCaptureException(UserCopy.Current.Text("error.micStartFailed"), error);
        }

        // StartRecording 成功後に pump を登録し、StopAsync と交差しても await 対象を失わない。
        var pumpTask = Task.Run(() => PumpAsync(pipeline, frames.Writer, pumpToken), CancellationToken.None);
        bool shouldAwaitOrphan;
        lock (_sync)
        {
            if (ReferenceEquals(_capture, capture) && ReferenceEquals(_pumpCts, cts))
            {
                _pumpTask = pumpTask;
                shouldAwaitOrphan = false;
            }
            else
            {
                shouldAwaitOrphan = true;
            }
        }

        if (shouldAwaitOrphan)
        {
            try
            {
                await pumpTask.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 停止経路の例外は呼び出し側へ伝播しない。
            }
        }
    }

    public async Task StopAsync()
    {
        Task? pump;
        ChannelWriter<ReadOnlyMemory<byte>>? orphanWriter;
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
    internal static Channel<ReadOnlyMemory<byte>> CreateFrameChannel() =>
        Channel.CreateBounded<ReadOnlyMemory<byte>>(
            new BoundedChannelOptions(FrameChannelCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true,
                AllowSynchronousContinuations = false,
            });

    private static async Task PumpAsync(
        CapturedAudioFramePipeline pipeline,
        ChannelWriter<ReadOnlyMemory<byte>> writer,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(FrameInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                var frames = pipeline.ReadFrames(Pcm16FramePacketizer.SamplesPerFrame);
                if (frames.Count == 0)
                {
                    // 契約: 録音中は無音 frame も 100ms ごとに送り続ける。
                    writer.TryWrite(new byte[Pcm16FramePacketizer.BytesPerFrame]);
                    continue;
                }

                foreach (var frame in frames)
                {
                    writer.TryWrite(frame);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 停止要求。frame stream を閉じて feeder を終わらせる。
        }
        finally
        {
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

        cts?.Cancel();
        cts?.Dispose();

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
    }
}
