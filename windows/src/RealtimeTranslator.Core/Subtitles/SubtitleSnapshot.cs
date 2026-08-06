using System;
using RealtimeTranslator.Core.Realtime;

namespace RealtimeTranslator.Core.Subtitles;

/// <summary>字幕スロット 1 件分の表示状態。macOS 版 <c>LiveSubtitle</c> と同義。</summary>
public readonly record struct LiveSubtitle(string SourceText, string TranslatedText)
{
    public static readonly LiveSubtitle Empty = new(string.Empty, string.Empty);

    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(SourceText) && string.IsNullOrWhiteSpace(TranslatedText);
}

/// <summary>オーバーレイに渡す 1 フレーム分の表示内容。</summary>
public readonly record struct SubtitleSnapshot(LiveSubtitle Current, string? StatusBanner)
{
    public static readonly SubtitleSnapshot Empty = new(LiveSubtitle.Empty, null);
}

/// <summary>
/// セッションイベントと接続状態から表示スナップショットを組み立てる。
/// WPF 非依存なので、表示ロジックの回帰はユニットテストで押さえられる。
/// </summary>
public sealed class SubtitleSnapshotBuilder
{
    public const string IdleBanner = "待機中 — Control + Alt + Space で録音開始";
    public const string ConnectingBanner = "接続中…";
    public const string ReconnectingBanner = "再接続中…";

    private LiveSubtitle _current = LiveSubtitle.Empty;
    private int _segmentGeneration;

    public SubtitleSnapshot Current { get; private set; } = new(LiveSubtitle.Empty, IdleBanner);

    public SubtitleSnapshot Apply(RealtimeSubtitleUpdate update, TranslationState state)
    {
        if (update.SegmentGeneration != _segmentGeneration)
        {
            _segmentGeneration = update.SegmentGeneration;
            _current = LiveSubtitle.Empty;
        }

        _current = new LiveSubtitle(update.SourceText, update.TranslatedText);
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
        return Publish(state);
    }

    private SubtitleSnapshot Publish(TranslationState state)
    {
        Current = new SubtitleSnapshot(_current, BannerFor(state, _current));
        return Current;
    }

    private static string? BannerFor(TranslationState state, LiveSubtitle current) => state switch
    {
        TranslationState.Connecting => ConnectingBanner,
        TranslationState.Reconnecting => ReconnectingBanner,
        // 表示中の字幕があるうちはバナーで覆わない。
        TranslationState.Idle => current.IsEmpty ? IdleBanner : null,
        // Error はトレイ側が状態を示す。空スロットで待機バナーを出すと失敗と矛盾する。
        TranslationState.Error => null,
        _ => null,
    };
}
