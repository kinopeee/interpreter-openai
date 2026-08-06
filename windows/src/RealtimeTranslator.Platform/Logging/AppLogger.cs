using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Text.RegularExpressions;

namespace RealtimeTranslator.Platform.Logging;

public enum LogCategory
{
    General,
    Audio,
    Realtime,
    Subtitle,
    Session,
}

/// <summary>
/// 出力先の差し替え口。テストと将来のファイル出力のために抽象化する。
/// </summary>
public interface ILogSink
{
    void Write(LogCategory category, EventLevel level, string message);
}

public sealed class TraceLogSink : ILogSink
{
    public void Write(LogCategory category, EventLevel level, string message) =>
        Trace.WriteLine($"[{level}][{category}] {message}");
}

/// <summary>
/// プライバシー契約を守るロガー。
/// API キー・認証ヘッダー・install UUID・音声バイト列・原文/訳文は決してログに出さない。
/// 呼び出し側が誤って渡した場合も <see cref="Redact"/> で伏字化する。
/// </summary>
public static partial class AppLogger
{
    public const string RedactedPlaceholder = "[redacted]";

    private static ILogSink _sink = new TraceLogSink();

    /// <summary>テスト・将来のファイル出力向けに出力先を差し替える。</summary>
    public static void UseSink(ILogSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);

        _sink = sink;
    }

    public static void Info(LogCategory category, string message) =>
        _sink.Write(category, EventLevel.Informational, Redact(message));

    public static void Warning(LogCategory category, string message) =>
        _sink.Write(category, EventLevel.Warning, Redact(message));

    public static void Error(LogCategory category, string message) =>
        _sink.Write(category, EventLevel.Error, Redact(message));

    /// <summary>秘密になりうる断片を伏字化する。ログ出力前に必ず通す。</summary>
    public static string Redact(string message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var redacted = message;
        foreach (var pattern in SecretPatterns)
        {
            redacted = pattern.Replace(redacted, RedactedPlaceholder);
        }

        return redacted;
    }

    private static IEnumerable<Regex> SecretPatterns
    {
        get
        {
            yield return ApiKeyPattern();
            yield return BearerPattern();
            yield return AuthorizationHeaderPattern();
            yield return SafetyIdentifierPattern();
            yield return UuidPattern();
        }
    }

    [GeneratedRegex(@"sk-[A-Za-z0-9_\-]{4,}", RegexOptions.None, matchTimeoutMilliseconds: 200)]
    private static partial Regex ApiKeyPattern();

    [GeneratedRegex(@"(?i)bearer\s+[A-Za-z0-9_\-\.]+", RegexOptions.None, matchTimeoutMilliseconds: 200)]
    private static partial Regex BearerPattern();

    [GeneratedRegex(@"(?i)authorization:\s*\S+", RegexOptions.None, matchTimeoutMilliseconds: 200)]
    private static partial Regex AuthorizationHeaderPattern();

    [GeneratedRegex(@"(?i)openai-safety-identifier:\s*\S+", RegexOptions.None, matchTimeoutMilliseconds: 200)]
    private static partial Regex SafetyIdentifierPattern();

    [GeneratedRegex(
        @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
        RegexOptions.None,
        matchTimeoutMilliseconds: 200)]
    private static partial Regex UuidPattern();
}
