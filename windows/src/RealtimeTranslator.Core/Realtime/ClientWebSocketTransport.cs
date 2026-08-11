using System;
using System.Buffers;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using RealtimeTranslator.Core.OpenAI;

namespace RealtimeTranslator.Core.Realtime;

/// <summary><see cref="ClientWebSocket"/> による本番 transport。送信は無期限待ちしないよう timeout を掛ける。</summary>
public sealed class ClientWebSocketTransport : IRealtimeWebSocketTransport, IDisposable
{
    /// <summary>1 メッセージの累積上限。字幕記録の 10 MB 上限に揃え、巨大応答による OOM を防ぐ。</summary>
    public const int MaxMessageBytes = 10 * 1024 * 1024;

    private static readonly TimeSpan DefaultSendTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(30);

    private readonly TimeSpan _sendTimeout;
    private readonly TimeSpan _connectTimeout;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly object _sync = new();

    private ClientWebSocket? _socket;

    public ClientWebSocketTransport(TimeSpan? sendTimeout = null, TimeSpan? connectTimeout = null)
    {
        _sendTimeout = sendTimeout ?? DefaultSendTimeout;
        _connectTimeout = connectTimeout ?? DefaultConnectTimeout;
    }

    public async Task ConnectAsync(
        Uri url,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(headers);

        await CloseAsync().ConfigureAwait(false);

        var socket = new ClientWebSocket();
        foreach (var header in headers)
        {
            socket.Options.SetRequestHeader(header.Key, header.Value);
        }

        lock (_sync)
        {
            _socket = socket;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_connectTimeout);
        try
        {
            await socket.ConnectAsync(url, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await CloseAsync().ConfigureAwait(false);
            throw new RealtimeTranslationException(
                RealtimeTranslationErrorKind.RecoverableTransportFailure);
        }
        catch (WebSocketException)
        {
            await CloseAsync().ConfigureAwait(false);
            throw new RealtimeTranslationException(
                RealtimeTranslationErrorKind.RecoverableTransportFailure);
        }
    }

    public async Task SendAsync(ReadOnlyMemory<byte> utf8Json, CancellationToken cancellationToken)
    {
        var socket = RequireSocket();
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_sendTimeout);
            try
            {
                await socket.SendAsync(utf8Json, WebSocketMessageType.Text, true, timeout.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new RealtimeTranslationException(
                    RealtimeTranslationErrorKind.RecoverableTransportFailure);
            }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException)
            {
                // CloseAsync と並行すると Abort/Dispose 済み socket へ触り得る。
                throw new RealtimeTranslationException(
                    RealtimeTranslationErrorKind.RecoverableTransportFailure);
            }
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public async Task<byte[]> ReceiveAsync(CancellationToken cancellationToken)
    {
        var socket = RequireSocket();
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            var message = new ArrayBufferWriter<byte>(16 * 1024);
            while (true)
            {
                ValueWebSocketReceiveResult result;
                try
                {
                    result = await socket.ReceiveAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException)
                {
                    // CloseAsync と並行すると Abort/Dispose 済み socket へ触り得る。
                    throw new RealtimeTranslationException(
                        RealtimeTranslationErrorKind.RecoverableTransportFailure);
                }

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    throw new RealtimeTranslationException(
                        RealtimeTranslationErrorKind.RecoverableTransportFailure);
                }

                EnsureWithinMessageLimit(message.WrittenCount, result.Count);
                message.Write(buffer.AsSpan(0, result.Count));
                if (result.EndOfMessage)
                {
                    return message.WrittenSpan.ToArray();
                }
            }
        }
        finally
        {
            // 音声・文字起こしの残骸を pool 経由で他所へ渡さない。
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    public Task CloseAsync()
    {
        ClientWebSocket? socket;
        lock (_sync)
        {
            socket = _socket;
            _socket = null;
        }

        if (socket is null)
        {
            return Task.CompletedTask;
        }

        // close handshake の完了を待つと再接続が遅れるため、abort で即座に解放する。
        socket.Abort();
        socket.Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _socket?.Dispose();
            _socket = null;
        }

        _sendGate.Dispose();
    }

    /// <summary>上限超過は確保・parse 前に回復可能な transport error として捨てる。</summary>
    internal static void EnsureWithinMessageLimit(int writtenCount, int incomingCount)
    {
        if (incomingCount > MaxMessageBytes - writtenCount)
        {
            throw new RealtimeTranslationException(
                RealtimeTranslationErrorKind.RecoverableTransportFailure);
        }
    }

    private ClientWebSocket RequireSocket()
    {
        lock (_sync)
        {
            return _socket ?? throw new RealtimeTranslationException(RealtimeTranslationErrorKind.NotConnected);
        }
    }
}
