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
/// 受信メッセージの累積上限、connect timeout → Recoverable、handshake の HTTP ステータス分類の本番 transport 契約。
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
        ClientWebSocketTransport.EnsureWithinMessageLimit(ClientWebSocketTransport.MaxMessageBytes - 1024, 1024);
    }

    // Given: 上限直下まで累積した受信状態
    // When: 残り容量を 1 byte 超える fragment が届く
    // Then: 回復可能な transport error として弾く
    [Fact]
    public void RejectsFragmentThatExceedsMessageLimit()
    {
        var exception = Assert.Throws<RealtimeTranslationException>(() =>
            ClientWebSocketTransport.EnsureWithinMessageLimit(ClientWebSocketTransport.MaxMessageBytes - 1024, 1025)
        );

        Assert.Equal(RealtimeTranslationErrorKind.RecoverableTransportFailure, exception.Kind);
    }

    // Given: 未受信の状態
    // When: 上限を超える単一の巨大 frame が届く
    // Then: 確保・parse する前に弾く
    [Fact]
    public void RejectsSingleOversizedFrame()
    {
        Assert.Throws<RealtimeTranslationException>(() =>
            ClientWebSocketTransport.EnsureWithinMessageLimit(0, ClientWebSocketTransport.MaxMessageBytes + 1)
        );
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
                connectTimeout: TimeSpan.FromMilliseconds(200)
            );
            var error = await Assert.ThrowsAsync<RealtimeTranslationException>(() =>
                transport.ConnectAsync(
                    new Uri($"ws://127.0.0.1:{port}/"),
                    new Dictionary<string, string>(StringComparer.Ordinal),
                    CancellationToken.None
                )
            );

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

    // Given: 未接続の本番 transport
    // When: Send / Receive する
    // Then: 無期限待ちせず NotConnected になる
    [Fact]
    public async Task SendAndReceiveBeforeConnectAreNotConnected()
    {
        using var transport = new ClientWebSocketTransport();

        var sendError = await Assert.ThrowsAsync<RealtimeTranslationException>(() =>
            transport.SendAsync("{"u8.ToArray(), CancellationToken.None)
        );
        var receiveError = await Assert.ThrowsAsync<RealtimeTranslationException>(() =>
            transport.ReceiveAsync(CancellationToken.None)
        );

        Assert.Equal(RealtimeTranslationErrorKind.NotConnected, sendError.Kind);
        Assert.Equal(RealtimeTranslationErrorKind.NotConnected, receiveError.Kind);
    }

    // Given: 未接続の本番 transport
    // When: CloseAsync する
    // Then: 例外なく完了する
    [Fact]
    public async Task CloseWhenNeverConnectedIsNoOp()
    {
        using var transport = new ClientWebSocketTransport();
        await transport.CloseAsync();
    }

    // Given: TCP は受けるが WebSocket ハンドシェイクを返さない loopback listener
    // When: 呼び出し側 token をキャンセルする
    // Then: connect timeout の Recoverable ではなく OperationCanceledException になる
    [Fact]
    public async Task ConnectCanceledByCallerPropagatesCancellation()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var accepted = new CancellationTokenSource();
            var tcpAccepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var acceptTask = AcceptAndHoldAsync(listener, accepted.Token, tcpAccepted);

            using var transport = new ClientWebSocketTransport(
                sendTimeout: TimeSpan.FromSeconds(5),
                connectTimeout: TimeSpan.FromSeconds(30)
            );
            using var caller = new CancellationTokenSource();
            var connectTask = transport.ConnectAsync(
                new Uri($"ws://127.0.0.1:{port}/"),
                new Dictionary<string, string>(StringComparer.Ordinal),
                caller.Token
            );

            await tcpAccepted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await caller.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connectTask);

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

    // Given: shared fixture の handshake HTTP ステータス各 case
    // When: ConnectAsync の失敗分類へステータスを渡す
    // Then: 401 / 403 は再接続しない AuthenticationFailed、429 は RecoverableServerError、
    //       それ以外とステータス不明は従来どおり RecoverableTransportFailure になる
    [Fact]
    public void HandshakeFailureKindMatchesFixture()
    {
        foreach (var item in SharedFixtures.Load("server-error")["handshakeHttpStatus"]!["cases"]!.AsArray())
        {
            var name = SharedFixtures.Text(item!["name"]);
            var error = ClientWebSocketTransport.HandshakeFailure(SharedFixtures.OptionalNumber(item["statusCode"]));
            var expectedKind = SharedFixtures.Text(item["expected"]!["termination"]) switch
            {
                "authenticationFailed" => RealtimeTranslationErrorKind.AuthenticationFailed,
                "recoverableServerError" => RealtimeTranslationErrorKind.RecoverableServerError,
                "transportFailure" => RealtimeTranslationErrorKind.RecoverableTransportFailure,
                _ => throw new Xunit.Sdk.XunitException("unknown termination"),
            };
            Assert.True(expectedKind == error.Kind, name);
            Assert.True(
                (SharedFixtures.Text(item["expected"]!["disposition"]) == "recover") == error.IsRecoverable,
                name
            );
        }
    }

    // Given: ClientWebSocket が返す HttpStatusCode
    // When: handshake の HTTP ステータスとして取り出す
    // Then: 未取得（0）と upgrade 成功（101）はステータス不明、それ以外は数値のまま
    [Theory]
    [InlineData(0, null)]
    [InlineData(101, null)]
    [InlineData(401, 401)]
    [InlineData(429, 429)]
    [InlineData(503, 503)]
    public void HandshakeHttpStatusCodeTreatsMissingAndUpgradeAsUnknown(int raw, int? expected)
    {
        Assert.Equal(expected, ClientWebSocketTransport.HandshakeHttpStatusCode((HttpStatusCode)raw));
    }

    // Given: WebSocket upgrade 要求へ HTTP 401 / 429 を返す loopback listener
    // When: ConnectAsync する
    // Then: 401 は再接続しない AuthenticationFailed、429 は RecoverableServerError になる
    [Theory]
    [InlineData("401 Unauthorized", RealtimeTranslationErrorKind.AuthenticationFailed)]
    [InlineData("429 Too Many Requests", RealtimeTranslationErrorKind.RecoverableServerError)]
    public async Task ConnectRejectedWithHttpStatusMapsToClassifiedKind(
        string statusLine,
        RealtimeTranslationErrorKind expected
    )
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var accepted = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var respondTask = RespondWithStatusAsync(listener, statusLine, accepted.Token);

            using var transport = new ClientWebSocketTransport(
                sendTimeout: TimeSpan.FromSeconds(5),
                connectTimeout: TimeSpan.FromSeconds(5)
            );
            var error = await Assert.ThrowsAsync<RealtimeTranslationException>(() =>
                transport.ConnectAsync(
                    new Uri($"ws://127.0.0.1:{port}/"),
                    new Dictionary<string, string>(StringComparer.Ordinal),
                    CancellationToken.None
                )
            );

            Assert.Equal(expected, error.Kind);
            await respondTask;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task RespondWithStatusAsync(
        TcpListener listener,
        string statusLine,
        CancellationToken cancellationToken
    )
    {
        using var client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
        var stream = client.GetStream();
        var request = new byte[4096];
        var received = new System.Text.StringBuilder();
        while (!received.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            var count = await stream.ReadAsync(request, cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                return;
            }

            received.Append(System.Text.Encoding.ASCII.GetString(request, 0, count));
        }

        var response = System.Text.Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {statusLine}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
        );
        await stream.WriteAsync(response, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task AcceptAndHoldAsync(
        TcpListener listener,
        CancellationToken cancellationToken,
        TaskCompletionSource? tcpAccepted = null
    )
    {
        using var client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
        tcpAccepted?.TrySetResult();
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
