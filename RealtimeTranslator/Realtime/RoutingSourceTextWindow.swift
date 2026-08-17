import Foundation

/// ルーティング判定用に保持する原文バッファの窓切り詰め。
/// `SpokenLanguageDetector.recentEvidence` と同じ判定窓を残す純粋ロジックで、
/// セッションのライフサイクル（接続・世代・状態遷移）から独立している。
enum RoutingSourceTextWindow {
    /// ルーティング判定用に保持する原文の上限 (UTF-16)。
    /// ja-* は末尾の非空白 scalar ウィンドウへ切り詰め、ウィンドウ内の空白が異常に長い場合の
    /// 安全弁として空白 run を圧縮してこの長さへ収める。en-es は語窓へ切り詰め、空白 run を圧縮する。
    static let maxLength = 16 * SpokenLanguageDetector.recentEvidenceWindow

    /// `en-es` は語窓へ切り詰めたあと空白 run を圧縮する。それ以外は末尾非空白 scalar 窓で、
    /// 空白 run が異常に長い場合だけ圧縮する。
    static func trim(_ text: String, pair: LanguagePair) -> String {
        guard !text.isEmpty else { return text }
        if pair == .enEs {
            let start = SpokenLanguageDetector.recentWordWindowStart(in: text)
            return collapseWhitespaceRuns(String(text.unicodeScalars[start...]))
        }
        let window = recentEvidenceWindowSubstring(
            text,
            window: SpokenLanguageDetector.recentEvidenceWindow
        )
        if window.utf16.count <= maxLength {
            return window
        }
        return collapseWhitespaceRuns(window)
    }

    /// 末尾から空白以外の Unicode scalar を `window` 個含む範囲の部分文字列。
    private static func recentEvidenceWindowSubstring(_ text: String, window: Int) -> String {
        guard window > 0, !text.isEmpty else { return text }
        let scalars = text.unicodeScalars
        var nonWhitespaceCount = 0
        var start = scalars.endIndex
        while start > scalars.startIndex, nonWhitespaceCount < window {
            start = scalars.index(before: start)
            if !CharacterSet.whitespacesAndNewlines.contains(scalars[start]) {
                nonWhitespaceCount += 1
            }
        }
        guard nonWhitespaceCount > 0 else { return "" }
        return String(scalars[start...])
    }

    /// 連続する空白 scalar を U+0020 1 個へ潰す。ラテン語境界を残しつつ保持長を抑える。
    private static func collapseWhitespaceRuns(_ text: String) -> String {
        var collapsed = ""
        collapsed.reserveCapacity(min(text.utf16.count, maxLength))
        var previousWasWhitespace = false
        for scalar in text.unicodeScalars {
            if CharacterSet.whitespacesAndNewlines.contains(scalar) {
                if previousWasWhitespace {
                    continue
                }
                previousWasWhitespace = true
                collapsed.unicodeScalars.append(" ")
                continue
            }
            previousWasWhitespace = false
            collapsed.unicodeScalars.append(scalar)
        }
        return collapsed
    }
}
