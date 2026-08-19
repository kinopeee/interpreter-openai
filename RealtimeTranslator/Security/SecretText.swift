import Foundation

/// 秘密検出の前処理。Format/Control の挿入や Unicode 空白でサニタイザを迂回させない。
enum SecretText {
    /// 部分一致判定用。不可視文字を落とし、空白類を ASCII 空白へ寄せてから小文字化する。
    static func normalizeForMatch(_ value: String) -> String {
        mappedScalars(value).lowercased()
    }

    /// ログ伏字の前に Format を落とし、制御空白は ASCII 空白へ寄せる。
    /// TAB を消して `api key` / `bearer ` を連結しない。ZWSP は除去して `sk-` を復元する。
    static func stripFormatAndControl(_ value: String) -> String {
        mappedScalars(value)
    }

    private static func mappedScalars(_ value: String) -> String {
        var scalars: [Unicode.Scalar] = []
        scalars.reserveCapacity(value.unicodeScalars.count)
        for scalar in value.unicodeScalars {
            switch scalar.properties.generalCategory {
            case .format:
                // ZWSP 等。キー断片の間に入れても照合できるよう落とす。
                continue
            case .control:
                // TAB/CR/LF は語区切りとして残す。消すと api key や 401 判定が壊れる。
                if scalar.properties.isWhitespace {
                    scalars.append(" ")
                }
            default:
                if scalar.properties.isWhitespace {
                    scalars.append(" ")
                } else {
                    scalars.append(scalar)
                }
            }
        }
        return String(String.UnicodeScalarView(scalars))
    }
}
