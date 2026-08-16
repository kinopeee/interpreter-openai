using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using RealtimeTranslator.Core.Localization;
using RealtimeTranslator.Core.Realtime;
using RealtimeTranslator.Core.Security;

namespace RealtimeTranslator.Platform.Security;

/// <summary>
/// BYOK の API キーを Windows 資格情報マネージャー (CRED_TYPE_GENERIC) に保管する。
/// 平文ファイル・設定ファイル・ログには一切書かない。
/// </summary>
public sealed class CredentialManagerApiKeyStore : IApiKeyStore
{
    public const string DefaultTargetName = "RealtimeTranslator:openai-api-key";

    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;

    /// <summary>ERROR_NOT_FOUND: 指定ターゲットの資格情報がない。</summary>
    internal const int ErrorNotFound = 1168;

    /// <summary>
    /// ERROR_NO_SUCH_LOGON_SESSION: ログオンセッションに資格情報セットがない
    /// （ネットワークログオン等）。CredRead の文書化済み失敗。
    /// </summary>
    internal const int ErrorNoSuchLogonSession = 1312;

    private readonly string _targetName;

    public CredentialManagerApiKeyStore(string targetName = DefaultTargetName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetName);

        _targetName = targetName;
    }

    /// <summary>
    /// 保存済みキーがあるか。CredRead の一時的/環境的失敗では例外を投げず false を返す
    /// （Settings 開閉・録音開始ゲートを落とさない）。
    /// </summary>
    public bool HasStoredKey
    {
        get
        {
            try
            {
                return StoredKeyState == StoredApiKeyState.Valid;
            }
            catch (Win32Exception)
            {
                return false;
            }
        }
    }

    /// <summary>保存項目の有無と、接続に利用できる形式かを秘密値なしで返す。</summary>
    public StoredApiKeyState StoredKeyState
    {
        get
        {
            return ApiKeyNormalizer.StoredState(ReadNormalizedCredential());
        }
    }

    public string? Load()
    {
        var result = ReadNormalizedCredential();
        return result is { Status: ApiKeyNormalizationStatus.Valid, Value: { } value }
            ? value
            : null;
    }

    private ApiKeyNormalizationResult? ReadNormalizedCredential()
    {
        if (!NativeMethods.CredReadW(_targetName, CredTypeGeneric, 0, out var handle))
        {
            // CredRead 失敗はすべて「キーなし」相当。以前は ErrorNotFound 以外で
            // Win32Exception を投げており、Settings 構築・録音開始ゲートが落ちていた。
            // 文書化済み失敗は ErrorNotFound / ErrorNoSuchLogonSession。それ以外も throw しない。
            return null;
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeMethods.Credential>(handle);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
            {
                return ApiKeyNormalizer.Normalize(string.Empty);
            }

            var blob = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, blob, 0, blob.Length);
            try
            {
                return ApiKeyNormalizer.Normalize(Encoding.UTF8.GetString(blob));
            }
            finally
            {
                Array.Clear(blob);
            }
        }
        finally
        {
            NativeMethods.CredFree(handle);
        }
    }

    public void Save(string apiKey)
    {
        var normalized = ApiKeyNormalizer.Normalize(apiKey);
        if (normalized.Status != ApiKeyNormalizationStatus.Valid || normalized.Value is not { } value)
        {
            throw new ApiKeyFormatException(
                UserCopy.Current.Text(
                    normalized.Status == ApiKeyNormalizationStatus.Malformed
                        ? "error.apiKeyMalformed"
                        : "error.apiKeyEmpty"));
        }

        var blob = Encoding.UTF8.GetBytes(value);
        var blobHandle = Marshal.AllocHGlobal(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, blobHandle, blob.Length);
            var credential = new NativeMethods.Credential
            {
                Type = CredTypeGeneric,
                TargetName = _targetName,
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = blobHandle,
                Persist = CredPersistLocalMachine,
                UserName = Environment.UserName,
            };

            if (!NativeMethods.CredWriteW(ref credential, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            // 解放前に平文をゼロ埋めしてプロセスメモリへ残さない。
            for (var index = 0; index < blob.Length; index++)
            {
                Marshal.WriteByte(blobHandle, index, 0);
            }

            Marshal.FreeHGlobal(blobHandle);
            Array.Clear(blob);
        }
    }

    public void Delete()
    {
        if (NativeMethods.CredDeleteW(_targetName, CredTypeGeneric, 0))
        {
            return;
        }

        var error = Marshal.GetLastWin32Error();
        if (error != ErrorNotFound)
        {
            throw new Win32Exception(error);
        }
    }

    private static class NativeMethods
    {
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "CredReadW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CredReadW(string target, uint type, uint reservedFlag, out IntPtr credentialPtr);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "CredWriteW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CredWriteW(ref Credential credential, uint flags);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "CredDeleteW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CredDeleteW(string target, uint type, uint flags);

        [DllImport("advapi32.dll", EntryPoint = "CredFree")]
        internal static extern void CredFree(IntPtr buffer);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct Credential
        {
            public uint Flags;
            public uint Type;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string TargetName;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string? Comment;
            public long LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string? TargetAlias;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string? UserName;
        }
    }
}
