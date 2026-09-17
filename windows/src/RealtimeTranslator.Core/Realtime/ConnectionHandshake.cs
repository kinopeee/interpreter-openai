using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace RealtimeTranslator.Core.Realtime;

/// <summary>
/// 原文 + 翻訳 lane の並列 handshake。1 本が失敗したら残りを即キャンセルし、
/// 最初の失敗を <see cref="ExceptionDispatchInfo"/> で元の例外として再送出する。
/// </summary>
internal static class ConnectionHandshake
{
    internal static async Task StartAllAsync(IReadOnlyCollection<Task> starts, CancellationTokenSource handshakeCts)
    {
        // Swift の throwing TaskGroup と同じく、1 本が失敗したら残り handshake を
        // timeout まで待たずキャンセルし、ready leftover をすぐ ForceClose する。
        Exception? handshakeFault = null;
        var pending = new List<Task>(starts);
        while (pending.Count > 0)
        {
            var done = await Task.WhenAny(pending).ConfigureAwait(false);
            pending.Remove(done);
            if (done.IsCompletedSuccessfully)
            {
                continue;
            }

            try
            {
                await done.ConfigureAwait(false);
            }
            catch (Exception error)
            {
                handshakeFault = error;
            }

            await handshakeCts.CancelAsync().ConfigureAwait(false);
            break;
        }

        try
        {
            await Task.WhenAll(starts).ConfigureAwait(false);
        }
        catch (Exception)
        {
            if (handshakeFault is not null)
            {
                ExceptionDispatchInfo.Capture(handshakeFault).Throw();
            }

            throw;
        }

        if (handshakeFault is not null)
        {
            ExceptionDispatchInfo.Capture(handshakeFault).Throw();
        }
    }
}
