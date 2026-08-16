import Foundation

/// BYOK キーの正規化。埋め込み空白・制御文字を落とし、ヘッダ破壊と貼り付けゴミを防ぐ。
enum APIKeyNormalization: Equatable, Sendable {
    case empty
    case malformed
    case valid(String)

    static func normalize(_ raw: String) -> APIKeyNormalization {
        let stripped = String(raw.unicodeScalars.filter { !shouldStrip($0) })
        if stripped.isEmpty {
            return .empty
        }
        guard stripped.unicodeScalars.allSatisfy(isAllowed) else {
            return .malformed
        }
        return .valid(stripped)
    }

    private static func shouldStrip(_ scalar: Unicode.Scalar) -> Bool {
        if scalar.properties.isWhitespace {
            return true
        }
        switch scalar.properties.generalCategory {
        case .control, .format:
            return true
        default:
            return false
        }
    }

    private static func isAllowed(_ scalar: Unicode.Scalar) -> Bool {
        (scalar >= "0" && scalar <= "9")
            || (scalar >= "A" && scalar <= "Z")
            || (scalar >= "a" && scalar <= "z")
            || scalar == "."
            || scalar == "_"
            || scalar == "-"
    }
}

func normalizedAPIKey(from raw: String) throws -> String {
    switch APIKeyNormalization.normalize(raw) {
    case .empty:
        throw APIKeyStoreError.emptyKey
    case .malformed:
        throw APIKeyStoreError.malformedKey
    case .valid(let key):
        return key
    }
}

func storedAPIKey(from raw: String) -> String? {
    if case .valid(let key) = APIKeyNormalization.normalize(raw) {
        return key
    }
    return nil
}
