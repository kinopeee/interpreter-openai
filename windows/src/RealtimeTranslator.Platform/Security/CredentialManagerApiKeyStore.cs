using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using RealtimeTranslator.Core.Realtime;

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
    private const int ErrorNotFound = 1168;

    private readonly string _targetName;

    public CredentialManagerApiKeyStore(string targetName = DefaultTargetName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetName);

        _targetName = targetName;
    }

    public bool HasStoredKey => !string.IsNullOrWhiteSpace(Load());

    public string? Load()
    {
        if (!NativeMethods.CredReadW(_targetName, CredTypeGeneric, 0, out var handle))
        {
            var error = Marshal.GetLastWin32Error();
            return error == ErrorNotFound ? null : throw new Win32Exception(error);
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeMethods.Credential>(handle);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
            {
                return null;
            }

            var blob = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, blob, 0, blob.Length);
            try
            {
                var value = Encoding.UTF8.GetString(blob).Trim();
                return value.Length == 0 ? null : value;
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
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        var blob = Encoding.UTF8.GetBytes(apiKey.Trim());
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
