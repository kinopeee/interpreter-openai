import Foundation

/// リリースタグや Info.plist へ埋め込んだバージョン文字列を、設定画面向けの表示値へ正規化する。
enum AppReleaseVersion {
    /// 未リリース / 埋め込みなしのときの表示値。
    static let unpublished = "0.0.0"

    /// 実行中バンドルの `CFBundleShortVersionString`。
    static var current: String {
        displayValue(
            from: Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String
        )
    }

    /// `v0.1.0` や `0.1.0+commit` から、設定に出すバージョンを取り出す。
    static func displayValue(from raw: String?) -> String {
        guard var value = raw?.trimmingCharacters(in: .whitespacesAndNewlines), !value.isEmpty else {
            return unpublished
        }

        if let plus = value.firstIndex(of: "+") {
            value = String(value[..<plus])
        }

        if value.count >= 2, value.first == "v" || value.first == "V",
           let second = value.dropFirst().first, ("0"..."9").contains(second)
        {
            value.removeFirst()
        }

        value = value.trimmingCharacters(in: .whitespacesAndNewlines)
        return value.isEmpty ? unpublished : value
    }
}
