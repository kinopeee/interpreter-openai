using System;
using System.Threading;
using System.Threading.Tasks;

namespace RealtimeTranslator.Core.Realtime;

/// <summary>
/// 翻訳送信ポンプの task ハンドル・世代番号・transport halt・連続失敗数を管理する。
/// ポンプ本体のループはオーケストレーターが持ち、ここは帳簿だけを担う。
/// `DualRealtimeTranslationClient` の `_sync` ロック下だけで触るコンポーネント。
/// </summary>
internal sealed class TranslationPumpSupervisor
{
    private readonly int _failureLimit;

    public TranslationPumpSupervisor(DualRealtimeTranslationClientTuning tuning)
    {
        ArgumentNullException.ThrowIfNull(tuning);
        _failureLimit = tuning.ConsecutiveFailureLimit;
    }

    public Task? PumpTask { get; private set; }

    /// <summary>現在登録中のポンプ世代。古いポンプの終了処理が新ポンプの参照を消さない。</summary>
    public int Generation { get; private set; }

    /// <summary>transport failure 後は再接続まで翻訳ポンプを再開しない。</summary>
    public bool HaltedForTransportFailure { get; private set; }

    public int ConsecutiveFailures { get; private set; }

    public CancellationTokenSource Cancellation { get; private set; } = new();

    public bool IsTracked => PumpTask is not null;

    public bool ReachedFailureLimit => ConsecutiveFailures >= _failureLimit;

    /// <summary>新しい世代としてポンプを登録する。task が既にある場合のガードは呼び出し側。</summary>
    public void Start(Task task)
    {
        Generation += 1;
        PumpTask = task;
    }

    /// <summary>現在世代なら task を手放す。再開可否の判定は呼び出し側が行う。</summary>
    public bool FinishIfCurrent(int generation)
    {
        if (Generation != generation)
        {
            return false;
        }

        PumpTask = null;
        return true;
    }

    /// <summary>ポンプ参照を切り離して返す。cancel / await はロック外で呼び出し側が行う。</summary>
    public (Task? PumpTask, CancellationTokenSource Cancellation) Detach()
    {
        var task = PumpTask;
        var cts = Cancellation;
        Generation += 1;
        PumpTask = null;
        return (task, cts);
    }

    /// <summary>次の接続世代向けにキャンセル源を張り直す。</summary>
    public void RecycleCancellation()
    {
        Cancellation.Dispose();
        Cancellation = new CancellationTokenSource();
    }

    public void HaltForTransportFailure() => HaltedForTransportFailure = true;

    public int RecordFailure() => ++ConsecutiveFailures;

    public void ResetFailures() => ConsecutiveFailures = 0;

    public void Reset()
    {
        HaltedForTransportFailure = false;
        ConsecutiveFailures = 0;
    }
}
