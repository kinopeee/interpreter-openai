import CoreGraphics

enum DisplayMirroringDetector {
    static func isMirroringActive() -> Bool {
        var displayCount: UInt32 = 0
        guard CGGetOnlineDisplayList(0, nil, &displayCount) == .success, displayCount > 0 else {
            return false
        }
        var displays = [CGDirectDisplayID](repeating: 0, count: Int(displayCount))
        guard CGGetOnlineDisplayList(displayCount, &displays, &displayCount) == .success else {
            return false
        }
        return displays.prefix(Int(displayCount)).contains { CGDisplayIsInMirrorSet($0) != 0 }
    }
}
