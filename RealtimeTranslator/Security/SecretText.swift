import Foundation

/// 秘密検出の前処理。Format/Control の挿入や Unicode 空白でサニタイザを迂回させない。
enum SecretText {
    /// 部分一致判定用。不可視文字を落とし、空白類を ASCII 空白へ寄せてから小文字化する。
    static func normalizeForMatch(_ value: String) -> String {
        var scalars: [Unicode.Scalar] = []
        scalars.reserveCapacity(value.unicodeScalars.count)
        for scalar in value.unicodeScalars {
            if scalar.properties.isWhitespace {
                scalars.append(" ")
                continue
            }
            switch scalar.properties.generalCategory {
            case .control, .format:
                continue
            default:
                scalars.append(scalar)
            }
        }
        return String(String.UnicodeScalarView(scalars)).lowercased()
    }

    /// ログ伏字の前に Format/Control を落とし、ZWSP 挿入キーを正規表現へ載せる。
    static func stripFormatAndControl(_ value: String) -> String {
        var scalars: [Unicode.Scalar] = []
        scalars.reserveCapacity(value.unicodeScalars.count)
        for scalar in value.unicodeScalars {
            if scalar.properties.isWhitespace {
                scalars.append(scalar)
                continue
            }
            switch scalar.properties.generalCategory {
            case .control, .format:
                continue
            default:
                scalars.append(scalar)
            }
        }
        return String(String.UnicodeScalarView(scalars))
    }
}
