import Darwin
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
    static var applicationStore: SessionDiagnosticStore? {
        guard !AppRuntimeEnvironment.isRunningXCTest() else { return nil }
        return shared
    }
    static let fileByteLimit = 1_048_576
    static let pendingLimit = 64

    private let directory: URL
    private let byteLimit: Int
    private let queue: DispatchQueue
    private let now: @Sendable () -> Date
    private let beforeOpeningLog: @Sendable () throws -> Void
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
        queue: DispatchQueue = DispatchQueue(label: "com.realtimetranslator.diagnostics", qos: .utility),
        beforeOpeningLog: @escaping @Sendable () throws -> Void = {}
    ) {
        self.directory = directory
        self.byteLimit = max(1_024, byteLimit)
        self.version = Self.safeVersion(version)
        self.now = now
        self.queue = queue
        self.beforeOpeningLog = beforeOpeningLog
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
        let directoryDescriptor = Darwin.open(directory.path, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC)
        guard directoryDescriptor >= 0 else { throw CocoaError(.fileWriteInvalidFileName) }
        defer { Darwin.close(directoryDescriptor) }
        var directoryStatus = stat()
        guard fstat(directoryDescriptor, &directoryStatus) == 0,
            directoryStatus.st_uid == geteuid(), fchmod(directoryDescriptor, 0o700) == 0
        else { throw CocoaError(.fileWriteNoPermission) }

        // 検査後もパスを開き直さず、固定したディレクトリを基準に処理する。
        try beforeOpeningLog()
        let current = "diagnostics.log"
        let previous = "diagnostics.previous.log"
        if let previousHandle = try openLogFile(previous, in: directoryDescriptor, create: false) {
            defer { try? previousHandle.close() }
            if try fileSize(previousHandle) > Int64(byteLimit) {
                try removeLogFile(previous, in: directoryDescriptor)
            }
        }
        guard var handle = try openLogFile(current, in: directoryDescriptor, create: true) else {
            throw CocoaError(.fileWriteUnknown)
        }
        defer { try? handle.close() }
        let size = try fileSize(handle)
        let remainingCapacity = Int64(byteLimit - data.count)
        if size > Int64(byteLimit) {
            try removeLogFile(current, in: directoryDescriptor)
        } else if size > remainingCapacity {
            try removeLogFile(previous, in: directoryDescriptor)
            guard renameat(directoryDescriptor, current, directoryDescriptor, previous) == 0 else {
                throw CocoaError(.fileWriteUnknown)
            }
        }
        if size > remainingCapacity {
            try handle.close()
            guard let replacement = try openLogFile(current, in: directoryDescriptor, create: true) else {
                throw CocoaError(.fileWriteUnknown)
            }
            handle = replacement
        }
        guard try fileSize(handle) <= remainingCapacity else { throw CocoaError(.fileWriteOutOfSpace) }
        try handle.write(contentsOf: data)
    }

    private func openLogFile(_ name: String, in directoryDescriptor: Int32, create: Bool) throws -> FileHandle? {
        let safeFlags = O_NOFOLLOW | O_NONBLOCK | O_CLOEXEC
        let flags = O_WRONLY | O_APPEND | safeFlags | (create ? O_CREAT : 0)
        var descriptor = openat(directoryDescriptor, name, flags, mode_t(0o600))
        if descriptor < 0 && errno == EACCES {
            let repairDescriptor = openat(directoryDescriptor, name, O_RDONLY | safeFlags)
            guard repairDescriptor >= 0 else { throw CocoaError(.fileWriteNoPermission) }
            defer { Darwin.close(repairDescriptor) }
            try protectLogFile(repairDescriptor)
            descriptor = openat(directoryDescriptor, name, flags, mode_t(0o600))
        }
        guard descriptor >= 0 else {
            if !create && errno == ENOENT { return nil }
            throw CocoaError(.fileWriteInvalidFileName)
        }
        do {
            try protectLogFile(descriptor)
        } catch {
            Darwin.close(descriptor)
            throw error
        }
        return FileHandle(fileDescriptor: descriptor, closeOnDealloc: true)
    }

    private func protectLogFile(_ descriptor: Int32) throws {
        var status = stat()
        guard fstat(descriptor, &status) == 0,
            status.st_mode & S_IFMT == S_IFREG, status.st_uid == geteuid(), status.st_nlink == 1,
            fchmod(descriptor, 0o600) == 0
        else {
            throw CocoaError(.fileWriteNoPermission)
        }
    }

    private func fileSize(_ handle: FileHandle) throws -> Int64 {
        var status = stat()
        guard fstat(handle.fileDescriptor, &status) == 0 else { throw CocoaError(.fileReadUnknown) }
        return status.st_size
    }

    private func removeLogFile(_ name: String, in directoryDescriptor: Int32) throws {
        guard unlinkat(directoryDescriptor, name, 0) == 0 || errno == ENOENT else {
            throw CocoaError(.fileWriteUnknown)
        }
    }

    private static func safeVersion(_ value: String) -> String {
        let allowed = Set("0123456789.")
        guard !value.isEmpty, value.count <= 32, value.allSatisfy({ allowed.contains($0) }) else {
            return "unknown"
        }
        return value
    }
}
