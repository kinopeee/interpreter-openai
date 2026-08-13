import Foundation

enum SubtitleTranscriptAppendResult: Equatable, Sendable {
    case appended
    case skippedDuplicate
    case skippedEmpty
    case capped
    case failed
}

/// 確定字幕ペアをローカルファイルへ追記する。件数に比例するメモリは持たない。
final class SubtitleTranscriptStore: @unchecked Sendable {
    static var sizeLimitBanner: String { SubtitleTranscriptLimits.sizeLimitBanner }
    static var writeFailureBanner: String { SubtitleTranscriptLimits.writeFailureBanner }
    static var maxFileBytes: Int { SubtitleTranscriptLimits.maxFileBytes }

    private let fileURL: URL
    private let now: () -> Date
    private let timeZone: TimeZone
    private let maxFileBytes: Int
    private let lock = NSLock()

    private var lastSource: String?
    private var lastTranslation: String?
    private var announcedSizeLimit = false
    private var cachedByteCount: Int?

    init(
        fileURL: URL,
        now: @escaping () -> Date = { Date() },
        timeZone: TimeZone = .current,
        maxFileBytes: Int = SubtitleTranscriptLimits.maxFileBytes
    ) {
        self.fileURL = fileURL
        self.now = now
        self.timeZone = timeZone
        self.maxFileBytes = maxFileBytes
    }

    var hasEntries: Bool {
        lock.lock()
        defer { lock.unlock() }
        return fileByteCountLocked() > 0
    }

    /// Application Support 下の既定パス。
    static func defaultFileURL(
        fileManager: FileManager = .default,
        bundleIdentifier: String? = Bundle.main.bundleIdentifier
    ) throws -> URL {
        guard let bundleIdentifier else {
            throw CocoaError(.fileNoSuchFile)
        }
        guard let applicationSupport = fileManager.urls(
            for: .applicationSupportDirectory,
            in: .userDomainMask
        ).first else {
            throw CocoaError(.fileNoSuchFile)
        }
        let directory = applicationSupport
            .appendingPathComponent(bundleIdentifier, isDirectory: true)
            .appendingPathComponent("transcripts", isDirectory: true)
        try fileManager.createDirectory(at: directory, withIntermediateDirectories: true)
        return directory.appendingPathComponent("session.txt", isDirectory: false)
    }

    @discardableResult
    func markSessionStart() -> SubtitleTranscriptAppendResult {
        lock.lock()
        defer { lock.unlock() }
        // 新セッションでは直前セッション末尾との連続重複判定を切る。
        lastSource = nil
        lastTranslation = nil
        let timestamp = SubtitleTranscriptFormatter.formatTimestamp(now(), timeZone: timeZone)
        let chunk = SubtitleTranscriptFormatter.formatSessionStart(timestamp: timestamp)
        return appendChunkLocked(chunk, updatingLastPair: nil)
    }

    @discardableResult
    func appendEntry(sourceText: String, translatedText: String) -> SubtitleTranscriptAppendResult {
        lock.lock()
        defer { lock.unlock() }

        let trimmedSource = sourceText.trimmingCharacters(in: .whitespacesAndNewlines)
        let trimmedTranslation = translatedText.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmedSource.isEmpty, !trimmedTranslation.isEmpty else {
            return .skippedEmpty
        }
        if sourceText == lastSource, translatedText == lastTranslation {
            return .skippedDuplicate
        }

        let timestamp = SubtitleTranscriptFormatter.formatTimestamp(now(), timeZone: timeZone)
        let chunk = SubtitleTranscriptFormatter.formatEntry(
            timestamp: timestamp,
            sourceText: sourceText,
            translatedText: translatedText
        )
        return appendChunkLocked(chunk, updatingLastPair: (sourceText, translatedText))
    }

    func exportCopy(to destination: URL) throws {
        lock.lock()
        defer { lock.unlock() }
        // 自己コピーは記録を消さないよう no-op にする。
        guard destination.standardizedFileURL != fileURL.standardizedFileURL else {
            return
        }
        let fileManager = FileManager.default
        guard fileManager.fileExists(atPath: fileURL.path), fileByteCountLocked() > 0 else {
            throw CocoaError(.fileReadNoSuchFile)
        }
        if fileManager.fileExists(atPath: destination.path) {
            try fileManager.removeItem(at: destination)
        }
        try fileManager.copyItem(at: fileURL, to: destination)
    }

    func clear() throws {
        lock.lock()
        defer { lock.unlock() }
        let fileManager = FileManager.default
        let directory = fileURL.deletingLastPathComponent()
        try fileManager.createDirectory(at: directory, withIntermediateDirectories: true)
        try Data().write(to: fileURL, options: .atomic)
        lastSource = nil
        lastTranslation = nil
        announcedSizeLimit = false
        cachedByteCount = 0
    }

    /// 既定の書き出しファイル名 `subtitles-YYYYMMDD-HHmmss.txt`。
    static func defaultExportFileName(now: Date = Date(), timeZone: TimeZone = .current) -> String {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.timeZone = timeZone
        formatter.dateFormat = "yyyyMMdd-HHmmss"
        return "subtitles-\(formatter.string(from: now)).txt"
    }

    private func appendChunkLocked(
        _ chunk: String,
        updatingLastPair: (String, String)?
    ) -> SubtitleTranscriptAppendResult {
        let chunkData = Data(chunk.utf8)
        let currentBytes = fileByteCountLocked()
        if currentBytes >= maxFileBytes
            || currentBytes + chunkData.count > maxFileBytes
        {
            if announcedSizeLimit {
                return .capped
            }
            announcedSizeLimit = true
            return .capped
        }

        do {
            try ensureFileExistsLocked()
            let handle = try FileHandle(forWritingTo: fileURL)
            defer { try? handle.close() }
            try handle.seekToEnd()
            try handle.write(contentsOf: chunkData)
            try handle.synchronize()
            cachedByteCount = currentBytes + chunkData.count
            if let updatingLastPair {
                lastSource = updatingLastPair.0
                lastTranslation = updatingLastPair.1
            }
            return .appended
        } catch {
            // 失敗したペアも記憶し、ticker からの同一再試行で失敗バナーを連発しない。
            if let updatingLastPair {
                lastSource = updatingLastPair.0
                lastTranslation = updatingLastPair.1
            }
            return .failed
        }
    }

    private func ensureFileExistsLocked() throws {
        let fileManager = FileManager.default
        let directory = fileURL.deletingLastPathComponent()
        try fileManager.createDirectory(at: directory, withIntermediateDirectories: true)
        if !fileManager.fileExists(atPath: fileURL.path) {
            try Data().write(to: fileURL, options: .atomic)
            cachedByteCount = 0
        }
    }

    private func fileByteCountLocked() -> Int {
        if let cachedByteCount {
            return cachedByteCount
        }
        let values = try? fileURL.resourceValues(forKeys: [.fileSizeKey])
        let size = values?.fileSize ?? 0
        cachedByteCount = size
        return size
    }
}
