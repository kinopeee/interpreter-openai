using System;
using System.Threading;

namespace RealtimeTranslator.Platform.App;

/// <summary>
/// 名前付き Mutex による多重起動防止。ログオンセッション単位 (Local\) で 1 プロセスだけを通す。
/// プロセスが落ちれば OS が Mutex を解放するため、stale lock は残らない。
/// </summary>
public sealed class SingleInstanceLease : IDisposable
{
    public const string DefaultName = @"Local\RealtimeTranslator.SingleInstance";

    private Mutex? _mutex;

    private SingleInstanceLease(Mutex mutex)
    {
        _mutex = mutex;
    }

    /// <summary>取得できなければ <c>null</c>。既に別インスタンスが起動している。</summary>
    public static SingleInstanceLease? TryAcquire(string name = DefaultName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        // Mutex は同一スレッドでは再入できてしまうため、所有権ではなく生成有無で判定する。
        // プロセスが落ちればカーネルオブジェクトも消えるので、stale lock は残らない。
        var mutex = new Mutex(initiallyOwned: true, name, out var createdNew);
        if (createdNew)
        {
            return new SingleInstanceLease(mutex);
        }

        mutex.Dispose();
        return null;
    }

    public void Dispose()
    {
        var mutex = _mutex;
        _mutex = null;
        if (mutex is null)
        {
            return;
        }

        try
        {
            mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // 所有していない場合は解放不要。
        }

        mutex.Dispose();
    }
}
