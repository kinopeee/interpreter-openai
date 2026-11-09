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

    // Given: XCTestが注入する実行環境
    // When: アプリケーション用診断ストアを要求する
    // Then: ホームディレクトリのストアを初期化せず無効化する
    func testApplicationStoreIsDisabledInXCTest() {
        XCTAssertTrue(AppRuntimeEnvironment.isRunningXCTest())
        XCTAssertNil(SessionDiagnosticStore.applicationStore)
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

    // Given: 書き込み直前に通常のログがシンボリックリンクへ差し替わる
    // When: 追記を試み、リンクを除去してから再試行する
    // Then: リンク先の内容・権限は不変で、次の成功時に欠落を報告する
    func testFileReplacementDoesNotModifyLinkTarget() throws {
        let root = try XCTUnwrap(directory)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let current = root.appendingPathComponent("diagnostics.log")
        let backup = root.appendingPathComponent("original.log")
        let target = root.appendingPathComponent("unrelated.txt")
        try Data("original".utf8).write(to: current)
        try Data("untouched".utf8).write(to: target)
        try FileManager.default.setAttributes([.posixPermissions: 0o644], ofItemAtPath: target.path)
        let store = SessionDiagnosticStore(
            directory: root,
            beforeOpeningLog: {
                guard !FileManager.default.fileExists(atPath: backup.path) else { return }
                try FileManager.default.moveItem(at: current, to: backup)
                try FileManager.default.createSymbolicLink(at: current, withDestinationURL: target)
            })
        store.record(.attemptStarted(generation: 1, attempt: 1))
        store.waitForPendingWrites()

        XCTAssertEqual(try String(contentsOf: target, encoding: .utf8), "untouched")
        let attributes = try FileManager.default.attributesOfItem(atPath: target.path)
        XCTAssertEqual((attributes[.posixPermissions] as? NSNumber)?.intValue, 0o644)
        XCTAssertEqual(try String(contentsOf: backup, encoding: .utf8), "original")

        try FileManager.default.removeItem(at: current)
        store.record(.attemptStarted(generation: 1, attempt: 2))
        store.waitForPendingWrites()
        let text = try String(contentsOf: current, encoding: .utf8)
        XCTAssertTrue(text.contains("droppedRecords=1"))
        XCTAssertTrue(text.contains("attempt=2"))
    }

    // Given: 開いた保存ディレクトリのパスが別ディレクトリへのリンクへ差し替わる
    // When: 診断を追記する
    // Then: 開いた元ディレクトリだけに書き込み、リンク先の内容・権限を変えない
    func testDirectoryReplacementKeepsWritesInOpenedDirectory() throws {
        let root = try XCTUnwrap(directory)
        let logs = root.appendingPathComponent("logs", isDirectory: true)
        let original = root.appendingPathComponent("original", isDirectory: true)
        let target = root.appendingPathComponent("unrelated", isDirectory: true)
        try FileManager.default.createDirectory(at: logs, withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: target, withIntermediateDirectories: true)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: target.path)
        let targetLog = target.appendingPathComponent("diagnostics.log")
        try Data("untouched".utf8).write(to: targetLog)
        try FileManager.default.setAttributes([.posixPermissions: 0o644], ofItemAtPath: targetLog.path)
        let store = SessionDiagnosticStore(
            directory: logs,
            beforeOpeningLog: {
                try FileManager.default.moveItem(at: logs, to: original)
                try FileManager.default.createSymbolicLink(at: logs, withDestinationURL: target)
            })
        store.record(.attemptStarted(generation: 1, attempt: 1))
        store.waitForPendingWrites()

        XCTAssertEqual(try String(contentsOf: targetLog, encoding: .utf8), "untouched")
        for (url, permissions) in [(target, 0o755), (targetLog, 0o644)] {
            let attributes = try FileManager.default.attributesOfItem(atPath: url.path)
            XCTAssertEqual((attributes[.posixPermissions] as? NSNumber)?.intValue, permissions)
        }
        let text = try String(contentsOf: original.appendingPathComponent("diagnostics.log"), encoding: .utf8)
        XCTAssertTrue(text.contains("attempt=1"))
    }

    // Given: 別ファイルのハードリンクがログ名に置かれる
    // When: 診断を追記する
    // Then: 同じinodeを共有する別ファイルの内容・権限を変えない
    func testDoesNotModifyHardLinkedFile() throws {
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let target = directory.appendingPathComponent("unrelated.txt")
        try Data("untouched".utf8).write(to: target)
        try FileManager.default.setAttributes([.posixPermissions: 0o644], ofItemAtPath: target.path)
        try FileManager.default.linkItem(at: target, to: directory.appendingPathComponent("diagnostics.log"))
        let store = SessionDiagnosticStore(directory: directory)
        store.record(.attemptStarted(generation: 1, attempt: 1))
        store.waitForPendingWrites()

        XCTAssertEqual(try String(contentsOf: target, encoding: .utf8), "untouched")
        let attributes = try FileManager.default.attributesOfItem(atPath: target.path)
        XCTAssertEqual((attributes[.posixPermissions] as? NSNumber)?.intValue, 0o644)
    }

    // Given: currentまたはpreviousの既存ログが所有者読み取り専用または書き込み専用
    // When: 診断イベントを書き込む
    // Then: 内容を保持して権限を修復し、新しいcurrent行を欠落なく追記する
    func testAccessibleLogsAreRepairedBeforeAppending() throws {
        for (name, mode) in [
            ("diagnostics.log", 0o400), ("diagnostics.previous.log", 0o400),
            ("diagnostics.log", 0o200), ("diagnostics.previous.log", 0o200),
        ] {
            let logDirectory = directory.appendingPathComponent("\(name)-\(mode)", isDirectory: true)
            try FileManager.default.createDirectory(at: logDirectory, withIntermediateDirectories: true)
            let current = logDirectory.appendingPathComponent("diagnostics.log")
            let previous = logDirectory.appendingPathComponent("diagnostics.previous.log")
            try Data("existing current\n".utf8).write(to: current)
            try Data("existing previous\n".utf8).write(to: previous)
            try FileManager.default.setAttributes(
                [.posixPermissions: mode],
                ofItemAtPath: logDirectory.appendingPathComponent(name).path
            )

            let store = SessionDiagnosticStore(directory: logDirectory)
            store.record(.attemptStarted(generation: 1, attempt: 1))
            store.waitForPendingWrites()

            let currentText = try String(contentsOf: current, encoding: .utf8)
            let previousText = try String(contentsOf: previous, encoding: .utf8)
            XCTAssertTrue(currentText.hasPrefix("existing current\n"))
            XCTAssertTrue(currentText.contains("droppedRecords=0"))
            XCTAssertTrue(currentText.contains("attempt=1"))
            XCTAssertEqual(previousText, "existing previous\n")
            for url in [current, previous] {
                let attributes = try FileManager.default.attributesOfItem(atPath: url.path)
                XCTAssertEqual((attributes[.posixPermissions] as? NSNumber)?.intValue, 0o600)
            }
        }
    }

    // Given: 読み書き両方の権限が取り除かれた既存ログ
    // When: 記録が失敗した後、所有者が権限を戻して再試行する
    // Then: 安全に開けないファイルは変更せず、復旧時に欠落数を報告する
    func testInaccessibleLogFailsClosedAndRecovers() throws {
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let current = directory.appendingPathComponent("diagnostics.log")
        try Data("existing\n".utf8).write(to: current)
        try FileManager.default.setAttributes([.posixPermissions: 0], ofItemAtPath: current.path)
        defer { try? FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: current.path) }
        let store = SessionDiagnosticStore(directory: directory)
        store.record(.attemptStarted(generation: 1, attempt: 1))
        store.waitForPendingWrites()
        let attributes = try FileManager.default.attributesOfItem(atPath: current.path)
        XCTAssertEqual((attributes[.posixPermissions] as? NSNumber)?.intValue, 0)

        try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: current.path)
        store.record(.attemptStarted(generation: 1, attempt: 2))
        store.waitForPendingWrites()
        let text = try String(contentsOf: current, encoding: .utf8)
        XCTAssertTrue(text.hasPrefix("existing\n"))
        XCTAssertTrue(text.contains("droppedRecords=1"))
        XCTAssertTrue(text.contains("attempt=2"))
        XCTAssertFalse(text.contains("attempt=1"))
    }
}
