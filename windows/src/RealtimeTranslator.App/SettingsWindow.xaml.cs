using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using RealtimeTranslator.Core.Audio;
using RealtimeTranslator.Core.Localization;
using RealtimeTranslator.Core.OpenAI;
using RealtimeTranslator.Core.Security;
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
        ApplyCopy();

        _tuningDebounce = new DispatcherTimer { Interval = TuningDebounceInterval };
        _tuningDebounce.Tick += OnTuningDebounceElapsed;

        NoiseReductionBox.ItemsSource = new[]
        {
            new ComboOption<RealtimeTranslationNoiseReduction>(
                RealtimeTranslationNoiseReduction.NearField,
                UiCopy.NoiseName(RealtimeTranslationNoiseReduction.NearField)),
            new ComboOption<RealtimeTranslationNoiseReduction>(
                RealtimeTranslationNoiseReduction.FarField,
                UiCopy.NoiseName(RealtimeTranslationNoiseReduction.FarField)),
        };
        LanguagePairBox.ItemsSource = new[]
        {
            new ComboOption<LanguagePair>(LanguagePair.JaEn, UiCopy.PairName(LanguagePair.JaEn)),
            new ComboOption<LanguagePair>(LanguagePair.JaEs, UiCopy.PairName(LanguagePair.JaEs)),
            new ComboOption<LanguagePair>(LanguagePair.EnEs, UiCopy.PairName(LanguagePair.EnEs)),
        };
        TranscriptionDelayBox.ItemsSource = new[]
        {
            new ComboOption<RealtimeTranscriptionDelay>(
                RealtimeTranscriptionDelay.Minimal,
                UiCopy.DelayName(RealtimeTranscriptionDelay.Minimal)),
            new ComboOption<RealtimeTranscriptionDelay>(
                RealtimeTranscriptionDelay.Low,
                UiCopy.DelayName(RealtimeTranscriptionDelay.Low)),
            new ComboOption<RealtimeTranscriptionDelay>(
                RealtimeTranscriptionDelay.Medium,
                UiCopy.DelayName(RealtimeTranscriptionDelay.Medium)),
            new ComboOption<RealtimeTranscriptionDelay>(
                RealtimeTranscriptionDelay.High,
                UiCopy.DelayName(RealtimeTranscriptionDelay.High)),
            new ComboOption<RealtimeTranscriptionDelay>(
                RealtimeTranscriptionDelay.XHigh,
                UiCopy.DelayName(RealtimeTranscriptionDelay.XHigh)),
        };
        UiLanguageBox.ItemsSource = new[]
        {
            new ComboOption<UiLanguagePreference>(
                UiLanguagePreference.System,
                UiCopy.Text("settings.uiLanguage.system")),
            new ComboOption<UiLanguagePreference>(
                UiLanguagePreference.Ja,
                UiCopy.Text("settings.uiLanguage.ja")),
            new ComboOption<UiLanguagePreference>(
                UiLanguagePreference.En,
                UiCopy.Text("settings.uiLanguage.en")),
        };
        PresetBox.ItemsSource = RealtimeSessionTuning.Preset.All
            .Select(preset => new ComboOption<RealtimeSessionTuning.Preset>(
                preset,
                UiCopy.PresetName(preset.Id)))
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
        SelectOption(LanguagePairBox, settings.LanguagePair);
        SelectOption(TranscriptionDelayBox, settings.TranscriptionDelay);
        SelectOption(UiLanguageBox, settings.UiLanguage);
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
            ShowApiKeyStatus(UiCopy.Text("settings.apiKeySaveOk.windows"), isError: false);
        }
        catch (ApiKeyFormatException error)
        {
            ShowApiKeyStatus(error.Message, isError: true);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            ShowApiKeyStatus(UiCopy.Text("settings.apiKeySaveFailed"), isError: true);
        }

        RefreshStoredKeyState();
    }

    private void OnDeleteApiKey(object sender, RoutedEventArgs e)
    {
        try
        {
            _apiKeyStore.Delete();
            ApiKeyBox.Clear();
            ShowApiKeyStatus(UiCopy.Text("settings.apiKeyDeleteOk"), isError: false);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            ShowApiKeyStatus(UiCopy.Text("settings.apiKeyDeleteFailed"), isError: true);
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

    private void OnLanguagePairChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || SelectedEnum<LanguagePair>(LanguagePairBox) is not { } value)
        {
            return;
        }

        var next = Settings with { LanguagePair = value };
        var refreshedHints = false;
        if (IsKnownDefaultPrompt(Settings.TranscriptionPrompt))
        {
            next = next with { TranscriptionPrompt = RealtimeSessionTuning.DefaultPromptForPair(value) };
            refreshedHints = true;
        }

        if (IsKnownDefaultKeywordsText(Settings.TranscriptionKeywordsText))
        {
            next = next with
            {
                TranscriptionKeywordsText = RealtimeSessionTuning.KeywordsText(
                    RealtimeSessionTuning.DefaultKeywordsForPair(value)),
            };
            refreshedHints = true;
        }

        if (refreshedHints)
        {
            _loading = true;
            PromptBox.Text = next.TranscriptionPrompt;
            KeywordsBox.Text = next.TranscriptionKeywordsText;
            _loading = false;
            Publish(next);
            UpdateHintCounters();
            ScheduleTuningChange();
            return;
        }

        Publish(next);
    }

    private void OnUiLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || SelectedEnum<UiLanguagePreference>(UiLanguageBox) is not { } value)
        {
            return;
        }

        // 反映は再起動後。開いているウィンドウは描き直さない。
        Publish(Settings with { UiLanguage = value });
    }

    private static bool IsKnownDefaultPrompt(string prompt) =>
        prompt == RealtimeSessionTuning.DefaultPromptForPair(LanguagePair.JaEn)
        || prompt == RealtimeSessionTuning.DefaultPromptForPair(LanguagePair.JaEs)
        || prompt == RealtimeSessionTuning.DefaultPromptForPair(LanguagePair.EnEs);

    private static bool IsKnownDefaultKeywordsText(string keywordsText) =>
        keywordsText == RealtimeSessionTuning.KeywordsText(
            RealtimeSessionTuning.DefaultKeywordsForPair(LanguagePair.JaEn))
        || keywordsText == RealtimeSessionTuning.KeywordsText(
            RealtimeSessionTuning.DefaultKeywordsForPair(LanguagePair.JaEs))
        || keywordsText == RealtimeSessionTuning.KeywordsText(
            RealtimeSessionTuning.DefaultKeywordsForPair(LanguagePair.EnEs));

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
        RealtimeSessionTuning.DefaultPromptForPair(Settings.LanguagePair),
        RealtimeSessionTuning.KeywordsText(
            RealtimeSessionTuning.DefaultKeywordsForPair(Settings.LanguagePair)));

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

    private void UpdateFontSizeText() =>
        FontSizeText.Text = UiCopy.Format(
            "settings.fontSize",
            "size",
            ((int)FontSizeSlider.Value).ToString(CultureInfo.InvariantCulture));

    private void UpdateHintCounters()
    {
        // 表示件数・超過警告は送信値と同じ正規化（改行潰し / 書記素クラスタ / 送信対象語）で揃える。
        var promptLength = RealtimeSessionTuning.CountTextElements(
            RealtimeSessionTuning.SanitizedPrompt(PromptBox.Text));
        var isPromptOverLimit = RealtimeSessionTuning.IsPromptOverCharacterLimit(PromptBox.Text);
        PromptCounterText.Text = UiCopy.Format(
            "settings.promptCounter",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["count"] = promptLength.ToString(CultureInfo.InvariantCulture),
                ["limit"] = RealtimeSessionTuning.PromptCharacterLimit.ToString(CultureInfo.InvariantCulture),
            })
            + (isPromptOverLimit ? UiCopy.Text("settings.promptOverLimit") : string.Empty);

        var keywordCount = RealtimeSessionTuning.ParseKeywords(KeywordsBox.Text).Length;
        var isKeywordOverLimit = RealtimeSessionTuning.IsKeywordCountOverLimit(KeywordsBox.Text);
        KeywordCounterText.Text = UiCopy.Format(
            "settings.keywordCounter",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["count"] = keywordCount.ToString(CultureInfo.InvariantCulture),
                ["limit"] = RealtimeSessionTuning.KeywordLimit.ToString(CultureInfo.InvariantCulture),
            })
            + (isKeywordOverLimit ? UiCopy.Text("settings.keywordOverLimit") : string.Empty);

        KeywordWarningText.Text = KeywordsBox.Text.IndexOfAny(['<', '>']) >= 0
            ? UiCopy.Text("settings.keywordForbidden")
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
            ShowApiKeyStatus(UiCopy.Text("settings.apiKeyStatusUnknown"), isError: true);
        }

        StoredKeyStateText.Text = hasKey
            ? UiCopy.Text("settings.apiKeySaved.windows")
            : UiCopy.Text("settings.apiKeyNotSaved");
        DeleteApiKeyButton.IsEnabled = hasKey;
    }

    private void ShowApiKeyStatus(string message, bool isError)
    {
        ApiKeyStatusText.Text = message;
        ApiKeyStatusText.Foreground = isError ? System.Windows.Media.Brushes.Firebrick : System.Windows.Media.Brushes.Gray;
    }

    private void ApplyCopy()
    {
        Title = UiCopy.Text("settings.windowTitle");
        GeneralTab.Header = UiCopy.Text("settings.tab.general");
        SpeechTab.Header = UiCopy.Text("settings.tab.speech");
        SubtitlesTab.Header = UiCopy.Text("settings.tab.subtitles");
        OpenAiSectionTitle.Text = UiCopy.Text("settings.section.openai");
        ModelText.Text = UiCopy.Text("settings.model") + ": gpt-live-transcribe / gpt-realtime-translate";
        LanguagePairLabel.Text = UiCopy.Text("settings.languagePair");
        LanguagePairAppliesNextRecordingText.Text = UiCopy.Text("settings.languagePairAppliesNextRecording");
        SubtitleDisplayText.Text = UiCopy.Text("settings.subtitleDisplay") + ": " + UiCopy.Text("settings.subtitleDisplayValue");
        TranslatedAudioText.Text = UiCopy.Text("settings.translatedAudio") + ": " + UiCopy.Text("settings.translatedAudioValue");
        ConsentCheckBox.Content = UiCopy.Text("settings.consentToggle");
        ConsentHelpText.Text = UiCopy.Text("settings.consentHelp");
        ApiKeySectionTitle.Text = UiCopy.Text("settings.section.apiKey");
        SaveApiKeyButton.Content = UiCopy.Text("settings.save");
        DeleteApiKeyButton.Content = UiCopy.Text("settings.delete");
        ApiKeyStorageHelpText.Text = UiCopy.Text("settings.apiKeyStorageHelp.windows");
        UiLanguageLabel.Text = UiCopy.Text("settings.uiLanguage");
        UiLanguageRestartHint.Text = UiCopy.Text("settings.uiLanguageRestartHint");
        AppVersionText.Text = UiCopy.Format("settings.appVersion", "version", AppReleaseVersionInfo.CurrentDisplayValue());
        RecognitionSectionTitle.Text = UiCopy.Text("settings.section.recognition");
        NoiseReductionLabel.Text = UiCopy.Text("settings.noiseReduction");
        TranscriptionDelayLabel.Text = UiCopy.Text("settings.transcriptionDelay");
        DelayHelpText.Text = UiCopy.Text("settings.delayHelp");
        ApplyPresetButton.Content = UiCopy.Text("settings.applyPreset");
        RestoreDefaultsButton.Content = UiCopy.Text("settings.restoreDefaults");
        HintsSectionTitle.Text = UiCopy.Text("settings.section.hints");
        PromptTitleText.Text = UiCopy.Text("settings.promptTitle");
        PromptHelpText.Text = UiCopy.Text("settings.promptHelp");
        KeywordsTitleText.Text = UiCopy.Text("settings.keywordsTitle");
        TuningLiveHelpText.Text = UiCopy.Text("settings.tuningLiveHelp");
        SubtitlesSectionTitle.Text = UiCopy.Text("settings.section.subtitles");
        RecordSubtitlesCheckBox.Content = UiCopy.Text("settings.recordSubtitles");
        RecordSubtitlesHelpText.Text = UiCopy.Text("settings.recordSubtitlesHelp.windows");
        ControlsSectionTitle.Text = UiCopy.Text("settings.section.controls");
        ControlsHelpText.Text = UiCopy.Text("settings.controlsHelp.windows");
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
