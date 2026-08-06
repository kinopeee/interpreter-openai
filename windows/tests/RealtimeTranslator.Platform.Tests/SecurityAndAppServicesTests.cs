using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using RealtimeTranslator.Core.Security;
using RealtimeTranslator.Platform.App;
using RealtimeTranslator.Platform.Logging;
using RealtimeTranslator.Platform.Security;
using Xunit;

namespace RealtimeTranslator.Platform.Tests;

/// <summary>資格情報保管・多重起動防止・ホットキー・ログ伏字の契約。</summary>
public sealed class SecurityAndAppServicesTests
{
    // Given: テスト専用ターゲット名の資格情報ストア
    // When: 保存・読み出し・削除する
    // Then: 資格情報マネージャーを往復し、削除後は null になる
    [Fact]
    public void CredentialStoreRoundTripsTheApiKey()
    {
        var target = $"RealtimeTranslator.Tests:{Guid.NewGuid()}";
        var store = new CredentialManagerApiKeyStore(target);
        try
        {
            Assert.Null(store.Load());

            store.Save("  sk-unit-test-value  ");

            Assert.Equal("sk-unit-test-value", store.Load());
            Assert.True(store.HasStoredKey);
        }
        finally
        {
            store.Delete();
        }

        Assert.Null(store.Load());
    }

    // Given: 未保存のターゲット
    // When: 削除する
    // Then: 例外にせず何も起きない
    [Fact]
    public void DeletingAMissingCredentialIsNotAnError()
    {
        var store = new CredentialManagerApiKeyStore($"RealtimeTranslator.Tests:{Guid.NewGuid()}");

        store.Delete();

        Assert.Null(store.Load());
    }

    // Given: テスト専用のレジストリキー
    // When: install 識別子を 2 回取得する
    // Then: 同じ UUID が返り、送信値はその SHA-256 hex になる
    [Fact]
    public void InstallIdentifierIsStableAndOnlySentAsAHash()
    {
        var keyPath = $@"Software\RealtimeTranslator.Tests\{Guid.NewGuid()}";
        var store = new InstallIdentifierStore(keyPath);
        try
        {
            var first = store.LoadOrCreate();
            var second = store.LoadOrCreate();

            Assert.Equal(first, second);
            Assert.True(Guid.TryParse(first, out _));
            Assert.Equal(OpenAISafetyIdentifier.HashedValue(first), store.SafetyIdentifier());
            Assert.DoesNotContain(first, store.SafetyIdentifier(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
        }
    }

    // Given: 既に lease を保持しているプロセス
    // When: 同じ名前で再取得を試みる
    // Then: 2 つ目は拒否され、解放後は再取得できる
    [Fact]
    public void SingleInstanceLeaseAdmitsOnlyOneHolder()
    {
        var name = $@"Local\RealtimeTranslator.Tests.{Guid.NewGuid()}";
        var first = SingleInstanceLease.TryAcquire(name);

        Assert.NotNull(first);
        Assert.Null(SingleInstanceLease.TryAcquire(name));

        first.Dispose();

        using var reacquired = SingleInstanceLease.TryAcquire(name);
        Assert.NotNull(reacquired);
    }

    // Given: 登録に成功するホットキー登録器
    // When: WM_HOTKEY を受け取る
    // Then: トグルイベントを 1 回発火し、解除後は登録が外れる
    [Fact]
    public void HotkeyManagerRaisesPressedForItsOwnMessage()
    {
        var registrar = new FakeHotkeyRegistrar(succeed: true);
        using var manager = new GlobalHotkeyManager(registrar);
        var pressed = 0;
        manager.Pressed += (_, _) => pressed += 1;

        Assert.True(manager.Register(new IntPtr(1)));
        Assert.False(manager.HandleMessage(GlobalHotkeyManager.WmHotkey, new IntPtr(0x1234)));
        Assert.True(manager.HandleMessage(
            GlobalHotkeyManager.WmHotkey,
            new IntPtr(GlobalHotkeyManager.DefaultHotkeyId)));

        manager.Unregister();

        Assert.Equal(1, pressed);
        Assert.False(manager.IsRegistered);
        Assert.Equal(1, registrar.UnregisterCount);
    }

    // Given: 登録後に解除したホットキー
    // When: 同じ ID の WM_HOTKEY を受け取る
    // Then: 処理せず Pressed も増えない
    [Fact]
    public void HotkeyManagerIgnoresMessagesAfterUnregister()
    {
        using var manager = new GlobalHotkeyManager(new FakeHotkeyRegistrar(succeed: true));
        var pressed = 0;
        manager.Pressed += (_, _) => pressed += 1;

        Assert.True(manager.Register(new IntPtr(1)));
        manager.Unregister();

        Assert.False(manager.HandleMessage(
            GlobalHotkeyManager.WmHotkey,
            new IntPtr(GlobalHotkeyManager.DefaultHotkeyId)));
        Assert.Equal(0, pressed);
    }

    // Given: 他アプリが同じ組み合わせを握っている状況
    // When: 登録する
    // Then: 例外ではなく false を返す
    [Fact]
    public void HotkeyRegistrationFailureIsReportedWithoutThrowing()
    {
        using var manager = new GlobalHotkeyManager(new FakeHotkeyRegistrar(succeed: false));

        Assert.False(manager.Register(new IntPtr(1)));
        Assert.False(manager.IsRegistered);
    }

    // Given: 秘密を含むメッセージ
    // When: ログへ出す
    // Then: API キー・Bearer・認証ヘッダー・install UUID が伏字化される
    [Fact]
    public void LoggerRedactsSecretMaterial()
    {
        var previous = new TraceLogSink();
        var sink = new RecordingSink();
        AppLogger.UseSink(sink);
        try
        {
            var installId = Guid.NewGuid().ToString();

            AppLogger.Error(
                LogCategory.Realtime,
                $"connect failed key=sk-live-abcdef123456 Authorization: Bearer sk-live-abcdef123456 install={installId}");

            var line = Assert.Single(sink.Lines);
            Assert.DoesNotContain("sk-live-abcdef123456", line, StringComparison.Ordinal);
            Assert.DoesNotContain(installId, line, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(AppLogger.RedactedPlaceholder, line, StringComparison.Ordinal);
            Assert.Contains("connect failed", line, StringComparison.Ordinal);
        }
        finally
        {
            AppLogger.UseSink(previous);
        }
    }

    private sealed class FakeHotkeyRegistrar(bool succeed) : IGlobalHotkeyRegistrar
    {
        public int UnregisterCount { get; private set; }

        public bool Register(IntPtr windowHandle, int id, HotkeyModifiers modifiers, uint virtualKey) => succeed;

        public bool Unregister(IntPtr windowHandle, int id)
        {
            UnregisterCount += 1;
            return true;
        }
    }

    private sealed class RecordingSink : ILogSink
    {
        public List<string> Lines { get; } = [];

        public void Write(LogCategory category, EventLevel level, string message) => Lines.Add(message);
    }
}
