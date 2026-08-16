import XCTest
@testable import RealtimeTranslator

final class APIKeyStoreTests: XCTestCase {
    func testInMemorySaveLoadDeleteAndRejectEmpty() throws {
        // Given: 空のin-memory store
        let store = InMemoryAPIKeyStore()
        XCTAssertEqual(try store.storedKeyState(), .missing)
        XCTAssertFalse(store.hasStoredKey)

        // When/Then: 空文字は拒否
        XCTAssertThrowsError(try store.save("   ")) { error in
            XCTAssertEqual(error as? APIKeyStoreError, .emptyKey)
        }

        // When: 保存・上書き・削除
        try store.save("sk-one")
        XCTAssertEqual(try store.load(), "sk-one")
        XCTAssertEqual(try store.storedKeyState(), .valid)
        XCTAssertTrue(store.hasStoredKey)
        try store.save("sk-two")
        XCTAssertEqual(try store.load(), "sk-two")
        try store.delete()

        // Then: 削除後はnil
        XCTAssertNil(try store.load())
        XCTAssertEqual(try store.storedKeyState(), .missing)
        XCTAssertFalse(try store.storedKeyState().canDelete)
    }

    func testInMemorySaveStripsEmbeddedWhitespaceAndRejectsTimestamp() throws {
        // Given: 空の in-memory store
        let store = InMemoryAPIKeyStore()

        // When: 行折り返しキーを保存する
        try store.save("sk-proj-AAAA\nBBBB")

        // Then: 空白除去後のキーが残る
        XCTAssertEqual(try store.load(), "sk-proj-AAAABBBB")

        // When/Then: 貼り付けゴミ（時刻）は形式不正
        XCTAssertThrowsError(try store.save("sk-proj-abc\n3:26")) { error in
            XCTAssertEqual(error as? APIKeyStoreError, .malformedKey)
            XCTAssertEqual(
                APIKeyStoreError.malformedKey.errorDescription,
                "APIキーの形式が正しくありません。コピー時に改行や余分な文字が入っていないか確認してください"
            )
        }
        XCTAssertEqual(try store.load(), "sk-proj-AAAABBBB")
    }

    func testInMemoryReportsMalformedStoredValueAsDeletable() throws {
        // Given: 初期値が形式不正
        let store = InMemoryAPIKeyStore(initialKey: "sk-abc:def")

        // When: 保存状態と接続用キーを読み出す
        let state = try store.storedKeyState()

        // Then: 接続には渡さず、削除可能な形式不正項目として保持する
        XCTAssertNil(try store.load())
        XCTAssertEqual(state, .malformed)
        XCTAssertFalse(state.hasUsableKey)
        XCTAssertTrue(state.canDelete)
        XCTAssertFalse(store.hasStoredKey)
    }

    func testDeletingMalformedStoredValueMakesStateMissing() throws {
        // Given: 形式不正の保存項目
        let store = InMemoryAPIKeyStore(initialKey: "sk-abc:def")
        XCTAssertEqual(try store.storedKeyState(), .malformed)

        // When: 保存項目を削除する
        try store.delete()

        // Then: 項目なしになり、削除操作も不要になる
        XCTAssertEqual(try store.storedKeyState(), .missing)
        XCTAssertFalse(try store.storedKeyState().canDelete)
    }

    func testEmptyStoredValueIsMalformedAndDeletable() throws {
        // Given: 更新前から残った空の保存項目
        let store = InMemoryAPIKeyStore(initialKey: "")

        // When: 保存状態を確認する
        let state = try store.storedKeyState()

        // Then: 未保存と同一視せず、形式不正として削除できる
        XCTAssertEqual(state, .malformed)
        XCTAssertTrue(state.canDelete)
        XCTAssertNil(try store.load())
    }

    func testKeychainOrphanMalformedKeyCanBeDeleted() throws {
        // Given: 旧バージョンが残した形式不正キー
        let service = "com.realtimetranslator.tests.\(UUID().uuidString)"
        let store = KeychainAPIKeyStore(service: service, account: "unit-test-key")
        defer { try? store.delete() }
        try store.seedUnnormalized("sk-proj-abc\n3:26")

        // When: 接続用キーと保存状態を読み出す
        let state = try store.storedKeyState()

        // Then: 接続には渡さず、削除可能な形式不正項目として扱う
        XCTAssertNil(try store.load())
        XCTAssertEqual(state, .malformed)
        XCTAssertTrue(state.canDelete)
        XCTAssertFalse(store.hasStoredKey)
        try store.delete()
        XCTAssertNil(try store.load())
        XCTAssertEqual(try store.storedKeyState(), .missing)
    }

    func testKeychainStoreRoundTripWithNamespacedService() throws {
        // Given: テスト専用service名のKeychain store
        let service = "com.realtimetranslator.tests.\(UUID().uuidString)"
        let store = KeychainAPIKeyStore(service: service, account: "unit-test-key")
        defer { try? store.delete() }

        // When: 保存する
        try store.save("sk-keychain-test")

        // Then: 読み戻せ、削除できる
        XCTAssertEqual(try store.load(), "sk-keychain-test")
        XCTAssertEqual(try store.storedKeyState(), .valid)
        try store.delete()
        XCTAssertNil(try store.load())
        XCTAssertEqual(try store.storedKeyState(), .missing)
    }

    func testBootstrapImportsEnvironmentOnlyWhenKeychainEmpty() throws {
        // Given: 空storeと環境変数
        let store = InMemoryAPIKeyStore()

        // When: 取り込む
        let imported = try APIKeyBootstrap.importFromEnvironmentIfNeeded(
            store: store,
            environment: ["OPENAI_API_KEY": " sk-from-env "]
        )

        // Then: 保存され、既存キーがある場合は無視
        XCTAssertTrue(imported)
        XCTAssertEqual(try store.load(), "sk-from-env")

        let ignored = try APIKeyBootstrap.importFromEnvironmentIfNeeded(
            store: store,
            environment: ["OPENAI_API_KEY": "sk-other"]
        )
        XCTAssertFalse(ignored)
        XCTAssertEqual(try store.load(), "sk-from-env")
    }

    func testBootstrapIgnoresBlankEnvironmentValue() throws {
        // Given: 空白の環境変数
        let store = InMemoryAPIKeyStore()

        // When
        let imported = try APIKeyBootstrap.importFromEnvironmentIfNeeded(
            store: store,
            environment: ["OPENAI_API_KEY": "   "]
        )

        // Then
        XCTAssertFalse(imported)
        XCTAssertNil(try store.load())
        XCTAssertEqual(try store.storedKeyState(), .missing)
    }

    func testBootstrapRejectsMalformedEnvironmentValue() throws {
        // Given: 空 store と形式不正の環境変数
        let store = InMemoryAPIKeyStore()

        // When/Then: 取り込みは形式不正で失敗し、空のまま
        XCTAssertThrowsError(
            try APIKeyBootstrap.importFromEnvironmentIfNeeded(
                store: store,
                environment: ["OPENAI_API_KEY": "sk-proj-abc\n3:26"]
            )
        ) { error in
            XCTAssertEqual(error as? APIKeyStoreError, .malformedKey)
        }
        XCTAssertNil(try store.load())
        XCTAssertEqual(try store.storedKeyState(), .missing)
    }
}
