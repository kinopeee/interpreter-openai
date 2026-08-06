import AppKit
import Foundation

/// Pure AppKit entry. Avoid SwiftUI `App` lifecycle.
///
/// On macOS 26 + Swift 6, `NSApplicationDelegate` methods are MainActor-isolated by the SDK.
/// AppKit invokes `applicationShouldTerminateAfterLastWindowClosed` from a CFRunLoop timer;
/// the MainActor executor check then null-dereferences. All delegate callbacks are `nonisolated`.
@main
enum RealtimeTranslatorMain {
    static func main() {
        let app = NSApplication.shared
        let delegate = AppDelegate()
        AppDelegate.retained = delegate
        app.delegate = delegate
        app.setActivationPolicy(.accessory)

        // Bootstrap from the main queue after `run()` starts — do not rely solely on
        // `applicationDidFinishLaunching` (can race with MainActor isolation on macOS 26).
        DispatchQueue.main.async {
            MainActor.assumeIsolated {
                AppRuntime.start()
            }
        }

        app.run()
    }
}

enum AppStatusFile {
    static let path = "/tmp/realtimetranslator.status"

    static func write(_ status: String, state: String = "") {
        #if DEBUG
        let body = state.isEmpty ? "\(status)\n" : "\(status)\n\(state)\n"
        try? body.write(toFile: path, atomically: true, encoding: .utf8)
        #endif
    }
}

enum AppRuntimeEnvironment {
    static func isRunningXCTest(
        environment: [String: String] = ProcessInfo.processInfo.environment
    ) -> Bool {
        environment["XCTestConfigurationFilePath"] != nil
            || environment["XCTestBundlePath"] != nil
    }
}

@MainActor
enum AppRuntime {
    private(set) static var coordinator: AppCoordinator?
    private static var keepAliveWindow: NSWindow?
    private static var instanceLease: SingleInstanceLease?
    private static var didAttemptStart = false

    static func start() {
        guard !AppRuntimeEnvironment.isRunningXCTest() else { return }
        guard !didAttemptStart else { return }
        didAttemptStart = true

        do {
            guard let lease = try SingleInstanceLease.acquireForCurrentUser() else {
                AppLogger.general.notice(
                    "Another app instance owns the runtime lock; exiting duplicate"
                )
                NSApp.terminate(nil)
                return
            }
            instanceLease = lease
        } catch {
            AppLogger.general.error(
                "Failed to acquire runtime lock: \(AppLogger.redact(error.localizedDescription), privacy: .public)"
            )
            NSApp.terminate(nil)
            return
        }

        AppStatusFile.write("boot")
        AppMainMenu.install()
        installKeepAliveWindow()
        let coordinator = AppCoordinator()
        self.coordinator = coordinator
        coordinator.start()
    }

    private static func installKeepAliveWindow() {
        let window = NSWindow(
            contentRect: NSRect(x: -10_000, y: -10_000, width: 1, height: 1),
            styleMask: [.borderless],
            backing: .buffered,
            defer: false
        )
        window.isReleasedWhenClosed = false
        window.alphaValue = 0
        window.ignoresMouseEvents = true
        window.collectionBehavior = [.canJoinAllSpaces, .stationary, .ignoresCycle, .fullScreenAuxiliary]
        window.level = .normal
        window.orderBack(nil)
        keepAliveWindow = window
    }
}

final class AppDelegate: NSObject, NSApplicationDelegate {
    nonisolated(unsafe) static var retained: AppDelegate?

    nonisolated func applicationDidFinishLaunching(_ notification: Notification) {
        // Backup bootstrap path.
        DispatchQueue.main.async {
            MainActor.assumeIsolated {
                AppRuntime.start()
            }
        }
    }

    nonisolated func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool {
        false
    }

    nonisolated func applicationSupportsSecureRestorableState(_ app: NSApplication) -> Bool {
        false
    }
}
