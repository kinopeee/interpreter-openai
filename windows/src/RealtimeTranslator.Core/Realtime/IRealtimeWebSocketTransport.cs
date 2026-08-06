using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RealtimeTranslator.Core.Realtime;

/// <summary>Realtime 接続が使う WebSocket の最小インターフェース。テストでは fake を差し込む。</summary>
public interface IRealtimeWebSocketTransport
{
    Task ConnectAsync(Uri url, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken);

    Task SendAsync(ReadOnlyMemory<byte> utf8Json, CancellationToken cancellationToken);

    Task<byte[]> ReceiveAsync(CancellationToken cancellationToken);

    Task CloseAsync();
}
