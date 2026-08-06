using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using RealtimeTranslator.Core.OpenAI;
using RealtimeTranslator.Core.Realtime;
using RealtimeTranslator.Core.Settings;
using RealtimeTranslator.Core.Subtitles;
using RealtimeTranslator.Platform.App;
using RealtimeTranslator.Platform.Audio;
using RealtimeTranslator.Platform.Logging;
using RealtimeTranslator.Platform.Security;
using RealtimeTranslator.Platform.Settings;

namespace RealtimeTranslator.App;

/// <summary>合成ルート。tray 常駐なのでメインウィンドウは持たず、明示終了でのみプロセスを閉じる。</summary>
public partial class App : Application, IDisposable
{
    private readonly SubtitleSnapshotBuilder _snapshots = new();
    private readonly SubtitleOverlayViewModel _overlayViewModel = new();
    private readonly AppSettingsStore _settingsStore = new();
    private readonly DispatcherTimer _subtitleClear = new() { Interval = SubtitleClearDelay };
    private readonly DispatcherTimer _settingsSaveDebounce = new()
    {
        Interval = SettingsWindow.TuningDebounceInterval,
    };

    private SingleInstanceLease? _lease;
    private CredentialManagerApiKeyStore? _apiKeyStore;
    private WasapiAudioCaptureService? _capture;
    private DualRealtimeTranslationClient? _dualClient;
    private InterpretationSession? _session;
    private GlobalHotkeyManager? _hotkey;
    private TrayController? _tray;
    private SubtitleOverlayWindow? _overlay;
    private SettingsWindow? _settingsWindow;
    private HwndSource? _overlaySource;

    private AppSettingsData _settings = AppSettingsData.Default;
    private AppSettingsData? _pendingSettingsSave;
    private TranslationState _state = TranslationState.Idle;
    private bool _isEditingPosition;
    private bool _shuttingDown;

    /// <summary>録音停止から字幕を消すまでの猶予 (macOS 版と同じ)。</summary>
    public static readonly TimeSpan SubtitleClearDelay = TimeSpan.FromSeconds(5);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _lease = SingleInstanceLease.TryAcquire();
        if (_lease is null)
        {
            MessageBox.Show(
                "Realtime Translator は既に起動しています。",
                "Realtime Translator",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        _settings = _settingsStore.Load();
        _overlayViewModel.FontSize = _settings.FontSize;

        _apiKeyStore = new CredentialManagerApiKeyStore();
        _capture = new WasapiAudioCaptureService();
        _dualClient = CreateDualClient();
        _session = new InterpretationSession(
            _apiKeyStore,
            _capture,
            _dualClient,
            () => _settings.Tuning());
        _session.StateChanged += OnSessionStateChanged;
        _session.SubtitleUpdated += OnSubtitleUpdated;
        _session.MessageEncountered += OnSessionMessage;

        _tray = new TrayController();
        _tray.StartStopRequested += (_, _) => ToggleTranslation();
        _tray.EditPositionRequested += (_, _) => ToggleEditingPosition();
        _tray.SettingsRequested += (_, _) => ShowSettings();
        _tray.ExitRequested += (_, _) => BeginShutdown();

        _overlay = new SubtitleOverlayWindow(_overlayViewModel);
        _overlayViewModel.Apply(_snapshots.Apply(TranslationState.Idle));
        _overlay.Show();
        _overlay.ApplyPlacement(_settings.HasCustomOverlayOrigin, _settings.OverlayOriginX, _settings.OverlayOriginY);

        _subtitleClear.Tick += OnSubtitleClearElapsed;
        _settingsSaveDebounce.Tick += OnSettingsSaveDebounceElapsed;

        RegisterHotkey();
        AppLogger.Info(LogCategory.General, "app started");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        FlushSettingsSave();
        DisposeResources();
        base.OnExit(e);
    }

    private static DualRealtimeTranslationClient CreateDualClient()
    {
        var safetyIdentifier = new InstallIdentifierStore().SafetyIdentifier();
        return new DualRealtimeTranslationClient(
            new RealtimeSourceTranscriptionConnection(new ClientWebSocketTransport(), safetyIdentifier),
            new RealtimeTranslationConnection(
                RealtimeTranslationOutputLanguage.English,
                new ClientWebSocketTransport(),
                safetyIdentifier),
            new RealtimeTranslationConnection(
                RealtimeTranslationOutputLanguage.Japanese,
                new ClientWebSocketTransport(),
                safetyIdentifier));
    }

    private void RegisterHotkey()
    {
        if (_overlay is null)
        {
            return;
        }

        _overlaySource = HwndSource.FromHwnd(_overlay.Handle);
        _overlaySource?.AddHook(HandleWindowMessage);

        _hotkey = new GlobalHotkeyManager();
        _hotkey.Pressed += (_, _) => ToggleTranslation();
        if (!_hotkey.Register(_overlay.Handle))
        {
            AppLogger.Warning(LogCategory.General, "global hotkey registration failed");
            _tray?.ShowMessage("Ctrl + Alt + Space を登録できませんでした。トレイメニューから操作してください。");
        }
    }

    private IntPtr HandleWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_hotkey?.HandleMessage(msg, wParam) == true)
        {
            handled = true;
        }

        return IntPtr.Zero;
    }

    private void ToggleTranslation()
    {
        if (_session is null || _shuttingDown)
        {
            return;
        }

        switch (_state)
        {
            case TranslationState.Idle:
            case TranslationState.Error:
                BeginTranslation();
                break;
            case TranslationState.Connecting:
            case TranslationState.Listening:
            case TranslationState.Reconnecting:
                _ = RunGuarded(_session.StopAsync);
                break;
            case TranslationState.Closing:
            default:
                break;
        }
    }

    private void BeginTranslation()
    {
        if (!_settings.HasAcceptedCurrentConsent)
        {
            ShowSettings();
            _tray?.ShowMessage("録音を開始する前に、設定で OpenAI への送信に同意してください。");
            return;
        }

        if (_apiKeyStore?.HasStoredKey != true)
        {
            ShowSettings();
            _tray?.ShowMessage("録音を開始する前に、設定で OpenAI API キーを保存してください。");
            return;
        }

        _ = RunGuarded(_session!.StartAsync);
    }

    private void ToggleEditingPosition()
    {
        if (_overlay is null)
        {
            return;
        }

        _isEditingPosition = !_isEditingPosition;
        _overlay.SetEditingPosition(_isEditingPosition);
        _tray?.SetEditingPosition(_isEditingPosition);

        if (!_isEditingPosition)
        {
            UpdateSettings(_settings with
            {
                HasCustomOverlayOrigin = true,
                OverlayOriginX = _overlay.Left,
                OverlayOriginY = _overlay.Top,
            });
        }
    }

    private void ShowSettings()
    {
        if (_settingsWindow is { IsVisible: true })
        {
            _settingsWindow.Activate();
            return;
        }

        // 設定ウィンドウは開いている間にトレイから変わる値 (字幕位置) を上書きしないよう、常に最新を読む。
        var window = new SettingsWindow(() => _settings, _apiKeyStore!);
        window.SettingsChanged += (_, settings) => UpdateSettings(settings);
        window.TuningChanged += (_, _) =>
        {
            if (_session is not null)
            {
                _ = RunGuarded(_session.ApplyTuningChangeAsync);
            }
        };
        window.Closed += (_, _) =>
        {
            // 入力直後に閉じても debounce 待ちの保存を取りこぼさない。
            FlushSettingsSave();
            _settingsWindow = null;
        };
        _settingsWindow = window;
        window.Show();
        window.Activate();
    }

    private void UpdateSettings(AppSettingsData settings)
    {
        // メモリ上の設定とフォントは即時反映し、ディスク書き込みだけ debounce する。
        _settings = settings;
        _overlayViewModel.FontSize = settings.FontSize;
        _pendingSettingsSave = settings;
        _settingsSaveDebounce.Stop();
        _settingsSaveDebounce.Start();
    }

    private void OnSettingsSaveDebounceElapsed(object? sender, EventArgs e) => FlushSettingsSave();

    private void FlushSettingsSave()
    {
        _settingsSaveDebounce.Stop();
        if (_pendingSettingsSave is not { } settings)
        {
            return;
        }

        _pendingSettingsSave = null;
        _settingsStore.Save(settings);
    }

    private void OnSessionStateChanged(object? sender, TranslationState state) =>
        Dispatcher.InvokeAsync(() =>
        {
            _state = state;
            _tray?.UpdateState(state);
            _overlayViewModel.Apply(_snapshots.Apply(state));
            ScheduleSubtitleClear(state);
        });

    private void OnSubtitleUpdated(object? sender, RealtimeSubtitleUpdate update) =>
        Dispatcher.InvokeAsync(() =>
        {
            _subtitleClear.Stop();
            _overlayViewModel.Apply(_snapshots.Apply(update, _state));
        });

    /// <summary>録音停止後は約 5 秒で最後の字幕を消す (録音中はタイマー消去しない)。</summary>
    private void ScheduleSubtitleClear(TranslationState state)
    {
        _subtitleClear.Stop();
        if (state is TranslationState.Idle or TranslationState.Error)
        {
            _subtitleClear.Start();
        }
    }

    private void OnSubtitleClearElapsed(object? sender, EventArgs e)
    {
        _subtitleClear.Stop();
        _overlayViewModel.Apply(_snapshots.Reset(_state));
    }

    private void OnSessionMessage(object? sender, string message) =>
        Dispatcher.InvokeAsync(() =>
        {
            // 本文 (原文・訳文) は通知に載せないので、セッション側の要約メッセージだけを出す。
            // ShowMessage は空文字を拒否するため、Dispatcher 内例外で落とさないよう先に弾く。
            if (_state == TranslationState.Error && !string.IsNullOrWhiteSpace(message))
            {
                _tray?.ShowMessage(message);
            }
        });

    public void Dispose()
    {
        DisposeResources();
        GC.SuppressFinalize(this);
    }

    private static async Task RunGuarded(Func<Task> operation)
    {
        try
        {
            await operation().ConfigureAwait(true);
        }
        catch (RealtimeTranslationException)
        {
            AppLogger.Error(LogCategory.General, "session operation failed");
        }
        catch (AudioCaptureException)
        {
            AppLogger.Error(LogCategory.Audio, "audio capture failed");
        }
    }

    private void BeginShutdown()
    {
        if (_shuttingDown)
        {
            return;
        }

        _shuttingDown = true;
        _ = ShutdownAsync();
    }

    private async Task ShutdownAsync()
    {
        if (_session is not null)
        {
            await RunGuarded(_session.StopAsync).ConfigureAwait(true);
        }

        Shutdown();
    }

    private void DisposeResources()
    {
        FlushSettingsSave();
        _subtitleClear.Stop();
        _subtitleClear.Tick -= OnSubtitleClearElapsed;
        _settingsSaveDebounce.Stop();
        _settingsSaveDebounce.Tick -= OnSettingsSaveDebounceElapsed;
        _hotkey?.Unregister();
        if (_overlaySource is not null)
        {
            _overlaySource.RemoveHook(HandleWindowMessage);
            _overlaySource = null;
        }

        _settingsWindow?.Close();
        _overlay?.Close();
        _tray?.Dispose();
        _hotkey?.Dispose();
        _session?.Dispose();
        _dualClient?.Dispose();
        _capture?.Dispose();
        _lease?.Dispose();
    }
}
