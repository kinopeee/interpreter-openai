using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using RealtimeTranslator.Core.Realtime;

namespace RealtimeTranslator.Core.Tests;

/// <summary>
/// Realtime サーバーの代役。既定では handshake (session.created / session.updated) を自動応答し、
/// 送信失敗・遅延・任意イベントの注入でエラー経路を再現する。
/// </summary>
internal sealed class FakeRealtimeServerTransport : IRealtimeWebSocketTransport
{
    private readonly Channel<byte[]> _inbound = Channel.CreateUnbounded<byte[]>();
    private readonly List<byte[]> _sent = [];
    private readonly object _sync = new();

    private bool _failNextSend;

    public bool AutoHandshake { get; set; } = true;

    /// <summary>
    /// graceful close 用の完了イベントを自動応答する。
    /// <c>session.close</c> → <c>session.closed</c>、
    /// <c>input_audio_buffer.commit</c> → transcription completed。
    /// </summary>
    public bool AutoCloseResponses { get; set; }

    public Exception? ConnectError { get; set; }

    /// <summary>設定している間、すべての send が失敗する。</summary>
    public Exception? SendError { get; set; }

    public TimeSpan SendDelay { get; set; }

    public int ConnectCount { get; private set; }

    public Uri? ConnectedUrl { get; private set; }

    public IReadOnlyDictionary<string, string> ConnectedHeaders { get; private set; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public int CloseCount { get; private set; }

    public IReadOnlyList<byte[]> Sent
    {
        get
        {
            lock (_sync)
            {
                return [.. _sent];
            }
        }
    }

    /// <summary>次の 1 回だけ send を失敗させる。fixture の translationSendFailure に対応する。</summary>
    public void FailNextSend()
    {
        lock (_sync)
        {
            _failNextSend = true;
        }
    }

    public void EnqueueJson(string json) => _inbound.Writer.TryWrite(Encoding.UTF8.GetBytes(json));

    public void EnqueueRaw(byte[] payload) => _inbound.Writer.TryWrite(payload);

    public Task ConnectAsync(Uri url, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            ConnectCount += 1;
            ConnectedUrl = url;
            ConnectedHeaders = new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase);
        }

        if (ConnectError is not null)
        {
            return Task.FromException(ConnectError);
        }

        if (AutoHandshake)
        {
            EnqueueJson("""{"type":"session.created"}""");
        }

        return Task.CompletedTask;
    }

    public async Task SendAsync(ReadOnlyMemory<byte> utf8Json, CancellationToken cancellationToken)
    {
        if (SendDelay > TimeSpan.Zero)
        {
            await Task.Delay(SendDelay, cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();

        bool failOnce;
        lock (_sync)
        {
            failOnce = _failNextSend;
            _failNextSend = false;
        }

        if (SendError is not null)
        {
            throw SendError;
        }

        if (failOnce)
        {
            throw new InvalidOperationException("injected send failure");
        }

        var payload = utf8Json.ToArray();
        lock (_sync)
        {
            _sent.Add(payload);
        }

        var type = TypeOf(payload);
        if (AutoHandshake && type == "session.update")
        {
            EnqueueJson("""{"type":"session.updated"}""");
        }

        if (AutoCloseResponses)
        {
            if (type == "session.close")
            {
                EnqueueJson("""{"type":"session.closed"}""");
            }
            else if (type == "input_audio_buffer.commit")
            {
                EnqueueJson(
                    """{"type":"conversation.item.input_audio_transcription.completed"}""");
            }
        }
    }

    public async Task<byte[]> ReceiveAsync(CancellationToken cancellationToken) =>
        await _inbound.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);

    public Task CloseAsync()
    {
        lock (_sync)
        {
            CloseCount += 1;
        }

        return Task.CompletedTask;
    }

    /// <summary>送信済み <c>input_audio_buffer.append</c> の音声 payload を復号して返す。</summary>
    public IReadOnlyList<string> AppendedFrameTexts()
    {
        var frames = new List<string>();
        foreach (var payload in Sent)
        {
            var node = JsonNode.Parse(payload)?.AsObject();
            // 原文接続は input_audio_buffer.append、翻訳接続は session.input_audio_buffer.append を使う。
            var type = node?["type"]?.GetValue<string>();
            if (type is not ("input_audio_buffer.append" or "session.input_audio_buffer.append"))
            {
                continue;
            }

            var audio = node!["audio"]?.GetValue<string>() ?? string.Empty;
            frames.Add(Encoding.UTF8.GetString(Convert.FromBase64String(audio)));
        }

        return frames;
    }

    private static string? TypeOf(byte[] payload) =>
        JsonNode.Parse(payload)?.AsObject()["type"]?.GetValue<string>();
}
