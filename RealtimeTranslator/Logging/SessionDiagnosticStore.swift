import Foundation

struct SessionDiagnosticMetrics: Sendable {
    var capturedFrames = 0
    var sentFrames = 0
    var sourceReceivedEvents = 0
    var translationReceivedEvents = 0
    var intervalPeakPermille = 0

    var description: String {
        "capturedFrames=\(capturedFrames) sentFrames=\(sentFrames) "
            + "sourceReceivedEvents=\(sourceReceivedEvents) translationReceivedEvents=\(translationReceivedEvents) "
            + "intervalPeakPermille=\(intervalPeakPermille)"
    }
}

/// 自由文・音声・認証情報を受け取らない、ローカル診断専用の入力型。
enum SessionDiagnosticEvent: Sendable {
    case attemptStarted(generation: Int, attempt: Int)
    case attemptEpoch(generation: Int, attempt: Int, epoch: Int)
    case snapshot(SessionHealthSnapshot, SessionDiagnosticMetrics)
    case detection(SessionHealthDetection)
    case termination(SessionTerminationDiagnostic)

    var description: String {
        switch self {
        case .attemptStarted(let generation, let attempt):
            "attemptStarted generation=\(generation) attempt=\(attempt)"
        case .attemptEpoch(let generation, let attempt, let epoch):
            "attemptEpoch generation=\(generation) attempt=\(attempt) epoch=\(epoch)"
        case .snapshot(let snapshot, let metrics):
            "snapshot \(snapshot.description) \(metrics.description)"
        case .detection(let detection):
            "detection \(detection.description)"
        case .termination(let diagnostic):
            "termination \(diagnostic.description)"
        }
    }
}

/// Release でも残す内容非保存の診断。ディスク I/O は専用 queue に隔離する。
final class SessionDiagnosticStore: @unchecked Sendable {
    static let shared = SessionDiagnosticStore(
        directory: FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Logs/RealtimeTranslator", isDirectory: true)
    )
    static let fileByteLimit = 1_048_576
    static let pendingLimit = 64

    private let directory: URL
    private let byteLimit: Int
    private let queue: DispatchQueue
    private let now: @Sendable () -> Date
    private let version: String
    private let lock = NSLock()
    private var pending: [(Date, SessionDiagnosticEvent)] = []
    private var draining = false
    private var droppedRecords = 0
    private let formatter = ISO8601DateFormatter()

    init(
        directory: URL,
        byteLimit: Int = SessionDiagnosticStore.fileByteLimit,
        version: String = AppReleaseVersion.current,
        now: @escaping @Sendable () -> Date = { Date() },
        queue: DispatchQueue = DispatchQueue(label: "com.realtimetranslator.diagnostics", qos: .utility)
    ) {
        self.directory = directory
        self.byteLimit = max(1_024, byteLimit)
        self.version = Self.safeVersion(version)
        self.now = now
        self.queue = queue
    }

    func record(_ event: SessionDiagnosticEvent) {
        lock.lock()
        if pending.count == Self.pendingLimit {
            pending.removeFirst()
            droppedRecords += 1
        }
        pending.append((now(), event))
        let shouldSchedule = !draining
        draining = true
        lock.unlock()
        if shouldSchedule {
            queue.async { self.drain() }
        }
    }

    /// テストから書き込み完了を待つ。録音経路からは呼ばない。
    func waitForPendingWrites() {
        queue.sync {}
    }

    private func drain() {
        while true {
            lock.lock()
            guard !pending.isEmpty else {
                draining = false
                lock.unlock()
                return
            }
            let (timestamp, event) = pending.removeFirst()
            let dropped = droppedRecords
            droppedRecords = 0
            lock.unlock()

            let line =
                "\(formatter.string(from: timestamp)) schema=1 appVersion=\(version) "
                + "pid=\(ProcessInfo.processInfo.processIdentifier) droppedRecords=\(dropped) "
                + "\(event.description)\n"
            do {
                try append(Data(line.utf8))
            } catch {
                // 診断の失敗は音声・接続処理へ伝播させず、次回成功時に件数だけ残す。
                lock.lock()
                droppedRecords += dropped + 1
                lock.unlock()
            }
        }
    }

    private func append(_ data: Data) throws {
        guard data.count <= byteLimit else { throw CocoaError(.fileWriteOutOfSpace) }
        let manager = FileManager.default
        if let attributes = try? manager.attributesOfItem(atPath: directory.path) {
            guard attributes[.type] as? FileAttributeType == .typeDirectory else {
                throw CocoaError(.fileWriteInvalidFileName)
            }
        } else {
            try manager.createDirectory(
                at: directory, withIntermediateDirectories: true,
                attributes: [.posixPermissions: 0o700]
            )
        }
        try manager.setAttributes([.posixPermissions: 0o700], ofItemAtPath: directory.path)
        let current = directory.appendingPathComponent("diagnostics.log")
        let previous = directory.appendingPathComponent("diagnostics.previous.log")
        for url in [current, previous] {
            guard let attributes = try? manager.attributesOfItem(atPath: url.path) else { continue }
            guard attributes[.type] as? FileAttributeType == .typeRegular else {
                throw CocoaError(.fileWriteInvalidFileName)
            }
            if ((attributes[.size] as? NSNumber)?.intValue ?? 0) > byteLimit {
                try manager.removeItem(at: url)
            } else {
                try manager.setAttributes([.posixPermissions: 0o600], ofItemAtPath: url.path)
            }
        }
        if manager.fileExists(atPath: current.path) {
            let size = (try manager.attributesOfItem(atPath: current.path)[.size] as? NSNumber)?.intValue ?? 0
            if size + data.count > byteLimit {
                if manager.fileExists(atPath: previous.path) {
                    try manager.removeItem(at: previous)
                }
                try manager.moveItem(at: current, to: previous)
            }
        }
        if !manager.fileExists(atPath: current.path) {
            guard manager.createFile(atPath: current.path, contents: nil, attributes: [.posixPermissions: 0o600]) else {
                throw CocoaError(.fileWriteUnknown)
            }
        }
        let handle = try FileHandle(forWritingTo: current)
        defer { try? handle.close() }
        try handle.seekToEnd()
        try handle.write(contentsOf: data)
    }

    private static func safeVersion(_ value: String) -> String {
        let allowed = Set("0123456789.")
        guard !value.isEmpty, value.count <= 32, value.allSatisfy({ allowed.contains($0) }) else {
            return "unknown"
        }
        return value
    }
}
