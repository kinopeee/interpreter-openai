using System;
using System.Text.RegularExpressions;
using RealtimeTranslator.Core.Localization;

namespace RealtimeTranslator.Core.OpenAI;

public enum RealtimeTranslationErrorKind
{
    MissingApiKey,
    NotConnected,
    InvalidMessage,
    AuthenticationFailed,
    FatalServerError,
    RecoverableTransportFailure,
    SessionUpdateTimeout,
    CloseTimeout,
    Cancelled,
}

/// <summary>Realtime セッションの失敗。表示文言は必ず正規化済みのものを使う。</summary>
public sealed partial class RealtimeTranslationException : Exception
{
    public static string GenericServerMessage => UserCopy.Current.Text("error.genericServer");

    public RealtimeTranslationException(RealtimeTranslationErrorKind kind, string? serverMessage = null)
        : this(kind, serverMessage, UserCopy.Current)
    {
    }

    /// <summary>表示文言の <see cref="UserCopy"/> を明示する。未指定時は <see cref="UserCopy.Current"/>。</summary>
    internal RealtimeTranslationException(
        RealtimeTranslationErrorKind kind,
        string? serverMessage,
        UserCopy copy)
        : base(DescribeFor(kind, serverMessage, copy))
    {
        Kind = kind;
        ServerMessage = serverMessage;
    }

    public RealtimeTranslationErrorKind Kind { get; }

    /// <summary>サーバー由来の生文言。UI/ログへは <see cref="Exception.Message"/> だけを出す。</summary>
    public string? ServerMessage { get; }

    public bool IsRecoverable => Kind is RealtimeTranslationErrorKind.RecoverableTransportFailure
        or RealtimeTranslationErrorKind.SessionUpdateTimeout;

    /// <summary>アラート・バナー・ログへ出してよいサーバー文言へ正規化する。</summary>
    public static string SanitizeServerMessage(string message)
    {
        var lowered = message.ToLowerInvariant();
        if (lowered.Contains("sk-", StringComparison.Ordinal)
            || lowered.Contains("api key", StringComparison.Ordinal)
            || lowered.Contains("authorization", StringComparison.Ordinal)
            || lowered.Contains("bearer ", StringComparison.Ordinal))
        {
            return GenericServerMessage;
        }

        return message.Length == 0 ? GenericServerMessage : message;
    }

    /// <summary>
    /// ランタイム / handshake の認証失敗判定。
    /// bare <c>auth</c> / <c>401</c> / <c>403</c> の部分一致は <c>authority</c> や <c>4010</c> に誤爆するため使わない。
    /// </summary>
    public static bool IsAuthenticationFailure(string? code, string message)
    {
        var codeLowered = (code ?? string.Empty).Trim().ToLowerInvariant();
        var messageLowered = message.ToLowerInvariant();

        if (Array.IndexOf(KnownAuthenticationFailureCodes, codeLowered) >= 0)
        {
            return true;
        }

        if (codeLowered.Contains("invalid_api_key", StringComparison.Ordinal)
            || codeLowered.Contains("authentication", StringComparison.Ordinal)
            || codeLowered.Contains("unauthorized", StringComparison.Ordinal)
            || codeLowered.Contains("authorization", StringComparison.Ordinal))
        {
            return true;
        }

        foreach (var phrase in AuthPhrases)
        {
            if (messageLowered.Contains(phrase, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return HttpAuthStatusPattern().IsMatch(messageLowered);
    }

    private static string DescribeFor(
        RealtimeTranslationErrorKind kind,
        string? serverMessage,
        UserCopy copy)
    {
        ArgumentNullException.ThrowIfNull(copy);
        return kind switch
        {
            RealtimeTranslationErrorKind.MissingApiKey => copy.Text("error.missingApiKey"),
            RealtimeTranslationErrorKind.NotConnected => copy.Text("error.notConnected"),
            RealtimeTranslationErrorKind.InvalidMessage => copy.Text("error.invalidMessage"),
            RealtimeTranslationErrorKind.AuthenticationFailed => copy.Text("error.authenticationFailed"),
            RealtimeTranslationErrorKind.FatalServerError => SanitizeServerMessage(serverMessage ?? string.Empty),
            RealtimeTranslationErrorKind.RecoverableTransportFailure => copy.Text("error.transportDisconnected"),
            RealtimeTranslationErrorKind.SessionUpdateTimeout => copy.Text("error.sessionUpdateTimeout"),
            RealtimeTranslationErrorKind.CloseTimeout => copy.Text("error.closeTimeout"),
            RealtimeTranslationErrorKind.Cancelled => copy.Text("error.cancelled"),
            _ => copy.Text("error.genericServer"),
        };
    }

    private static readonly string[] KnownAuthenticationFailureCodes =
    [
        "invalid_api_key",
        "invalid_auth",
        "authentication_error",
        "unauthorized",
        "unauthenticated",
        "401",
        "403",
    ];

    private static readonly string[] AuthPhrases =
    [
        "unauthorized",
        "unauthenticated",
        "authorization",
        "invalid_api_key",
        "incorrect api key",
        "invalid api key",
        "authentication",
        "authentication failed",
        "authentication error",
        "not authenticated",
        "api key is invalid",
    ];

    [GeneratedRegex(@"(?<![0-9])(401|403)(?![0-9])")]
    private static partial Regex HttpAuthStatusPattern();
}
