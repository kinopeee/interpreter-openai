using System;
using RealtimeTranslator.Core.Realtime;
using RealtimeTranslator.Core.Security;

namespace RealtimeTranslator.Core.OpenAI;

/// <summary>サーバー error イベントの扱い。<c>shared/fixtures/v1/server-error.json</c> が正本。</summary>
public enum RealtimeServerErrorDisposition
{
    /// <summary>接続を維持し、termination も下流イベントも出さない。</summary>
    KeepAlive,

    /// <summary>既存の再接続へ倒す。</summary>
    Recover,

    /// <summary>録音を止めて Error にする。</summary>
    Halt,
}

/// <summary>
/// <c>error.type</c> / <c>error.code</c> を別々に受け取り、許可リストだけで分類する。
/// 判定に使わなかった文字列や生のサーバー文言は保持しない。
/// </summary>
public readonly record struct RealtimeServerErrorClassification(
    RealtimeServerErrorDisposition Disposition,
    EventDeliveryTermination Termination,
    string? SanitizedMessage)
{
    public const string TransportCode = "transport";

    private static readonly string[] KeepAliveCodes =
    [
        "input_audio_buffer_commit_empty",
    ];

    private static readonly string[] HaltCodes =
    [
        "insufficient_quota",
        "billing_hard_limit_reached",
    ];

    private static readonly string[] RecoverCodes =
    [
        "server_error",
        "rate_limit_exceeded",
        "session_expired",
    ];

    private static readonly string[] RecoverTypes =
    [
        "server_error",
        "rate_limit_error",
    ];

    public static RealtimeServerErrorClassification Classify(string? errorType, string? code, string message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var normalizedCode = Normalize(code);
        var normalizedType = Normalize(errorType);

        // 認証失敗は code に関わらず最優先（transport 扱いで再接続に回さない）。
        // code が無い error は type を認証判定へ回す（既存の fallback と同じ範囲を守る）。
        if (RealtimeTranslationException.IsAuthenticationFailure(code ?? errorType, message))
        {
            return new(
                RealtimeServerErrorDisposition.Halt,
                EventDeliveryTermination.AuthenticationFailed,
                null);
        }

        if (normalizedCode == TransportCode)
        {
            return new(
                RealtimeServerErrorDisposition.Recover,
                EventDeliveryTermination.TransportFailure,
                null);
        }

        if (Matches(HaltCodes, normalizedCode) || Matches(HaltCodes, normalizedType))
        {
            return Fatal(message);
        }

        if (Matches(KeepAliveCodes, normalizedCode))
        {
            return new(
                RealtimeServerErrorDisposition.KeepAlive,
                EventDeliveryTermination.None,
                null);
        }

        if (Matches(RecoverCodes, normalizedCode) || Matches(RecoverTypes, normalizedType))
        {
            return new(
                RealtimeServerErrorDisposition.Recover,
                EventDeliveryTermination.RecoverableServerError,
                null);
        }

        return Fatal(message);
    }

    public RealtimeTranslationException ToException() => Termination switch
    {
        EventDeliveryTermination.AuthenticationFailed =>
            new RealtimeTranslationException(RealtimeTranslationErrorKind.AuthenticationFailed),
        EventDeliveryTermination.FatalServerError =>
            new RealtimeTranslationException(RealtimeTranslationErrorKind.FatalServerError, SanitizedMessage),
        EventDeliveryTermination.RecoverableServerError =>
            new RealtimeTranslationException(RealtimeTranslationErrorKind.RecoverableServerError),
        EventDeliveryTermination.TransportFailure =>
            new RealtimeTranslationException(RealtimeTranslationErrorKind.RecoverableTransportFailure),
        _ => throw new InvalidOperationException("keep-alive errors do not produce an exception."),
    };

    private static RealtimeServerErrorClassification Fatal(string message) => new(
        RealtimeServerErrorDisposition.Halt,
        EventDeliveryTermination.FatalServerError,
        RealtimeTranslationException.SanitizeServerMessage(message));

    private static string Normalize(string? value) =>
        SecretText.NormalizeForMatch(value ?? string.Empty).Replace(" ", string.Empty, StringComparison.Ordinal);

    private static bool Matches(string[] allowlist, string normalized) =>
        normalized.Length > 0 && Array.IndexOf(allowlist, normalized) >= 0;
}
