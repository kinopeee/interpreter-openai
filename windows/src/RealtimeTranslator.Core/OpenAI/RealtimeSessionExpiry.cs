using System;
using System.Text.Json.Nodes;

namespace RealtimeTranslator.Core.OpenAI;

/// <summary>
/// <c>session.created</c> の <c>session.expires_at</c>（unix 秒）を読む共有ヘルパー。
/// 翻訳 codec と原文 transcription codec の両方から使う。
/// </summary>
public static class RealtimeSessionExpiry
{
    /// <summary>
    /// JSON 数値かつ整数値（小数部なし）で 0 以上のときだけ有効値として返す。
    /// session 欠落/非 object、expires_at 欠落、null、文字列、bool、負数、小数、非有限は
    /// すべて「不明」として null を返し、codec エラーにしない。
    /// </summary>
    public static long? ParseExpiresAt(JsonNode? session)
    {
        if (session is not JsonObject sessionObject
            || sessionObject["expires_at"] is not JsonValue value)
        {
            return null;
        }

        // bool・文字列・小数は TryGetValue<long> が false を返す。
        return value.TryGetValue<long>(out var expiresAt) && expiresAt >= 0
            ? expiresAt
            : null;
    }

    /// <summary>
    /// <c>expires_at − 壁時計</c> を残り秒へ変換する。
    /// 差が ±10 年（315,360,000 秒）を超える場合や減算がオーバーフローする場合は
    /// 「不明」として null を返し、<see cref="TimeSpan.FromSeconds"/> で例外にしない。
    /// </summary>
    public static long? RemainingSeconds(long expiresAtUnixSeconds, long wallNowUnixSeconds)
    {
        try
        {
            var diff = checked(expiresAtUnixSeconds - wallNowUnixSeconds);
            return diff is > 315_360_000L or < -315_360_000L ? null : diff;
        }
        catch (OverflowException)
        {
            return null;
        }
    }
}
