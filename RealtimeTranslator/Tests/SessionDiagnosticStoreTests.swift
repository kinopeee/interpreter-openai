import Foundation
import XCTest
@testable import RealtimeTranslator

final class SessionDiagnosticStoreTests: XCTestCase {
    private var directory: URL!

    override func setUpWithError() throws {
        directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("diagnostic-tests-\(UUID().uuidString)", isDirectory: true)
    }

    override func tearDownWithError() throws {
        if FileManager.default.fileExists(atPath: directory.path) {
            try FileManager.default.removeItem(at: directory)
        }
    }

    // Given: 秘密情報に見える不正なバージョンと型付き診断イベント
    // When: 診断をファイルへ保存する
    // Then: 許可された数値・列挙値だけを保存し、所有者以外の読み取りを許可しない
    func testTypedEventsAndPermissions() throws {
        let store = SessionDiagnosticStore(
            directory: directory, version: "sk-secret\n原文: private",
            now: { Date(timeIntervalSince1970: 0) }
        )
        store.record(.attemptStarted(generation: 1, attempt: 1))
        store.record(.attemptEpoch(generation: 1, attempt: 1, epoch: 2))
        store.record(.snapshot(SessionHealthSnapshot(), SessionDiagnosticMetrics(capturedFrames: 12, sentFrames: 11)))
        store.record(
            .detection(
                SessionHealthDetection(
                    kind: .receiveStalled, generation: 1, epoch: 2, lane: .source,
                    elapsed: .seconds(30), remaining: nil
                )))
        store.record(
            .termination(
                SessionTerminationDiagnostic(
                    kind: .userStopped, connectionDuration: .seconds(40), generation: 1, epoch: 2
                )))
        store.waitForPendingWrites()
        let url = directory.appendingPathComponent("diagnostics.log")
        let text = try String(contentsOf: url, encoding: .utf8)
        XCTAssertEqual(text.split(separator: "\n").count, 5)
        XCTAssertTrue(text.contains("attemptStarted generation=1 attempt=1"))
        XCTAssertTrue(text.contains("attemptEpoch generation=1 attempt=1 epoch=2"))
        XCTAssertTrue(text.contains("1970-01-01T00:00:00Z schema=1 appVersion=unknown"))
        XCTAssertTrue(text.contains("capturedFrames=12 sentFrames=11"))
        XCTAssertTrue(text.contains("kind=receiveStalled"))
        XCTAssertTrue(text.contains("kind=userStopped"))
        XCTAssertFalse(text.contains("sk-secret"))
        XCTAssertFalse(text.contains("原文"))
        let permissions = try FileManager.default.attributesOfItem(atPath: url.path)[.posixPermissions] as? NSNumber
        XCTAssertEqual(permissions?.intValue, 0o600)
    }

    // Given: 小さいファイル上限
    // When: 複数回ローテーションする量のイベントを保存する
    // Then: 直近のイベントを残し、各ファイルの上限とファイル数を守る
    func testRotationKeepsOnlyTwoBoundedFiles() throws {
        let store = SessionDiagnosticStore(directory: directory, byteLimit: 1_024, version: "0.4.5")
        for epoch in 0..<100 {
            store.record(.attemptEpoch(generation: 1, attempt: epoch, epoch: epoch))
            store.waitForPendingWrites()
        }
        let urls = try FileManager.default.contentsOfDirectory(at: directory, includingPropertiesForKeys: nil)
        XCTAssertEqual(Set(urls.map(\.lastPathComponent)), ["diagnostics.log", "diagnostics.previous.log"])
        for url in urls {
            XCTAssertLessThanOrEqual(try Data(contentsOf: url).count, 1_024)
        }
        let current = try String(contentsOf: directory.appendingPathComponent("diagnostics.log"), encoding: .utf8)
        XCTAssertTrue(current.contains("epoch=99\n"))
    }

    // Given: ディスク処理が進まないキュー
    // When: 上限を超えるイベントを渡す
    // Then: producer を待たせず、保持件数を制限し欠落数を記録する
    func testPendingQueueIsBounded() throws {
        let queue = DispatchQueue(label: "diagnostics-test.suspended")
        let store = SessionDiagnosticStore(directory: directory, queue: queue)
        queue.suspend()
        for epoch in 0..<100 {
            store.record(.attemptStarted(generation: 1, attempt: epoch))
        }
        queue.resume()
        store.waitForPendingWrites()
        let text = try String(contentsOf: directory.appendingPathComponent("diagnostics.log"), encoding: .utf8)
        XCTAssertEqual(text.split(separator: "\n").count, SessionDiagnosticStore.pendingLimit)
        XCTAssertTrue(text.contains("droppedRecords=36"))
        XCTAssertTrue(text.contains("attempt=99\n"))
    }

    // Given: 保存先がディレクトリではなく通常ファイル
    // When: 書き込みが失敗した後に保存先を直して次のイベントを渡す
    // Then: 失敗を呼び出し元へ伝播せず、次回成功時に欠落数を残す
    func testWriteFailureIsNonFatalAndReportedOnRecovery() throws {
        try Data("blocked".utf8).write(to: directory)
        let store = SessionDiagnosticStore(directory: directory)
        store.record(.attemptStarted(generation: 1, attempt: 1))
        store.waitForPendingWrites()
        try FileManager.default.removeItem(at: directory)
        store.record(.attemptStarted(generation: 1, attempt: 2))
        store.waitForPendingWrites()
        let text = try String(contentsOf: directory.appendingPathComponent("diagnostics.log"), encoding: .utf8)
        XCTAssertTrue(text.contains("droppedRecords=1"))
        XCTAssertTrue(text.contains("attempt=2"))
    }

    // Given: 診断ファイル名が別ファイルへのシンボリックリンク
    // When: 診断を保存する
    // Then: リンク先を書き換えない
    func testDoesNotFollowSymbolicLink() throws {
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let target = directory.appendingPathComponent("unrelated.txt")
        try Data("untouched".utf8).write(to: target)
        try FileManager.default.createSymbolicLink(
            at: directory.appendingPathComponent("diagnostics.log"), withDestinationURL: target
        )
        let store = SessionDiagnosticStore(directory: directory)
        store.record(.attemptStarted(generation: 1, attempt: 1))
        store.waitForPendingWrites()
        XCTAssertEqual(try String(contentsOf: target, encoding: .utf8), "untouched")
    }
}
