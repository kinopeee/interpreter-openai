namespace RealtimeTranslator.Core.Subtitles;

/// <summary>字幕スロット 1 件分の表示状態。macOS 版 <c>LiveSubtitle</c> と同義。</summary>
public readonly record struct LiveSubtitle(string SourceText, string TranslatedText, bool IsFinalized)
{
    public static readonly LiveSubtitle Empty = new(string.Empty, string.Empty, false);

    public bool IsEmpty => string.IsNullOrWhiteSpace(SourceText) && string.IsNullOrWhiteSpace(TranslatedText);
}

/// <summary>オーバーレイに渡す 1 フレーム分の表示内容。</summary>
public readonly record struct SubtitleSnapshot(LiveSubtitle Current, string? StatusBanner)
{
    public static readonly SubtitleSnapshot Empty = new(LiveSubtitle.Empty, null);
}
