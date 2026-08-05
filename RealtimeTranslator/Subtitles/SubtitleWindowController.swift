import AppKit
import SwiftUI

@MainActor
final class SubtitleWindowController: NSObject {
    private let panel: SubtitlePanel
    private let controlPanel: SubtitlePanel
    private let hostingController: NSHostingController<SubtitleView>
    private let subtitleContainerView: NSView
    private let controlHostingView: NSHostingView<RecordingControlView>
    private let controlContainerView = NSView()
    private var snapshot = SubtitleSnapshot.empty
    private var fontSize: Double = 32
    private var translationState: TranslationState = .idle
    private var onToggleRecording: () -> Void = {}
    private var isEditingPosition = false
    private var screenObserver: NSObjectProtocol?
    private var customOrigin: CGPoint?
    private var dragStartPanelOrigin: NSPoint?
    private var dragStartMouseLocation: NSPoint?

    override init() {
        let subtitleView = SubtitleView(
            snapshot: .empty,
            fontSize: 32,
            isEditingPosition: false
        )
        let subtitleHostingController = NSHostingController(rootView: subtitleView)
        let initialScreen = NSScreen.main ?? NSScreen.screens.first
        let initialLayout = Self.initialLayout(
            hostingController: subtitleHostingController,
            screen: initialScreen
        )

        panel = SubtitlePanel(contentRect: initialLayout.subtitleFrame)
        controlPanel = SubtitlePanel(contentRect: initialLayout.controlFrame)
        controlPanel.ignoresMouseEvents = false
        hostingController = subtitleHostingController
        subtitleContainerView = NSView(
            frame: NSRect(origin: .zero, size: initialLayout.subtitleFrame.size)
        )
        controlHostingView = NSHostingView(
            rootView: RecordingControlView(state: .idle, onToggleRecording: {})
        )
        super.init()
        subtitleContainerView.autoresizesSubviews = true
        hostingController.view.frame = subtitleContainerView.bounds
        hostingController.view.autoresizingMask = [.width, .height]
        subtitleContainerView.addSubview(hostingController.view)
        panel.contentView = subtitleContainerView
        panel.setFrame(initialLayout.subtitleFrame, display: false)
        panel.orderFrontRegardless()
        controlContainerView.frame = controlPanel.contentView?.bounds ?? .zero
        controlContainerView.autoresizingMask = [.width, .height]
        controlHostingView.frame = controlContainerView.bounds
        controlHostingView.autoresizingMask = [.width, .height]
        controlContainerView.addSubview(controlHostingView)
        controlPanel.contentView = controlContainerView
        if SubtitleWindowGeometry.showsRecordingControl {
            controlPanel.orderFrontRegardless()
        } else {
            controlPanel.orderOut(nil)
        }

        screenObserver = NotificationCenter.default.addObserver(
            forName: NSApplication.didChangeScreenParametersNotification,
            object: nil,
            queue: .main
        ) { [weak self] _ in
            Task { @MainActor in
                self?.relayoutIfNeeded()
            }
        }
    }

    func tearDown() {
        if let screenObserver {
            NotificationCenter.default.removeObserver(screenObserver)
            self.screenObserver = nil
        }
        removeDragMonitor()
        panel.orderOut(nil)
        controlPanel.orderOut(nil)
    }

    func show() {
        panel.orderFrontRegardless()
        if SubtitleWindowGeometry.showsRecordingControl {
            controlPanel.orderFrontRegardless()
        } else {
            controlPanel.orderOut(nil)
        }
    }

    func update(
        snapshot: SubtitleSnapshot,
        fontSize: Double,
        translationState: TranslationState
    ) {
        let shouldRenderSubtitles = self.snapshot.presentation != snapshot.presentation
            || self.fontSize != fontSize
        let shouldRenderControls = self.translationState != translationState
        self.snapshot = snapshot
        self.fontSize = fontSize
        self.translationState = translationState
        if shouldRenderSubtitles {
            renderSubtitles()
            relayoutIfNeeded()
        }
        if shouldRenderControls {
            renderControls()
        }
    }

    func setRecordingHandler(_ handler: @escaping () -> Void) {
        onToggleRecording = handler
        renderControls()
    }

    func applySavedOrigin(_ origin: CGPoint?) {
        customOrigin = origin
        relayoutIfNeeded(forceDefault: origin == nil)
    }

    func setPositionEditingEnabled(_ enabled: Bool) {
        isEditingPosition = enabled
        panel.ignoresMouseEvents = !enabled
        if enabled {
            panel.makeKeyAndOrderFront(nil)
            installDragMonitor()
        } else {
            removeDragMonitor()
        }
        renderSubtitles()
    }

    var currentOrigin: CGPoint {
        panel.frame.origin
    }

    private var dragMonitor: Any?

    private func installDragMonitor() {
        removeDragMonitor()
        dragMonitor = NSEvent.addLocalMonitorForEvents(matching: [.leftMouseDown, .leftMouseDragged, .leftMouseUp]) { [weak self] event in
            self?.handleDrag(event)
            return event
        }
    }

    private func removeDragMonitor() {
        if let dragMonitor {
            NSEvent.removeMonitor(dragMonitor)
            self.dragMonitor = nil
        }
        dragStartPanelOrigin = nil
        dragStartMouseLocation = nil
    }

    private func handleDrag(_ event: NSEvent) {
        switch event.type {
        case .leftMouseDown:
            dragStartPanelOrigin = panel.frame.origin
            dragStartMouseLocation = NSEvent.mouseLocation
        case .leftMouseDragged:
            guard
                let startOrigin = dragStartPanelOrigin,
                let startMouse = dragStartMouseLocation
            else { return }
            let current = NSEvent.mouseLocation
            let delta = NSPoint(x: current.x - startMouse.x, y: current.y - startMouse.y)
            movePanels(
                to: NSPoint(x: startOrigin.x + delta.x, y: startOrigin.y + delta.y)
            )
        case .leftMouseUp:
            customOrigin = panel.frame.origin
            dragStartPanelOrigin = nil
            dragStartMouseLocation = nil
        default:
            break
        }
    }

    private func renderSubtitles() {
        hostingController.rootView = SubtitleView(
            snapshot: snapshot,
            fontSize: fontSize,
            isEditingPosition: isEditingPosition
        )
    }

    private func renderControls() {
        controlHostingView.rootView = RecordingControlView(
            state: translationState,
            onToggleRecording: onToggleRecording
        )
    }

    private func relayoutIfNeeded(forceDefault: Bool = false) {
        let requestedOrigin = forceDefault ? nil : customOrigin
        guard let screen = targetScreen(containing: requestedOrigin) else { return }
        apply(layout(in: screen, requestedOrigin: requestedOrigin))
    }

    private func movePanels(to requestedOrigin: CGPoint) {
        guard let screen = targetScreen(
            bestMatchingSubtitleOrigin: requestedOrigin
        ) else {
            return
        }
        let layout = layout(in: screen, requestedOrigin: requestedOrigin)
        apply(layout)
        customOrigin = layout.subtitleFrame.origin
    }

    private func targetScreen(containing origin: CGPoint?) -> NSScreen? {
        let screens = NSScreen.screens
        guard !screens.isEmpty else { return nil }

        let fallbackScreen = NSScreen.main ?? screens.first
        guard let origin else { return fallbackScreen }

        let fallbackIndex = fallbackScreen.flatMap { fallback in
            screens.firstIndex { $0 === fallback }
        }
        guard let index = SubtitleWindowGeometry.screenIndex(
            containing: origin,
            in: screens.map(\.frame),
            fallbackIndex: fallbackIndex
        ) else {
            return fallbackScreen
        }
        return screens[index]
    }

    private func targetScreen(
        bestMatchingSubtitleOrigin origin: CGPoint
    ) -> NSScreen? {
        let screens = NSScreen.screens
        guard !screens.isEmpty else { return nil }

        let fallbackScreen = panel.screen ?? NSScreen.main ?? screens.first
        let fallbackIndex = fallbackScreen.flatMap { fallback in
            screens.firstIndex { $0 === fallback }
        }
        guard let index = SubtitleWindowGeometry.screenIndex(
            bestMatching: CGRect(origin: origin, size: panel.frame.size),
            in: screens.map(\.frame),
            fallbackIndex: fallbackIndex
        ) else {
            return fallbackScreen
        }
        return screens[index]
    }

    private func layout(
        in screen: NSScreen,
        requestedOrigin: CGPoint?
    ) -> SubtitleWindowLayout {
        let visibleFrame = screen.visibleFrame
        let subtitleSize = Self.subtitleSize(
            hostingController: hostingController,
            in: visibleFrame
        )
        let origin = requestedOrigin ?? SubtitleWindowGeometry.defaultOrigin(
            in: visibleFrame,
            subtitleSize: subtitleSize
        )
        return SubtitleWindowGeometry.layout(
            subtitleOrigin: origin,
            subtitleSize: subtitleSize,
            in: visibleFrame
        )
    }

    private func apply(_ layout: SubtitleWindowLayout) {
        if panel.frame != layout.subtitleFrame {
            panel.setFrame(layout.subtitleFrame, display: true)
        }
        guard SubtitleWindowGeometry.showsRecordingControl else {
            controlPanel.orderOut(nil)
            return
        }
        if controlPanel.frame != layout.controlFrame {
            controlPanel.setFrame(layout.controlFrame, display: true)
        }
    }

    private static func initialLayout(
        hostingController: NSHostingController<SubtitleView>,
        screen: NSScreen?
    ) -> SubtitleWindowLayout {
        let visibleFrame = screen?.visibleFrame ?? .zero
        let subtitleSize = subtitleSize(
            hostingController: hostingController,
            in: visibleFrame
        )
        let origin = SubtitleWindowGeometry.defaultOrigin(
            in: visibleFrame,
            subtitleSize: subtitleSize
        )
        return SubtitleWindowGeometry.layout(
            subtitleOrigin: origin,
            subtitleSize: subtitleSize,
            in: visibleFrame
        )
    }

    private static func subtitleSize(
        hostingController: NSHostingController<SubtitleView>,
        in visibleFrame: CGRect
    ) -> CGSize {
        let width = SubtitleWindowGeometry.subtitleWidth(in: visibleFrame)
        let measuredSize = hostingController.sizeThatFits(
            in: NSSize(
                width: width,
                height: CGFloat.greatestFiniteMagnitude
            )
        )
        let height = SubtitleWindowGeometry.subtitleHeight(
            measuredContentHeight: measuredSize.height,
            in: visibleFrame
        )
        return CGSize(width: width, height: height)
    }
}
