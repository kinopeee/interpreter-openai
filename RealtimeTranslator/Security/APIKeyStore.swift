import Foundation

enum APIKeyStoreError: Error, LocalizedError, Equatable, Sendable {
    case emptyKey
    case malformedKey
    case notFound
    case unexpectedStatus(OSStatus)
    case encodingFailed

    var errorDescription: String? {
        switch self {
        case .emptyKey:
            return UiCopy.text("error.apiKeyEmpty")
        case .malformedKey:
            return UiCopy.text("error.apiKeyMalformed")
        case .notFound:
            return UiCopy.text("error.apiKeyNotFound")
        case .unexpectedStatus:
            return UiCopy.text("error.apiKeyStoreUnavailable")
        case .encodingFailed:
            return UiCopy.text("error.apiKeyEncodingFailed")
        }
    }
}

protocol APIKeyStore: AnyObject, Sendable {
    func load() throws -> String?
    func save(_ key: String) throws
    func delete() throws
}

extension APIKeyStore {
    var hasStoredKey: Bool {
        (try? load()?.isEmpty == false) == true
    }
}
