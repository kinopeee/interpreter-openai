using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using RealtimeTranslator.Core.OpenAI;
using RealtimeTranslator.Core.Settings;
using RealtimeTranslator.Platform.Security;

namespace RealtimeTranslator.App;

/// <summary>設定ウィンドウ (一般 / 音声認識 / 字幕・操作)。値の変更は即座に永続化する。</summary>
public partial class SettingsWindow : Window
{
    /// <summary>入力の 1 文字ごとにセッションへ反映しないための待ち時間。</summary>
    public static readonly TimeSpan TuningDebounceInterval = TimeSpan.FromMilliseconds(800);

    private readonly CredentialManagerApiKeyStore _apiKeyStore;
    private readonly DispatcherTimer _tuningDebounce;
    private readonly Func<AppSettingsData> _currentSettings;

    private bool _loading = true;

    /// <param name="currentSettings">
    /// 開いている間にトレイ側 (字幕位置など) で変わった値を上書きしないよう、値は都度最新を読む。
    /// </param>
    public SettingsWindow(Func<AppSettingsData> currentSettings, CredentialManagerApiKeyStore apiKeyStore)
    {
        ArgumentNullException.ThrowIfNull(currentSettings);
        ArgumentNullException.ThrowIfNull(apiKeyStore);

        _currentSettings = currentSettings;
        _apiKeyStore = apiKeyStore;

        InitializeComponent();

        _tuningDebounce = new DispatcherTimer { Interval = TuningDebounceInterval };
        _tuningDebounce.Tick += OnTuningDebounceElapsed;

        NoiseReductionBox.ItemsSource = new[]
        {
            new ComboOption<RealtimeTranslationNoiseReduction>(RealtimeTranslationNoiseReduction.NearField, "近距離マイク"),
            new ComboOption<RealtimeTranslationNoiseReduction>(RealtimeTranslationNoiseReduction.FarField, "遠距離マイク"),
        };
        TranscriptionDelayBox.ItemsSource = new[]
        {
            new ComboOption<RealtimeTranscriptionDelay>(RealtimeTranscriptionDelay.Minimal, "最小"),
            new ComboOption<RealtimeTranscriptionDelay>(RealtimeTranscriptionDelay.Low, "低"),
            new ComboOption<RealtimeTranscriptionDelay>(RealtimeTranscriptionDelay.Medium, "中"),
            new ComboOption<RealtimeTranscriptionDelay>(RealtimeTranscriptionDelay.High, "高"),
            new ComboOption<RealtimeTranscriptionDelay>(RealtimeTranscriptionDelay.XHigh, "最高"),
        };
        PresetBox.ItemsSource = RealtimeSessionTuning.Preset.All
            .Select(preset => new ComboOption<RealtimeSessionTuning.Preset>(preset, preset.DisplayName))
            .ToArray();
        PresetBox.SelectedIndex = 0;

        LoadFromSettings();
        RefreshStoredKeyState();
        _loading = false;
    }

    /// <summary>設定値が変わったときに発火する。呼び出し側が永続化する。</summary>
    public event EventHandler<AppSettingsData>? SettingsChanged;

    /// <summary>録音中セッションへ反映すべき変更 (プロンプト・キーワード・遅延) を debounce 後に通知する。</summary>
    public event EventHandler? TuningChanged;

    private AppSettingsData Settings => _currentSettings();

    protected override void OnClosed(EventArgs e)
    {
        // 入力直後に閉じても debounce 待ちの変更を取りこぼさない。
        var hasPendingTuningChange = _tuningDebounce.IsEnabled;
        _tuningDebounce.Stop();
        _tuningDebounce.Tick -= OnTuningDebounceElapsed;
        if (hasPendingTuningChange)
        {
            TuningChanged?.Invoke(this, EventArgs.Empty);
        }

        base.OnClosed(e);
    }

    private void LoadFromSettings()
    {
        var settings = Settings;
        ConsentCheckBox.IsChecked = settings.HasAcceptedCurrentConsent;
        SelectOption(NoiseReductionBox, settings.NoiseReduction);
        SelectOption(TranscriptionDelayBox, settings.TranscriptionDelay);
        PromptBox.Text = settings.TranscriptionPrompt;
        KeywordsBox.Text = settings.TranscriptionKeywordsText;
        FontSizeSlider.Value = settings.FontSize;
        RecordSubtitlesCheckBox.IsChecked = settings.RecordSubtitles;
        UpdateFontSizeText();
        UpdateHintCounters();
    }

    private void Publish(AppSettingsData settings) => SettingsChanged?.Invoke(this, settings);

    private void ScheduleTuningChange()
    {
        _tuningDebounce.Stop();
        _tuningDebounce.Start();
    }

    private void OnTuningDebounceElapsed(object? sender, EventArgs e)
    {
        _tuningDebounce.Stop();
        TuningChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnConsentChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var accepted = ConsentCheckBox.IsChecked == true;
        Publish(Settings with
        {
            AcceptedConsentVersion = accepted ? AppSettingsData.CurrentConsentVersion : 0,
        });
    }

    private void OnApiKeyDraftChanged(object sender, RoutedEventArgs e) =>
        SaveApiKeyButton.IsEnabled = !string.IsNullOrWhiteSpace(ApiKeyBox.Password);

    private void OnSaveApiKey(object sender, RoutedEventArgs e)
    {
        try
        {
            _apiKeyStore.Save(ApiKeyBox.Password);
            ApiKeyBox.Clear();
            ShowApiKeyStatus("API キーを資格情報マネージャーへ保存しました", isError: false);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            ShowApiKeyStatus("API キーを保存できませんでした", isError: true);
        }

        RefreshStoredKeyState();
    }

    private void OnDeleteApiKey(object sender, RoutedEventArgs e)
    {
        try
        {
            _apiKeyStore.Delete();
            ApiKeyBox.Clear();
            ShowApiKeyStatus("API キーを削除しました", isError: false);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            ShowApiKeyStatus("API キーを削除できませんでした", isError: true);
        }

        RefreshStoredKeyState();
    }

    private void OnNoiseReductionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || SelectedEnum<RealtimeTranslationNoiseReduction>(NoiseReductionBox) is not { } value)
        {
            return;
        }

        // ノイズ低減は session.update では変えられないため、次回の録音開始から反映する。
        Publish(Settings with { NoiseReduction = value });
    }

    private void OnTranscriptionDelayChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || SelectedEnum<RealtimeTranscriptionDelay>(TranscriptionDelayBox) is not { } value)
        {
            return;
        }

        Publish(Settings with { TranscriptionDelay = value });
        ScheduleTuningChange();
    }

    private void OnPromptChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        Publish(Settings with { TranscriptionPrompt = PromptBox.Text });
        UpdateHintCounters();
        ScheduleTuningChange();
    }

    private void OnKeywordsChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        Publish(Settings with { TranscriptionKeywordsText = KeywordsBox.Text });
        UpdateHintCounters();
        ScheduleTuningChange();
    }

    private void OnFontSizeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateFontSizeText();
        if (_loading)
        {
            return;
        }

        Publish(Settings with { FontSize = AppSettingsCodec.ClampFontSize(FontSizeSlider.Value) });
    }

    private void OnRecordSubtitlesChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        Publish(Settings with { RecordSubtitles = RecordSubtitlesCheckBox.IsChecked == true });
    }

    private void OnApplyPreset(object sender, RoutedEventArgs e)
    {
        if (PresetBox.SelectedItem is not ComboOption<RealtimeSessionTuning.Preset> { Value: var preset })
        {
            return;
        }

        ApplyHints(preset.Prompt, RealtimeSessionTuning.KeywordsText(preset.Keywords));
    }

    private void OnRestoreDefaults(object sender, RoutedEventArgs e) => ApplyHints(
        RealtimeSessionTuning.DefaultPrompt,
        RealtimeSessionTuning.KeywordsText(RealtimeSessionTuning.DefaultKeywords));

    private void ApplyHints(string prompt, string keywordsText)
    {
        _loading = true;
        PromptBox.Text = prompt;
        KeywordsBox.Text = keywordsText;
        _loading = false;

        Publish(Settings with
        {
            TranscriptionPrompt = prompt,
            TranscriptionKeywordsText = keywordsText,
        });
        UpdateHintCounters();
        ScheduleTuningChange();
    }

    private void UpdateFontSizeText() => FontSizeText.Text = string.Create(
        CultureInfo.InvariantCulture,
        $"フォントサイズ: {(int)FontSizeSlider.Value}pt");

    private void UpdateHintCounters()
    {
        var promptLength = RealtimeSessionTuning.SanitizedPrompt(PromptBox.Text).Length;
        var isPromptOverLimit = PromptBox.Text.Length > RealtimeSessionTuning.PromptCharacterLimit;
        PromptCounterText.Text = string.Create(
            CultureInfo.InvariantCulture,
            $"{promptLength}/{RealtimeSessionTuning.PromptCharacterLimit} 文字")
            + (isPromptOverLimit ? "（超過分は切り詰められます）" : string.Empty);

        var keywordLines = KeywordsBox.Text
            .Split('\n')
            .Count(line => !string.IsNullOrWhiteSpace(line));
        var keywordCount = RealtimeSessionTuning.ParseKeywords(KeywordsBox.Text).Length;
        KeywordCounterText.Text = string.Create(
            CultureInfo.InvariantCulture,
            $"{keywordCount}/{RealtimeSessionTuning.KeywordLimit} 語")
            + (keywordLines > RealtimeSessionTuning.KeywordLimit ? "（超過分は送信されません）" : string.Empty);

        KeywordWarningText.Text = KeywordsBox.Text.IndexOfAny(['<', '>']) >= 0
            ? "「<」「>」は送信時に自動除去されます。"
            : string.Empty;
    }

    private void RefreshStoredKeyState()
    {
        // HasStoredKey は CredRead 失敗を false に畳むが、未知の Win32 失敗でも
        // 設定ウィンドウ構築・保存後更新でプロセスを落とさない。
        bool hasKey;
        try
        {
            hasKey = _apiKeyStore.HasStoredKey;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            hasKey = false;
            ShowApiKeyStatus("API キーの保存状態を確認できませんでした", isError: true);
        }

        StoredKeyStateText.Text = hasKey ? "資格情報マネージャーに保存済み" : "未保存";
        DeleteApiKeyButton.IsEnabled = hasKey;
    }

    private void ShowApiKeyStatus(string message, bool isError)
    {
        ApiKeyStatusText.Text = message;
        ApiKeyStatusText.Foreground = isError ? System.Windows.Media.Brushes.Firebrick : System.Windows.Media.Brushes.Gray;
    }

    private static void SelectOption<T>(Selector box, T value) =>
        box.SelectedItem = box.ItemsSource
            ?.OfType<ComboOption<T>>()
            .FirstOrDefault(option => Equals(option.Value, value));

    private static T? SelectedEnum<T>(Selector box)
        where T : struct =>
        box.SelectedItem is ComboOption<T> option ? option.Value : null;

    private sealed record ComboOption<T>(T Value, string DisplayName);
}
