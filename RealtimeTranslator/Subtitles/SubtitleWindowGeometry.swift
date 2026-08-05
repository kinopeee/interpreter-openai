import CoreGraphics

struct SubtitleWindowLayout: Equatable {
    var subtitleFrame: CGRect
    var controlFrame: CGRect

    var combinedFrame: CGRect {
        subtitleFrame.union(controlFrame)
    }
}

enum SubtitleWindowGeometry {
    /// 一時的に字幕下の録音ボタンを隠す。再表示するときは `true` に戻す。
    static let showsRecordingControl = false

    static let controlSize = CGSize(width: 160, height: 48)
    static let controlSpacing: CGFloat = 8

    private static let maximumSubtitleWidth: CGFloat = 1200
    private static let subtitleWidthRatio: CGFloat = 0.70
    private static let defaultBottomOffsetWithControl: CGFloat = 104
    private static let defaultBottomOffsetWithoutControl: CGFloat = 24

    private static var defaultBottomOffset: CGFloat {
        showsRecordingControl
            ? defaultBottomOffsetWithControl
            : defaultBottomOffsetWithoutControl
    }

    private static var reservedControlHeight: CGFloat {
        showsRecordingControl ? controlSize.height + controlSpacing : 0
    }

    static func subtitleWidth(in visibleFrame: CGRect) -> CGFloat {
        min(max(0, visibleFrame.width * subtitleWidthRatio), maximumSubtitleWidth)
    }

    static func subtitleHeight(
        measuredContentHeight: CGFloat,
        in visibleFrame: CGRect
    ) -> CGFloat {
        let availableHeight = max(0, visibleFrame.height - reservedControlHeight)
        return min(max(0, ceil(measuredContentHeight)), availableHeight)
    }

    static func defaultOrigin(
        in visibleFrame: CGRect,
        subtitleSize: CGSize
    ) -> CGPoint {
        CGPoint(
            x: visibleFrame.midX - subtitleSize.width / 2,
            y: visibleFrame.minY + defaultBottomOffset
        )
    }

    static func screenIndex(
        containing origin: CGPoint,
        in screenFrames: [CGRect],
        fallbackIndex: Int?
    ) -> Int? {
        guard !screenFrames.isEmpty else { return nil }

        if origin.x.isFinite, origin.y.isFinite,
           let index = screenFrames.firstIndex(where: { contains(origin, in: $0) }) {
            return index
        }
        if let fallbackIndex, screenFrames.indices.contains(fallbackIndex) {
            return fallbackIndex
        }
        return screenFrames.startIndex
    }

    static func screenIndex(
        bestMatching proposedFrame: CGRect,
        in screenFrames: [CGRect],
        fallbackIndex: Int?
    ) -> Int? {
        guard !screenFrames.isEmpty else { return nil }

        let candidates: [(index: Int, area: CGFloat)] = screenFrames.indices
            .map { index in
                let intersection = proposedFrame.intersection(screenFrames[index])
                let area: CGFloat = intersection.isNull || intersection.isEmpty
                    ? 0
                    : intersection.width * intersection.height
                return (index: index, area: area)
            }
        let bestMatch = candidates
            .filter { $0.area > 0 }
            .max { first, second in
                if first.area == second.area {
                    return first.index > second.index
                }
                return first.area < second.area
            }
        if let bestMatch {
            return bestMatch.index
        }
        if let fallbackIndex, screenFrames.indices.contains(fallbackIndex) {
            return fallbackIndex
        }
        return screenFrames.startIndex
    }

    static func layout(
        subtitleOrigin: CGPoint,
        subtitleSize: CGSize,
        in visibleFrame: CGRect
    ) -> SubtitleWindowLayout {
        let subtitleFrame = CGRect(origin: subtitleOrigin, size: subtitleSize)
        let controlFrame = showsRecordingControl
            ? controlFrame(for: subtitleFrame)
            : CGRect(origin: subtitleFrame.origin, size: .zero)
        return clamped(
            SubtitleWindowLayout(
                subtitleFrame: subtitleFrame,
                controlFrame: controlFrame
            ),
            to: visibleFrame
        )
    }

    static func controlFrame(for subtitleFrame: CGRect) -> CGRect {
        CGRect(
            x: subtitleFrame.midX - controlSize.width / 2,
            y: subtitleFrame.minY - controlSpacing - controlSize.height,
            width: controlSize.width,
            height: controlSize.height
        )
    }

    private static func clamped(
        _ layout: SubtitleWindowLayout,
        to visibleFrame: CGRect
    ) -> SubtitleWindowLayout {
        let frameToClamp = showsRecordingControl
            ? layout.combinedFrame
            : layout.subtitleFrame
        let dx = offset(
            lowerBound: frameToClamp.minX,
            upperBound: frameToClamp.maxX,
            containerLowerBound: visibleFrame.minX,
            containerUpperBound: visibleFrame.maxX
        )
        let dy = offset(
            lowerBound: frameToClamp.minY,
            upperBound: frameToClamp.maxY,
            containerLowerBound: visibleFrame.minY,
            containerUpperBound: visibleFrame.maxY
        )

        return SubtitleWindowLayout(
            subtitleFrame: layout.subtitleFrame.offsetBy(dx: dx, dy: dy),
            controlFrame: layout.controlFrame.offsetBy(dx: dx, dy: dy)
        )
    }

    private static func offset(
        lowerBound: CGFloat,
        upperBound: CGFloat,
        containerLowerBound: CGFloat,
        containerUpperBound: CGFloat
    ) -> CGFloat {
        let extent = upperBound - lowerBound
        let containerExtent = containerUpperBound - containerLowerBound
        if extent > containerExtent {
            return containerLowerBound - lowerBound
        }
        if lowerBound < containerLowerBound {
            return containerLowerBound - lowerBound
        }
        if upperBound > containerUpperBound {
            return containerUpperBound - upperBound
        }
        return 0
    }

    private static func contains(_ point: CGPoint, in frame: CGRect) -> Bool {
        point.x >= frame.minX
            && point.x < frame.maxX
            && point.y >= frame.minY
            && point.y < frame.maxY
    }
}
