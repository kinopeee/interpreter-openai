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

    private string _sourceText = string.Empty;
    private string _translatedText = string.Empty;
    private string? _statusBanner = SubtitleSnapshotBuilder.IdleBanner;
    private double _fontSize = AppSettingsData.DefaultFontSize;
    private bool _isEditingPosition;

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

    public double FontSize
    {
        get => _fontSize;
        set
        {
            if (Set(ref _fontSize, AppSettingsCodec.ClampFontSize(value)))
            {
                OnPropertyChanged(nameof(SourceFontSize));
                OnPropertyChanged(nameof(BannerFontSize));
            }
        }
    }

    public double SourceFontSize => FontSize * SourceFontScale;

    public double BannerFontSize => Math.Max(MinimumBannerFontSize, FontSize * BannerFontScale);

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

        SourceText = source;
        OnPropertyChanged(nameof(HasSourceText));
        TranslatedText = translated;
        OnPropertyChanged(nameof(HasTranslatedText));
        StatusBanner = snapshot.StatusBanner;
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
