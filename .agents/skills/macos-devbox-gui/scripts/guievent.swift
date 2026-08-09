import ApplicationServices
import CoreGraphics
import Foundation

// Coordinates accepted by this tool are logical display coordinates:
// the measured display is 1280x800, with the origin at the top-left.
// Screenshots are 2x Retina (2560x1600), so their pixel coordinates must
// not be passed directly to CGEvent.

enum CommandError: Error, CustomStringConvertible {
    case usage(String)
    case invalidNumber(String)
    case unsupportedCommand(String)

    var description: String {
        switch self {
        case .usage(let message), .unsupportedCommand(let message):
            return message
        case .invalidNumber(let value):
            return "invalid number: \(value)"
        }
    }
}

func printHelp() {
    print(
        """
        Usage:
          guievent.swift list
          guievent.swift click <x> <y>
          guievent.swift doubleclick <x> <y>
          guievent.swift key [--flags <names>] <virtualKey> [virtualKey...]
          guievent.swift --help

        Coordinates:
          x and y are logical screen coordinates, not PNG pixels.
          The measured display is 1280x800 with origin at the top-left.
          screencapture PNGs are 2560x1600 (2x Retina).

        Key flags:
          --flags command,option,control,shift
          Virtual-key values are the macOS CGKeyCode values, for example:
            8=c, 24=+, 67=*, 88=7

        Examples:
          swift guievent.swift list
          swift guievent.swift click 500 200
          swift guievent.swift doubleclick 412 238
          swift guievent.swift key --flags command 43
          swift guievent.swift key 8 24 8
        """
    )
}

func parseDouble(_ raw: String) throws -> Double {
    guard let value = Double(raw) else {
        throw CommandError.invalidNumber(raw)
    }
    return value
}

func parseKeyCode(_ raw: String) throws -> CGKeyCode {
    guard let value = UInt16(raw) else {
        throw CommandError.invalidNumber(raw)
    }
    return CGKeyCode(value)
}

func flags(from raw: String) -> CGEventFlags {
    raw.split(separator: ",").reduce(into: CGEventFlags()) { result, name in
        switch name.lowercased() {
        case "command", "cmd":
            result.insert(.maskCommand)
        case "option", "alt":
            result.insert(.maskAlternate)
        case "control", "ctrl":
            result.insert(.maskControl)
        case "shift":
            result.insert(.maskShift)
        case "":
            break
        default:
            fputs("warning: unknown modifier '\(name)'\n", stderr)
        }
    }
}

func click(at point: CGPoint, clickState: Int64 = 1) {
    CGWarpMouseCursorPosition(point)
    usleep(300_000)

    let down = CGEvent(
        mouseEventSource: nil,
        mouseType: .leftMouseDown,
        mouseCursorPosition: point,
        mouseButton: .left
    )!
    down.setIntegerValueField(.mouseEventClickState, value: clickState)
    down.post(tap: .cghidEventTap)

    usleep(80_000)

    let up = CGEvent(
        mouseEventSource: nil,
        mouseType: .leftMouseUp,
        mouseCursorPosition: point,
        mouseButton: .left
    )!
    up.setIntegerValueField(.mouseEventClickState, value: clickState)
    up.post(tap: .cghidEventTap)
}

func doubleClick(at point: CGPoint) {
    click(at: point, clickState: 1)
    usleep(120_000)
    click(at: point, clickState: 2)
}

func sendKey(_ keyCode: CGKeyCode, flags: CGEventFlags) {
    let source = CGEventSource(stateID: .hidSystemState)
    let down = CGEvent(keyboardEventSource: source, virtualKey: keyCode, keyDown: true)!
    down.flags = flags
    down.post(tap: .cghidEventTap)
    usleep(70_000)
    let up = CGEvent(keyboardEventSource: source, virtualKey: keyCode, keyDown: false)!
    up.flags = flags
    up.post(tap: .cghidEventTap)
    usleep(70_000)
}

func listWindows() {
    let windows = CGWindowListCopyWindowInfo(
        [.optionOnScreenOnly, .excludeDesktopElements],
        kCGNullWindowID
    ) as? [[String: Any]] ?? []

    for window in windows {
        let number = window[kCGWindowNumber as String] as? UInt32 ?? 0
        let owner = window[kCGWindowOwnerName as String] as? String ?? "?"
        let pid = window[kCGWindowOwnerPID as String] as? Int32 ?? 0
        let name = window[kCGWindowName as String] as? String ?? ""
        let layer = window[kCGWindowLayer as String] as? Int ?? -1
        let alpha = window[kCGWindowAlpha as String] as? Double ?? -1
        let isOnscreen = window[kCGWindowIsOnscreen as String] as? Bool ?? false
        let bounds = window[kCGWindowBounds as String] as? [String: Any] ?? [:]
        print(
            "window=\(number) owner=\(owner) pid=\(pid) name=\(name.debugDescription) " +
            "layer=\(layer) alpha=\(alpha) onScreen=\(isOnscreen) bounds=\(bounds)"
        )
    }
}

do {
    let arguments = Array(CommandLine.arguments.dropFirst())
    guard let command = arguments.first else {
        printHelp()
        exit(0)
    }

    switch command {
    case "--help", "-h", "help":
        printHelp()
    case "list":
        listWindows()
    case "click":
        guard arguments.count == 3 else {
            throw CommandError.usage("click requires <x> <y>; use --help for usage")
        }
        let point = CGPoint(x: try parseDouble(arguments[1]), y: try parseDouble(arguments[2]))
        print("AXIsProcessTrusted=\(AXIsProcessTrusted())")
        click(at: point)
        print("clicked at \(point)")
    case "doubleclick":
        guard arguments.count == 3 else {
            throw CommandError.usage("doubleclick requires <x> <y>; use --help for usage")
        }
        let point = CGPoint(x: try parseDouble(arguments[1]), y: try parseDouble(arguments[2]))
        print("AXIsProcessTrusted=\(AXIsProcessTrusted())")
        doubleClick(at: point)
        print("double-clicked at \(point)")
    case "key":
        var index = 1
        var eventFlags = CGEventFlags()
        if arguments.count > index, arguments[index] == "--flags" {
            guard arguments.count > index + 1 else {
                throw CommandError.usage("--flags requires a comma-separated modifier list")
            }
            eventFlags = flags(from: arguments[index + 1])
            index += 2
        }
        guard arguments.count > index else {
            throw CommandError.usage("key requires at least one virtualKey; use --help for usage")
        }
        print("AXIsProcessTrusted=\(AXIsProcessTrusted())")
        for raw in arguments[index...] {
            sendKey(try parseKeyCode(raw), flags: eventFlags)
        }
        print("sent \(arguments.count - index) key event(s)")
    default:
        throw CommandError.unsupportedCommand("unknown command '\(command)'; use --help for usage")
    }
} catch {
    fputs("error: \(error)\n", stderr)
    exit(2)
}
