using System;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using RealtimeTranslator.Core.Security;

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
public static class AppLogger
{
    public const string RedactedPlaceholder = LogSecretRedactor.Placeholder;

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
    public static string Redact(string message) => LogSecretRedactor.Redact(message);
}
