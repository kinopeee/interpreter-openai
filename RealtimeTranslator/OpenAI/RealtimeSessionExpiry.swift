import CoreFoundation
import Foundation

/// `session.created` の `session.expires_at`（unix 秒）を読む共有ヘルパー。
/// 翻訳 codec と原文接続の raw-dict handshake の両方から使う。
enum RealtimeSessionExpiry {
    /// JSON 数値かつ整数値（小数部なし）で 0 以上のときだけ有効値として返す。
    /// session 欠落/非 object、expires_at 欠落、null、文字列、bool、負数、小数、非有限は
    /// すべて「不明」として nil を返し、codec エラーにしない。
    static func parseExpiresAt(fromSessionPayload session: [String: Any]?) -> Int? {
        guard let session,
            let raw = session["expires_at"],
            !(raw is NSNull),
            let number = raw as? NSNumber
        else {
            return nil
        }
        // JSONSerialization は JSON の true/false も NSNumber で返すため除外する。
        guard CFGetTypeID(number) != CFBooleanGetTypeID() else {
            return nil
        }
        // 整数表現は Int64 範囲をそのまま受け入れ（C# 側の long と同じ範囲）、
        // 浮動小数点表現は有限・整数・0 以上・2^53 以下に限る。
        switch String(cString: number.objCType) {
        case "c", "s", "i", "l", "q", "C", "S", "I", "L":
            let value = number.int64Value
            return value >= 0 ? Int(value) : nil
        case "Q":
            let value = number.uint64Value
            guard value <= UInt64(Int64.max) else { return nil }
            return Int(value)
        case "f", "d":
            let value = number.doubleValue
            guard value.isFinite,
                value >= 0,
                value <= 9_007_199_254_740_992,
                value.truncatingRemainder(dividingBy: 1) == 0
            else {
                return nil
            }
            return Int(value)
        default:
            return nil
        }
    }

    /// `expires_at − 壁時計` を残り時間へ変換する。
    /// 差が ±10 年（315,360,000 秒）を超える場合や減算がオーバーフローする場合は
    /// 「不明」として nil を返す。
    static func remaining(expiresAtUnixSeconds: Int, wallNowUnixSeconds: Int64) -> Duration? {
        let (diff, overflow) = Int64(expiresAtUnixSeconds)
            .subtractingReportingOverflow(wallNowUnixSeconds)
        guard !overflow, diff <= 315_360_000, diff >= -315_360_000 else {
            return nil
        }
        return .seconds(diff)
    }
}
