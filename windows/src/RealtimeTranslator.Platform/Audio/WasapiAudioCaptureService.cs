using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using RealtimeTranslator.Core.Audio;
using RealtimeTranslator.Core.Realtime;

namespace RealtimeTranslator.Platform.Audio;

public sealed class AudioCaptureException : Exception
{
    public AudioCaptureException()
        : this("マイクを開始できませんでした")
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
    private static readonly TimeSpan FrameInterval =
        TimeSpan.FromMilliseconds(Pcm16FramePacketizer.FrameDurationMilliseconds);

    private readonly Func<MMDevice>? _deviceFactory;
    private readonly object _sync = new();

    private Channel<ReadOnlyMemory<byte>> _frames = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
    private WasapiCapture? _capture;
    private MMDevice? _ownedDevice;
    private CancellationTokenSource? _pumpCts;
    private Task? _pumpTask;
<<<<<<< HEAD
    private bool _stopRequested;
=======
    private int _stopping = 1;
>>>>>>> 290004f (Fix WASAPI capture lifecycle races and guarantee silence frames)

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
            throw new AudioCaptureException("マイクが見つかりません", error);
        }

        var pipeline = new CapturedAudioFramePipeline(capture.WaveFormat);
<<<<<<< HEAD
        capture.DataAvailable += (_, args) => pipeline.Push(args.Buffer, args.BytesRecorded);
        capture.RecordingStopped += (_, _) => OnRecordingStopped(capture);

=======
>>>>>>> 290004f (Fix WASAPI capture lifecycle races and guarantee silence frames)
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var frames = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
        var writer = frames.Writer;

        void OnDataAvailable(object? sender, WaveInEventArgs args) =>
            pipeline.Push(args.Buffer, args.BytesRecorded);

        void OnRecordingStopped(object? sender, StoppedEventArgs args)
        {
            // 意図停止以外（デバイス抜去など）は frame stream を閉じてセッション側へ再接続を促す。
            if (Volatile.Read(ref _stopping) != 0)
            {
                return;
            }

            writer.TryComplete();
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Stop と交差した場合は無視する。
            }
        }

        capture.DataAvailable += OnDataAvailable;
        capture.RecordingStopped += OnRecordingStopped;

        lock (_sync)
        {
            Volatile.Write(ref _stopping, 0);
            _ownedDevice = ownedDevice;
            _capture = capture;
            _pumpCts = cts;
            _frames = frames;
            _stopRequested = false;
        }

        try
        {
            capture.StartRecording();
        }
        catch (Exception error) when (error is COMException or InvalidOperationException)
        {
            await StopAsync().ConfigureAwait(false);
            throw new AudioCaptureException("マイクを開始できませんでした", error);
        }

        // StartRecording 成功後に pump を登録し、StopAsync と交差しても await 対象を失わない。
        var pumpTask = Task.Run(() => PumpAsync(pipeline, writer, cts.Token), CancellationToken.None);
        lock (_sync)
        {
            if (ReferenceEquals(_capture, capture) && ReferenceEquals(_pumpCts, cts))
            {
                _pumpTask = pumpTask;
            }
        }

        if (Volatile.Read(ref _stopping) != 0)
        {
            // Start 直後に Stop された場合は、登録できなかった pump も回収する。
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
        lock (_sync)
        {
            Volatile.Write(ref _stopping, 1);
            pump = _pumpTask;
            _pumpTask = null;
        }

        StopCore();
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
<<<<<<< HEAD
            _stopRequested = true;
=======
            Volatile.Write(ref _stopping, 1);
>>>>>>> 290004f (Fix WASAPI capture lifecycle races and guarantee silence frames)
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
