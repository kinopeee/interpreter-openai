import Foundation

/// テストと一時的な注入用のメモリ実装。Keychainへは触れない。
final class InMemoryAPIKeyStore: APIKeyStore, @unchecked Sendable {
    private let lock = NSLock()
    private var stored: String?

    init(initialKey: String? = nil) {
        stored = initialKey
    }

    var hasStoredKey: Bool {
        lock.lock()
        defer { lock.unlock() }
        return stored != nil
    }

    func load() throws -> String? {
        lock.lock()
        defer { lock.unlock() }
        guard let stored else {
            return nil
        }
        return storedAPIKey(from: stored)
    }

    func save(_ key: String) throws {
        let normalized = try normalizedAPIKey(from: key)
        lock.lock()
        stored = normalized
        lock.unlock()
    }

    func delete() throws {
        lock.lock()
        stored = nil
        lock.unlock()
    }
}
