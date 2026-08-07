using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using RealtimeTranslator.Core.Settings;

namespace RealtimeTranslator.Core.Subtitles;

/// <summary>
/// 字幕オーバーレイの表示状態。WPF 型に依存しないので表示ロジックだけを単体テストできる。
/// </summary>
public sealed class SubtitleOverlayViewModel : INotifyPropertyChanged
{
    public const double SourceFontScale = 0.85;
    public const double BannerFontScale = 0.45;
    public const double MinimumBannerFontSize = 14;
    public const double SourceOpacity = 0.7;
    /// 行間はフォントサイズ比例。macOS の lineSpacing は既定行送りへの「加算」なので、
    /// WPF では既定行送り (Segoe UI の FontFamily.LineSpacing = 1.3333) に fontSize/10 を足す。
    /// FontSize * 1.1 にすると WPF では既定より行間が「詰まる」ので誤り。
    public const double DefaultLineSpacingRatio = 1.3333;
    public const double AddedLineSpacingRatio = 0.1;
    public const double LineHeightRatio = DefaultLineSpacingRatio + AddedLineSpacingRatio;

    /// 行数で高さが動かないよう常に確保する行数。macOS 版 currentLineLimit と同じ。
    public const int ReservedLineCount = 2;

    /// 訳文が未確定である間だけ末尾へ添える記号。
    public const string PendingMarker = "…";
    /// Aggregator の確定句読点に加え、表示用マーカー抑制のため末尾の `…` も見る。
    /// （`……` の誤記を避ける。Aggregator の確定条件自体は変えない。）
    private const string TerminalPunctuation = "。．.!？?！…";

    private string _sourceText = string.Empty;
    private string _translatedText = string.Empty;
    private string? _statusBanner = SubtitleSnapshotBuilder.IdleBanner;
    private double _fontSize = AppSettingsData.DefaultFontSize;
    private bool _isEditingPosition;
    private bool _isFinalized;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>表示上限で末尾側に切り詰めた原文。</summary>
    public string SourceText
    {
        get => _sourceText;
        private set => Set(ref _sourceText, value);
    }

    public string TranslatedText
    {
        get => _translatedText;
        private set => Set(ref _translatedText, value);
    }

    public string? StatusBanner
    {
        get => _statusBanner;
        private set
        {
            if (Set(ref _statusBanner, value))
            {
                OnPropertyChanged(nameof(HasStatusBanner));
            }
        }
    }

    public bool HasStatusBanner => !string.IsNullOrWhiteSpace(StatusBanner);

    public bool HasSourceText => !string.IsNullOrWhiteSpace(SourceText);

    public bool HasTranslatedText => !string.IsNullOrWhiteSpace(TranslatedText);

    public bool IsFinalized
    {
        get => _isFinalized;
        private set => Set(ref _isFinalized, value);
    }

    public double FontSize
    {
        get => _fontSize;
        set
        {
            if (Set(ref _fontSize, AppSettingsCodec.ClampFontSize(value)))
            {
                OnPropertyChanged(nameof(SourceFontSize));
                OnPropertyChanged(nameof(BannerFontSize));
                OnPropertyChanged(nameof(TranslatedLineHeight));
                OnPropertyChanged(nameof(SourceLineHeight));
                OnPropertyChanged(nameof(TranslatedSlotHeight));
                OnPropertyChanged(nameof(SourceSlotHeight));
            }
        }
    }

    public double SourceFontSize => FontSize * SourceFontScale;

    public double BannerFontSize => Math.Max(MinimumBannerFontSize, FontSize * BannerFontScale);

    public double TranslatedLineHeight => FontSize * LineHeightRatio;

    public double SourceLineHeight => SourceFontSize * LineHeightRatio;

    public double TranslatedSlotHeight => TranslatedLineHeight * ReservedLineCount;

    public double SourceSlotHeight => SourceLineHeight * ReservedLineCount;

    /// 未確定かつ字幕が空でなく、訳文が文末記号で終わらない場合だけマーカーを出す。
    /// 文末記号で終わる訳文へ足すと「are.…」のような誤記に見えるため出さない。
    public bool ShowsPendingMarker =>
        !IsFinalized
        && !(string.IsNullOrWhiteSpace(SourceText) && string.IsNullOrWhiteSpace(TranslatedText))
        && !EndsWithTerminalPunctuation(TranslatedText);

    public string PendingMarkerText => ShowsPendingMarker ? PendingMarker : string.Empty;

    /// 訳文本体かマーカーのどちらかが出るなら訳文スロットを可視にする。
    public bool HasVisibleTranslation => HasTranslatedText || ShowsPendingMarker;

    /// <summary>位置編集中はクリックスルーを解除し、枠線を出す。</summary>
    public bool IsEditingPosition
    {
        get => _isEditingPosition;
        set => Set(ref _isEditingPosition, value);
    }

    public void Apply(SubtitleSnapshot snapshot)
    {
        var source = SubtitleTailClipper.Clip(snapshot.Current.SourceText);
        var translated = SubtitleTailClipper.Clip(snapshot.Current.TranslatedText);
        // 空白だけの訳文は「未着」と同じ扱い。不可視スペース＋… にしない。
        if (string.IsNullOrWhiteSpace(translated))
        {
            translated = string.Empty;
        }

        SourceText = source;
        OnPropertyChanged(nameof(HasSourceText));
        TranslatedText = translated;
        OnPropertyChanged(nameof(HasTranslatedText));
        IsFinalized = snapshot.Current.IsFinalized;
        OnPropertyChanged(nameof(ShowsPendingMarker));
        OnPropertyChanged(nameof(PendingMarkerText));
        OnPropertyChanged(nameof(HasVisibleTranslation));
        StatusBanner = snapshot.StatusBanner;
    }

    private static bool EndsWithTerminalPunctuation(string? text)
    {
        var trimmed = text?.Trim();
        return !string.IsNullOrEmpty(trimmed) && TerminalPunctuation.Contains(trimmed[^1]);
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged(string? propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
