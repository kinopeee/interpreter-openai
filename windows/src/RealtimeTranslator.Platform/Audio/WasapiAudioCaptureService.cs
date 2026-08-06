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
    private CancellationTokenSource? _pumpCts;
    private Task? _pumpTask;
    private bool _stopRequested;

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

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        StopCore();

        MMDevice device;
        WasapiCapture capture;
        try
        {
            device = _deviceFactory is not null
                ? _deviceFactory()
                : new MMDeviceEnumerator().GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            capture = new WasapiCapture(device);
        }
        catch (Exception error) when (error is COMException or InvalidOperationException or ArgumentException)
        {
            throw new AudioCaptureException("マイクが見つかりません", error);
        }

        var pipeline = new CapturedAudioFramePipeline(capture.WaveFormat);
        capture.DataAvailable += (_, args) => pipeline.Push(args.Buffer, args.BytesRecorded);
        capture.RecordingStopped += (_, _) => OnRecordingStopped(capture);

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = cts.Token;
        Channel<ReadOnlyMemory<byte>> frames = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
        lock (_sync)
        {
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
            StopCore();
            throw new AudioCaptureException("マイクを開始できませんでした", error);
        }

        _pumpTask = Task.Run(() => PumpAsync(pipeline, frames.Writer, token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        Task? pump;
        lock (_sync)
        {
            pump = _pumpTask;
            _pumpTask = null;
        }

        StopCore();
        if (pump is not null)
        {
            await pump.ConfigureAwait(false);
        }
    }

    public void Dispose() => StopCore();

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
                foreach (var frame in pipeline.ReadFrames(Pcm16FramePacketizer.SamplesPerFrame))
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
        CancellationTokenSource? cts;
        lock (_sync)
        {
            _stopRequested = true;
            capture = _capture;
            _capture = null;
            cts = _pumpCts;
            _pumpCts = null;
        }

        cts?.Cancel();
        cts?.Dispose();

        if (capture is null)
        {
            return;
        }

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
}
