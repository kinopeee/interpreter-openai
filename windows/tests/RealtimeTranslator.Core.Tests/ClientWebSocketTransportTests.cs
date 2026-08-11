using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using RealtimeTranslator.Core.OpenAI;
using RealtimeTranslator.Core.Realtime;
using Xunit;

namespace RealtimeTranslator.Core.Tests;

/// <summary>
/// 受信メッセージの累積上限と、connect timeout → Recoverable の本番 transport 契約。
/// send timeout は peer が読まないだけでは OS 送信バッファ次第で満たせず flaky なため、ここでは扱わない。
/// </summary>
public sealed class ClientWebSocketTransportTests
{
    // Given: 上限直下まで累積した受信状態
    // When: 残り容量ぴったりの fragment が届く
    // Then: 例外なく受け入れる
    [Fact]
    public void AcceptsFragmentThatExactlyFillsMessageLimit()
    {
        ClientWebSocketTransport.EnsureWithinMessageLimit(
            ClientWebSocketTransport.MaxMessageBytes - 1024,
            1024);
    }

    // Given: 上限直下まで累積した受信状態
    // When: 残り容量を 1 byte 超える fragment が届く
    // Then: 回復可能な transport error として弾く
    [Fact]
    public void RejectsFragmentThatExceedsMessageLimit()
    {
        var exception = Assert.Throws<RealtimeTranslationException>(() =>
            ClientWebSocketTransport.EnsureWithinMessageLimit(
                ClientWebSocketTransport.MaxMessageBytes - 1024,
                1025));

        Assert.Equal(RealtimeTranslationErrorKind.RecoverableTransportFailure, exception.Kind);
    }

    // Given: 未受信の状態
    // When: 上限を超える単一の巨大 frame が届く
    // Then: 確保・parse する前に弾く
    [Fact]
    public void RejectsSingleOversizedFrame()
    {
        Assert.Throws<RealtimeTranslationException>(() =>
            ClientWebSocketTransport.EnsureWithinMessageLimit(
                0,
                ClientWebSocketTransport.MaxMessageBytes + 1));
    }

    // Given: TCP は受けるが WebSocket ハンドシェイクを返さない loopback listener
    // When: 短い connectTimeout で ConnectAsync する
    // Then: 呼び出し側 cancel ではなく RecoverableTransportFailure になる
    [Fact]
    public async Task ConnectTimeoutMapsToRecoverableTransportFailure()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var accepted = new CancellationTokenSource();
            var acceptTask = AcceptAndHoldAsync(listener, accepted.Token);

            using var transport = new ClientWebSocketTransport(
                sendTimeout: TimeSpan.FromSeconds(5),
                connectTimeout: TimeSpan.FromMilliseconds(200));
            var error = await Assert.ThrowsAsync<RealtimeTranslationException>(
                () => transport.ConnectAsync(
                    new Uri($"ws://127.0.0.1:{port}/"),
                    new Dictionary<string, string>(StringComparer.Ordinal),
                    CancellationToken.None));

            Assert.Equal(RealtimeTranslationErrorKind.RecoverableTransportFailure, error.Kind);
            await accepted.CancelAsync();
            try
            {
                await acceptTask;
            }
            catch (OperationCanceledException)
            {
                // hold 解除。
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task AcceptAndHoldAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        using var client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
        // ハンドシェイク応答を返さず、connect timeout が発火するまでソケットを保持する。
        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // テスト終了。
        }
    }
}
