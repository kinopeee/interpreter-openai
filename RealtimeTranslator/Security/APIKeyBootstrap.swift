import Foundation

enum APIKeyBootstrap {
    static let environmentKeyName = "OPENAI_API_KEY"

    /// Keychainが空で環境変数がある場合だけKeychainへ取り込む。既存キーは上書きしない。
    @discardableResult
    static func importFromEnvironmentIfNeeded(
        store: any APIKeyStore,
        environment: [String: String] = ProcessInfo.processInfo.environment
    ) throws -> Bool {
        if let existing = try store.load(), !existing.isEmpty {
            return false
        }
        guard let raw = environment[environmentKeyName] else {
            return false
        }
        do {
            try store.save(raw)
            return true
        } catch APIKeyStoreError.emptyKey {
            return false
        }
    }
}
