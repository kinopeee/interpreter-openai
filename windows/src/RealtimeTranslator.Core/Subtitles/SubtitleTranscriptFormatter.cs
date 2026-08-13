using System;
using System.Globalization;
using RealtimeTranslator.Core.Localization;

namespace RealtimeTranslator.Core.Subtitles;

/// <summary>字幕セッション記録の上限とユーザー向けバナー（本文を含めない）。</summary>
public static class SubtitleTranscriptLimits
{
    public const int MaxFileBytes = 10 * 1024 * 1024;

    public static string SizeLimitBanner => UserCopy.Current.Text("transcript.sizeLimitBanner");

    public static string WriteFailureBanner => UserCopy.Current.Text("transcript.writeFailureBanner");
}

/// <summary>字幕セッション記録のプレーンテキスト整形。時刻文字列は呼び出し側が渡す。</summary>
public static class SubtitleTranscriptFormatter
{
    public static string FormatEntry(string timestamp, string sourceText, string translatedText)
    {
        ArgumentNullException.ThrowIfNull(timestamp);
        ArgumentNullException.ThrowIfNull(sourceText);
        ArgumentNullException.ThrowIfNull(translatedText);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"--- {timestamp}\n原文: {sourceText}\n訳文: {translatedText}\n\n");
    }

    public static string FormatSessionStart(string timestamp)
    {
        ArgumentNullException.ThrowIfNull(timestamp);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"=== 録音開始 {timestamp}\n\n");
    }

    /// <summary>ローカルオフセット付き ISO8601。UTC は macOS の <c>XXXXX</c> と同様に <c>Z</c>。</summary>
    public static string FormatTimestamp(DateTimeOffset timestamp)
    {
        if (timestamp.Offset == TimeSpan.Zero)
        {
            return timestamp.UtcDateTime.ToString(
                "yyyy-MM-dd'T'HH:mm:ss'Z'",
                CultureInfo.InvariantCulture);
        }

        return timestamp.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
    }
}
