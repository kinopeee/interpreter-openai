using RealtimeTranslator.Core.Subtitles;
using Xunit;

namespace RealtimeTranslator.Core.Tests;

public sealed class SubtitleTranscriptSessionGateTests
{
    // Given: 字幕記録がオフ
    // When: 録音を開始する
    // Then: 開始マーカーは不要でセッションも開かない
    [Fact]
    public void BeginRecordingSkipsWhenOptedOut()
    {
        var gate = new SubtitleTranscriptSessionGate();

        Assert.False(gate.BeginRecording(recordSubtitles: false));
        Assert.False(gate.HasOpenSession);
    }

    // Given: 字幕記録がオン
    // When: 録音を開始する
    // Then: 開始マーカーが必要になりセッションが開く
    [Fact]
    public void BeginRecordingOpensWhenOptedIn()
    {
        var gate = new SubtitleTranscriptSessionGate();

        Assert.True(gate.BeginRecording(recordSubtitles: true));
        Assert.True(gate.HasOpenSession);
        Assert.False(gate.TryOpenBeforeAppend(recordSubtitles: true));
    }

    // Given: 録音中に OFF→ON へ切り替える
    // When: mid-recording opt-in を判定する
    // Then: 初回だけ開始マーカーが必要
    [Fact]
    public void MidRecordingOptInOpensOnce()
    {
        var gate = new SubtitleTranscriptSessionGate();

        Assert.True(gate.TryOpenOnMidRecordingOptIn(
            previouslyEnabled: false,
            nowEnabled: true,
            isActivelyRecording: true));
        Assert.True(gate.HasOpenSession);

        Assert.False(gate.TryOpenOnMidRecordingOptIn(
            previouslyEnabled: false,
            nowEnabled: true,
            isActivelyRecording: true));
    }

    // Given: 待機中または既にオン
    // When: mid-recording opt-in を判定する
    // Then: 開始マーカーは不要
    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    [InlineData(false, false, true)]
    public void MidRecordingOptInIgnoredOutsideTransition(
        bool previouslyEnabled,
        bool nowEnabled,
        bool isActivelyRecording)
    {
        var gate = new SubtitleTranscriptSessionGate();

        Assert.False(gate.TryOpenOnMidRecordingOptIn(
            previouslyEnabled,
            nowEnabled,
            isActivelyRecording));
        Assert.False(gate.HasOpenSession);
    }

    // Given: 録音中オプトイン前に確定ペアが来る想定
    // When: 追記前ゲートを通す
    // Then: 未開始なら開き、二度目は開かない
    [Fact]
    public void TryOpenBeforeAppendOpensOnceWhileRecording()
    {
        var gate = new SubtitleTranscriptSessionGate();

        Assert.True(gate.TryOpenBeforeAppend(recordSubtitles: true));
        Assert.True(gate.HasOpenSession);
        Assert.False(gate.TryOpenBeforeAppend(recordSubtitles: true));
    }

    // Given: 開いたセッション
    // When: Idle へ戻る
    // Then: 録音区間フラグだけ落ち、上限通知フラグは残る
    [Fact]
    public void ResetOnIdleClearsOpenSessionOnly()
    {
        var gate = new SubtitleTranscriptSessionGate();
        Assert.True(gate.BeginRecording(recordSubtitles: true));
        Assert.True(gate.TryAnnounceCap());

        gate.ResetOnIdle();

        Assert.False(gate.HasOpenSession);
        Assert.True(gate.DidAnnounceCap);
    }

    // Given: 上限到達
    // When: 通知を複数回試す
    // Then: 一度だけ true になり、クリア後は再開できる
    [Fact]
    public void CapAnnouncementIsOnceUntilReset()
    {
        var gate = new SubtitleTranscriptSessionGate();

        Assert.True(gate.TryAnnounceCap());
        Assert.False(gate.TryAnnounceCap());

        gate.ResetCapAnnouncement();

        Assert.True(gate.TryAnnounceCap());
    }

    // Given: 前セッションでマーカー済みのあと Idle リセット
    // When: 次の録音をオプトインで開始する
    // Then: 新しい録音区間として再度開始マーカーが必要
    [Fact]
    public void BeginRecordingAfterIdleReopensSession()
    {
        var gate = new SubtitleTranscriptSessionGate();
        Assert.True(gate.BeginRecording(recordSubtitles: true));
        gate.ResetOnIdle();

        Assert.True(gate.BeginRecording(recordSubtitles: true));
        Assert.True(gate.HasOpenSession);
    }
}
