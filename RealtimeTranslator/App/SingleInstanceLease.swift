import Darwin
import Foundation

enum SingleInstanceLeaseError: Error {
    case applicationSupportDirectoryUnavailable
    case bundleIdentifierUnavailable
    case invalidLockPath
    case openFailed(Int32)
    case ownerMetadataWriteFailed(Int32)
}

/// `O_EXLOCK`付きファイルロックによる多重起動防止。
/// 同一Bundle IDのプロセスが既にロックを保持していれば`acquire`は`nil`を返す。
/// プロセス異常終了時はOSがロックを自動解放するため、stale lockは発生しない。
final class SingleInstanceLease {
    private var fileDescriptor: Int32?

    private init(fileDescriptor: Int32) {
        self.fileDescriptor = fileDescriptor
    }

    static func acquireForCurrentUser() throws -> SingleInstanceLease? {
        guard let bundleIdentifier = Bundle.main.bundleIdentifier else {
            throw SingleInstanceLeaseError.bundleIdentifierUnavailable
        }
        guard
            let applicationSupportDirectory = FileManager.default.urls(
                for: .applicationSupportDirectory,
                in: .userDomainMask
            ).first
        else {
            throw SingleInstanceLeaseError.applicationSupportDirectoryUnavailable
        }

        let runtimeDirectory = applicationSupportDirectory.appendingPathComponent(
            bundleIdentifier,
            isDirectory: true
        )
        try FileManager.default.createDirectory(
            at: runtimeDirectory,
            withIntermediateDirectories: true
        )
        return try acquire(
            at: runtimeDirectory.appendingPathComponent("instance.lock")
        )
    }

    static func acquire(at lockURL: URL) throws -> SingleInstanceLease? {
        let flags = O_CREAT | O_RDWR | O_EXLOCK | O_NONBLOCK | O_CLOEXEC | O_NOFOLLOW
        guard
            let descriptor = lockURL.withUnsafeFileSystemRepresentation({ path -> Int32? in
                guard let path else { return nil }
                return Darwin.open(
                    path,
                    flags,
                    mode_t(S_IRUSR | S_IWUSR)
                )
            })
        else {
            throw SingleInstanceLeaseError.invalidLockPath
        }

        guard descriptor >= 0 else {
            let errorNumber = errno
            if errorNumber == EWOULDBLOCK || errorNumber == EAGAIN {
                return nil
            }
            throw SingleInstanceLeaseError.openFailed(errorNumber)
        }
        do {
            try writeOwnerPID(to: descriptor)
        } catch {
            Darwin.close(descriptor)
            throw error
        }
        return SingleInstanceLease(fileDescriptor: descriptor)
    }

    func release() {
        guard let fileDescriptor else { return }
        self.fileDescriptor = nil
        _ = Darwin.ftruncate(fileDescriptor, 0)
        Darwin.close(fileDescriptor)
    }

    deinit {
        release()
    }

    private static func writeOwnerPID(to descriptor: Int32) throws {
        guard Darwin.ftruncate(descriptor, 0) == 0,
            Darwin.lseek(descriptor, 0, SEEK_SET) >= 0
        else {
            throw SingleInstanceLeaseError.ownerMetadataWriteFailed(errno)
        }

        let metadata = "\(Darwin.getpid())\n"
        let bytes = Array(metadata.utf8)
        let written = bytes.withUnsafeBytes { rawBuffer -> Int in
            guard let baseAddress = rawBuffer.baseAddress else { return 0 }
            return Darwin.write(descriptor, baseAddress, rawBuffer.count)
        }
        guard written == bytes.count else {
            throw SingleInstanceLeaseError.ownerMetadataWriteFailed(errno)
        }
    }
}
