namespace RealtimeTranslator.Core.Subtitles;

/// <summary>
/// オプトイン字幕記録のセッション開始マーカーと上限通知の状態機械。
/// App 層の配線を Core で単体検証できるように切り出す。
/// </summary>
public sealed class SubtitleTranscriptSessionGate
{
    /// <summary>現在の録音区間でセッション開始マーカーを既に書いたか。</summary>
    public bool HasOpenSession { get; private set; }

    /// <summary>サイズ上限バナーを既に一度出したか。</summary>
    public bool DidAnnounceCap { get; private set; }

    /// <summary>
    /// 録音開始時に呼び出す。フラグをリセットし、オプトイン済みなら開始マーカー追記が必要。
    /// </summary>
    public bool BeginRecording(bool recordSubtitles)
    {
        HasOpenSession = false;
        if (!recordSubtitles)
        {
            return false;
        }

        HasOpenSession = true;
        return true;
    }

    /// <summary>
    /// 録音中に OFF→ON へ切り替えたときだけ開始マーカーが必要。
    /// 既にマーカー済みなら false（再有効化での重複防止）。
    /// </summary>
    public bool TryOpenOnMidRecordingOptIn(
        bool previouslyEnabled,
        bool nowEnabled,
        bool isActivelyRecording)
    {
        if (!isActivelyRecording || previouslyEnabled || !nowEnabled || HasOpenSession)
        {
            return false;
        }

        HasOpenSession = true;
        return true;
    }

    /// <summary>確定ペア追記前。オプトイン済みで未開始なら開始マーカーが必要。</summary>
    public bool TryOpenBeforeAppend(bool recordSubtitles)
    {
        if (!recordSubtitles || HasOpenSession)
        {
            return false;
        }

        HasOpenSession = true;
        return true;
    }

    /// <summary>Idle / Error へ戻ったときに録音区間フラグだけ落とす。</summary>
    public void ResetOnIdle() => HasOpenSession = false;

    /// <summary>サイズ上限到達を一度だけ通知する。</summary>
    public bool TryAnnounceCap()
    {
        if (DidAnnounceCap)
        {
            return false;
        }

        DidAnnounceCap = true;
        return true;
    }

    /// <summary>字幕記録クリア後に上限通知を再開できるようにする。</summary>
    public void ResetCapAnnouncement() => DidAnnounceCap = false;
}
