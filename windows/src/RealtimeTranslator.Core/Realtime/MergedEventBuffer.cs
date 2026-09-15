using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using RealtimeTranslator.Core.OpenAI;

namespace RealtimeTranslator.Core.Realtime;

/// <summary>
/// `events` 出力 channel、接続世代ごとの `EventDeliveryState`、merge 配送の帳簿を管理する。
/// `DualRealtimeTranslationClient` の `_sync` ロック下だけで状態を書き換えるコンポーネント。
/// </summary>
internal sealed class MergedEventBuffer
{
    public Channel<RealtimeTranslationStreamEvent> Events { get; private set; } =
        RealtimeEventChannel.Create();

    public EventDeliveryState DeliveryState { get; private set; } = new(0);

    public EventDeliveryWriter? MergeWriter { get; private set; }

    public CancellationTokenSource? MergeCts { get; private set; }

    /// <summary>merge ループ本体はオーケストレーターが起動し、ここへ登録する。</summary>
    public Task? MergeTask { get; set; }

    public ChannelReader<RealtimeTranslationStreamEvent> Reader => Events.Reader;

    public ChannelWriter<RealtimeTranslationStreamEvent> Writer => Events.Writer;

    /// <summary>新しい接続世代向けに channel と deliveryState を張り直す。</summary>
    public void Recreate(int epoch)
    {
        Events = RealtimeEventChannel.Create();
        DeliveryState = new EventDeliveryState(epoch);
        MergeWriter = null;
        MergeCts = null;
        MergeTask = null;
    }

    /// <summary>merge 配送 writer とキャンセル源を用意する。task 起動は呼び出し側。</summary>
    public EventDeliveryWriter ArmMerge()
    {
        var cts = new CancellationTokenSource();
        var writer = new EventDeliveryWriter(
            Events.Writer,
            DeliveryState,
            EventDeliveryStage.Merge,
            RealtimeEventChannel.Capacity);
        MergeCts = cts;
        MergeWriter = writer;
        return writer;
    }

    /// <summary>merge の帳簿を切り離して返す。cancel / await / complete はロック外で呼び出し側が行う。</summary>
    public (
        CancellationTokenSource? Cts,
        Task? Task,
        EventDeliveryWriter? Writer,
        EventDeliveryState State) DetachMerge()
    {
        var detached = (MergeCts, MergeTask, MergeWriter, DeliveryState);
        MergeCts = null;
        MergeTask = null;
        MergeWriter = null;
        return detached;
    }

    /// <summary>Dispose 経路でキャンセル源だけを切り離す。</summary>
    public CancellationTokenSource? DetachMergeCts()
    {
        var cts = MergeCts;
        MergeCts = null;
        return cts;
    }
}
