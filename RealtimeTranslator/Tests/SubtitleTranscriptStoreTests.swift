import XCTest
@testable import RealtimeTranslator

final class SubtitleTranscriptStoreTests: XCTestCase {
    private var directory: URL!
    private var fileURL: URL!
    private var fixedNow: Date!

    override func setUpWithError() throws {
        directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("transcript-tests-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        fileURL = directory.appendingPathComponent("session.txt")
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(secondsFromGMT: 9 * 3600)!
        fixedNow = calendar.date(
            from: DateComponents(
                calendar: calendar,
                timeZone: calendar.timeZone,
                year: 2026,
                month: 8,
                day: 7,
                hour: 15,
                minute: 40,
                second: 12
            )
        )
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: directory)
    }

    // Given: 空のストア
    // When: セッション開始マーカーと確定ペアを追記する
    // Then: ファイルにマーカーとペアが書かれ hasEntries が true になる
    func testAppendSessionStartAndEntry() throws {
        let store = makeStore()

        XCTAssertFalse(store.hasEntries)
        XCTAssertEqual(store.markSessionStart(), .appended)
        XCTAssertEqual(
            store.appendEntry(sourceText: "こんにちは", translatedText: "Hello"),
            .appended
        )
        XCTAssertTrue(store.hasEntries)

        let text = try String(contentsOf: fileURL, encoding: .utf8)
        XCTAssertTrue(text.contains("=== 録音開始 2026-08-07T15:40:12+09:00"))
        XCTAssertTrue(text.contains("--- 2026-08-07T15:40:12+09:00"))
        XCTAssertTrue(text.contains("原文: こんにちは"))
        XCTAssertTrue(text.contains("訳文: Hello"))
    }

    // Given: 同じ確定ペアを連続で受け取る
    // When: 2回 append する
    // Then: 2回目は skip されファイルは1エントリのまま
    func testDeduplicatesIdenticalConsecutiveEntries() throws {
        let store = makeStore()
        XCTAssertEqual(
            store.appendEntry(sourceText: "こんにちは", translatedText: "Hello"),
            .appended
        )
        XCTAssertEqual(
            store.appendEntry(sourceText: "こんにちは", translatedText: "Hello"),
            .skippedDuplicate
        )

        let text = try String(contentsOf: fileURL, encoding: .utf8)
        XCTAssertEqual(text.components(separatedBy: "--- ").count - 1, 1)
    }

    // Given: 直前セッション末尾と同じペア
    // When: markSessionStart のあと再度 append する
    // Then: 新セッションでは重複スキップせず追記される
    func testMarkSessionStartClearsConsecutiveDedup() throws {
        let store = makeStore()
        XCTAssertEqual(
            store.appendEntry(sourceText: "こんにちは", translatedText: "Hello"),
            .appended
        )
        XCTAssertEqual(store.markSessionStart(), .appended)
        XCTAssertEqual(
            store.appendEntry(sourceText: "こんにちは", translatedText: "Hello"),
            .appended
        )

        let text = try String(contentsOf: fileURL, encoding: .utf8)
        XCTAssertEqual(text.components(separatedBy: "--- ").count - 1, 2)
        XCTAssertEqual(text.components(separatedBy: "=== 録音開始 ").count - 1, 1)
    }

    // Given: 書き込み不能なパス
    // When: 同じペアを連続で append する
    // Then: 初回は failed、2回目は skippedDuplicate になり再試行しない
    func testFailedWriteRemembersPairToAvoidRetrySpam() {
        let blockedPath = directory.appendingPathComponent("blocked-dir", isDirectory: true)
        try? FileManager.default.createDirectory(at: blockedPath, withIntermediateDirectories: true)
        let store = SubtitleTranscriptStore(
            fileURL: blockedPath,
            now: { self.fixedNow },
            timeZone: TimeZone(secondsFromGMT: 9 * 3600)!
        )

        XCTAssertEqual(
            store.appendEntry(sourceText: "こんにちは", translatedText: "Hello"),
            .failed
        )
        XCTAssertEqual(
            store.appendEntry(sourceText: "こんにちは", translatedText: "Hello"),
            .skippedDuplicate
        )
    }

    // Given: 空白のみの原文または訳文
    // When: append する
    // Then: skippedEmpty になりファイルは作られないか空のまま
    func testSkipsEmptyPairs() {
        let store = makeStore()
        XCTAssertEqual(store.appendEntry(sourceText: "  ", translatedText: "Hello"), .skippedEmpty)
        XCTAssertEqual(store.appendEntry(sourceText: "こんにちは", translatedText: "\n"), .skippedEmpty)
        XCTAssertFalse(store.hasEntries)
    }

    // Given: 上限直前まで埋まったファイル
    // When: 追記しようとする
    // Then: capped を返し以後も no-op、クリア後は再開できる
    func testStopsAtSizeCapAndResumesAfterClear() throws {
        let store = makeStore(maxFileBytes: 64)
        XCTAssertEqual(
            store.appendEntry(sourceText: "短い", translatedText: "short"),
            .appended
        )
        let first = try String(contentsOf: fileURL, encoding: .utf8)

        XCTAssertEqual(
            store.appendEntry(sourceText: "とても長い原文を追加して上限を超える", translatedText: "overflow"),
            .capped
        )
        XCTAssertEqual(
            store.appendEntry(sourceText: "別の文", translatedText: "another"),
            .capped
        )
        XCTAssertEqual(try String(contentsOf: fileURL, encoding: .utf8), first)

        try store.clear()
        XCTAssertFalse(store.hasEntries)
        XCTAssertEqual(
            store.appendEntry(sourceText: "再開", translatedText: "resume"),
            .appended
        )
        XCTAssertTrue(store.hasEntries)
    }

    // Given: 追記済みのセッションファイル
    // When: 別パスへ exportCopy する
    // Then: 内容がコピーされる
    func testExportCopy() throws {
        let store = makeStore()
        XCTAssertEqual(
            store.appendEntry(sourceText: "こんにちは", translatedText: "Hello"),
            .appended
        )
        let destination = directory.appendingPathComponent("export.txt")
        try store.exportCopy(to: destination)
        XCTAssertEqual(
            try String(contentsOf: destination, encoding: .utf8),
            try String(contentsOf: fileURL, encoding: .utf8)
        )
    }

    // Given: 追記済みのセッションファイル
    // When: 同一パスへ exportCopy する
    // Then: 内容を消さず no-op になる
    func testExportCopyToSamePathIsNoOp() throws {
        let store = makeStore()
        XCTAssertEqual(
            store.appendEntry(sourceText: "こんにちは", translatedText: "Hello"),
            .appended
        )
        let before = try String(contentsOf: fileURL, encoding: .utf8)
        try store.exportCopy(to: fileURL)
        XCTAssertEqual(try String(contentsOf: fileURL, encoding: .utf8), before)
    }

    // Given: 固定時刻
    // When: 既定の書き出しファイル名を生成する
    // Then: subtitles-YYYYMMDD-HHmmss.txt になる
    func testDefaultExportFileName() {
        XCTAssertEqual(
            SubtitleTranscriptStore.defaultExportFileName(
                now: fixedNow,
                timeZone: TimeZone(secondsFromGMT: 9 * 3600)!
            ),
            "subtitles-20260807-154012.txt"
        )
    }

    // Given: 上限・失敗バナー定数
    // When: 文言を確認する
    // Then: 原文・訳文を含まない
    func testBannerMessagesDoNotContainSubtitleBody() {
        XCTAssertFalse(SubtitleTranscriptStore.sizeLimitBanner.contains("原文"))
        XCTAssertFalse(SubtitleTranscriptStore.writeFailureBanner.contains("訳文"))
    }

    private func makeStore(maxFileBytes: Int = SubtitleTranscriptLimits.maxFileBytes)
        -> SubtitleTranscriptStore
    {
        SubtitleTranscriptStore(
            fileURL: fileURL,
            now: { self.fixedNow },
            timeZone: TimeZone(secondsFromGMT: 9 * 3600)!,
            maxFileBytes: maxFileBytes
        )
    }
}
