using System;
using Microsoft.Win32;
using RealtimeTranslator.Core.Security;

namespace RealtimeTranslator.Platform.Security;

/// <summary>
/// 初回起動時に生成した UUID を HKCU に保存し、OpenAI へは SHA-256 hex だけを送る。
/// UUID 自体は送信もログ出力もしない。
/// </summary>
public sealed class InstallIdentifierStore
{
    public const string DefaultKeyPath = @"Software\RealtimeTranslator";
    public const string ValueName = "InstallIdentifier";

    private readonly string _keyPath;

    public InstallIdentifierStore(string keyPath = DefaultKeyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyPath);

        _keyPath = keyPath;
    }

    /// <summary>永続化済みの UUID を返す。無ければ生成して保存する。</summary>
    public string LoadOrCreate()
    {
        using var key = Registry.CurrentUser.CreateSubKey(_keyPath, writable: true)
            ?? throw new InvalidOperationException("レジストリキーを作成できませんでした");
        if (key.GetValue(ValueName) is string existing && Guid.TryParse(existing, out _))
        {
            return existing;
        }

        var generated = Guid.NewGuid().ToString();
        key.SetValue(ValueName, generated, RegistryValueKind.String);
        return generated;
    }

    /// <summary><c>OpenAI-Safety-Identifier</c> ヘッダーへ載せる値。</summary>
    public string SafetyIdentifier() => OpenAISafetyIdentifier.HashedValue(LoadOrCreate());
}
