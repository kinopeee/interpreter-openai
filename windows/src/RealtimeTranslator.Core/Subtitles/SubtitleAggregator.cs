using System;
using RealtimeTranslator.Core.Localization;
using RealtimeTranslator.Core.Realtime;

namespace RealtimeTranslator.Core.Subtitles;

/// <summary>
/// セッションイベントと接続状態から表示スナップショットを組み立てる。
/// WPF 非依存なので、表示ロジックの回帰はユニットテストで押さえられる。
/// macOS 版 <c>SubtitleAggregator</c> と同義。
/// </summary>
public sealed class SubtitleAggregator
{
    public const string DefaultIdleHotkey = "Ctrl + Alt + Space";

    public static string IdleBanner => IdleBannerFor(DefaultIdleHotkey);

    public static string ConnectingBanner => UserCopy.Current.Text("banner.connecting");

    public static string ReconnectingBanner => UserCopy.Current.Text("banner.reconnecting");

    private readonly string _idleBanner;
    private LiveSubtitle _current = LiveSubtitle.Empty;
    private int _segmentGeneration;
    private long _lastSequence;

    public SubtitleAggregator(string? idleBanner = null)
    {
        _idleBanner = idleBanner ?? IdleBanner;
        Current = new SubtitleSnapshot(LiveSubtitle.Empty, _idleBanner);
    }

    public static string IdleBannerFor(string hotkey) => UserCopy.Current.Format("banner.idle", "hotkey", hotkey);

    public SubtitleSnapshot Current { get; private set; }

    public SubtitleSnapshot Apply(RealtimeSubtitleUpdate update, TranslationState state)
    {
        if (update.Sequence != 0 && update.Sequence <= _lastSequence)
        {
            return Current;
        }

        if (update.Sequence != 0)
        {
            _lastSequence = update.Sequence;
        }

        if (update.IsInvalidation)
        {
            _segmentGeneration = update.SegmentGeneration;
            if (!_current.IsFinalized)
            {
                _current = LiveSubtitle.Empty;
            }

            return Publish(state);
        }

        if (update.SegmentGeneration != _segmentGeneration)
        {
            _segmentGeneration = update.SegmentGeneration;
            _current = LiveSubtitle.Empty;
        }

        _current = new LiveSubtitle(update.SourceText, update.TranslatedText, update.ShouldFinalize);
        if (update.ShouldFinalize)
        {
            _segmentGeneration = update.SegmentGeneration + 1;
        }

        return Publish(state);
    }

    public SubtitleSnapshot Apply(TranslationState state) => Publish(state);

    public SubtitleSnapshot Reset(TranslationState state)
    {
        _current = LiveSubtitle.Empty;
        _lastSequence = 0;
        return Publish(state);
    }

    private SubtitleSnapshot Publish(TranslationState state)
    {
        Current = new SubtitleSnapshot(_current, BannerFor(state, _current));
        return Current;
    }

    private string? BannerFor(TranslationState state, LiveSubtitle current) =>
        state switch
        {
            TranslationState.Connecting => ConnectingBanner,
            TranslationState.Reconnecting => ReconnectingBanner,
            // 表示中の字幕があるうちはバナーで覆わない。
            TranslationState.Idle => current.IsEmpty ? _idleBanner : null,
            // Error はトレイ側が状態を示す。空スロットで待機バナーを出すと失敗と矛盾する。
            TranslationState.Error => null,
            _ => null,
        };
}
