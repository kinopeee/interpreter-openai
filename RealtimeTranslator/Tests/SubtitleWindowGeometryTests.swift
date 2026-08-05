import XCTest
@testable import RealtimeTranslator

final class SubtitleWindowGeometryTests: XCTestCase {
    func testUsesMeasuredContentHeightWhenItFits() {
        // Given: 画面内に収まる字幕内容高
        let visibleFrame = CGRect(x: -300, y: -100, width: 1_000, height: 700)

        // When: 字幕パネル高を算出する
        let height = SubtitleWindowGeometry.subtitleHeight(
            measuredContentHeight: 321.2,
            in: visibleFrame
        )

        // Then: ピクセル境界へ切り上げた内容高をそのまま採用する
        XCTAssertEqual(height, 322)
    }

    func testCapsContentHeightToVisibleFrame() {
        // Given: 画面の利用可能高を超える字幕内容高
        let visibleFrame = CGRect(x: -300, y: -100, width: 1_000, height: 700)
        let reserved = SubtitleWindowGeometry.showsRecordingControl
            ? SubtitleWindowGeometry.controlSize.height
                + SubtitleWindowGeometry.controlSpacing
            : 0

        // When: 字幕パネル高を算出する
        let height = SubtitleWindowGeometry.subtitleHeight(
            measuredContentHeight: 900,
            in: visibleFrame
        )

        // Then: 操作パネル予約分を除いた高さへ制限する
        XCTAssertEqual(height, 700 - reserved)
    }

    func testSelectsNegativeCoordinateSecondaryScreen() {
        // Given: 原点が負座標にある副画面と、その画面内の保存位置
        let screenFrames = [
            CGRect(x: 200, y: 0, width: 1_400, height: 900),
            CGRect(x: -1_720, y: -120, width: 1_920, height: 1_080)
        ]
        let savedOrigin = CGPoint(x: -1_200, y: 80)

        // When: 保存位置を含む画面を選択する
        let index = SubtitleWindowGeometry.screenIndex(
            containing: savedOrigin,
            in: screenFrames,
            fallbackIndex: 0
        )

        // Then: 負座標の副画面を選択する
        XCTAssertEqual(index, 1)
    }

    func testFallsBackWhenSavedOriginIsOutsideAllScreens() {
        // Given: 接続中のどの画面にも含まれない保存位置
        let screenFrames = [
            CGRect(x: 100, y: 100, width: 800, height: 600),
            CGRect(x: 900, y: 100, width: 1_000, height: 700)
        ]
        let savedOrigin = CGPoint(x: -2_000, y: -1_000)

        // When: fallbackを指定して画面を選択する
        let index = SubtitleWindowGeometry.screenIndex(
            containing: savedOrigin,
            in: screenFrames,
            fallbackIndex: 1
        )

        // Then: 指定したfallback画面を選択する
        XCTAssertEqual(index, 1)
    }

    func testSelectsAdjacentScreenBeforePanelOriginCrossesBoundary() {
        // Given: 原点は左画面内だが、面積の大半が右画面へ移った字幕パネル
        let screenFrames = [
            CGRect(x: 0, y: 0, width: 1_000, height: 800),
            CGRect(x: 1_000, y: 0, width: 1_000, height: 800)
        ]
        let proposedFrame = CGRect(x: 800, y: 200, width: 600, height: 200)

        // When: パネルとの重なり面積からドラッグ先画面を選択する
        let index = SubtitleWindowGeometry.screenIndex(
            bestMatching: proposedFrame,
            in: screenFrames,
            fallbackIndex: 0
        )

        // Then: 原点の境界通過を待たず、大半が属する右画面を選択する
        XCTAssertEqual(index, 1)
    }

    func testClampsBothPanelsAtLeftEdge() {
        // Given: 字幕パネルが画面左端から50ptはみ出す配置
        let visibleFrame = testVisibleFrame

        // When: 字幕レイアウトを算出する
        let layout = SubtitleWindowGeometry.layout(
            subtitleOrigin: CGPoint(x: -350, y: 0),
            subtitleSize: testSubtitleSize,
            in: visibleFrame
        )

        // Then: 字幕を左端へ寄せる
        XCTAssertEqual(layout.subtitleFrame.minX, visibleFrame.minX)
        XCTAssertEqual(layout.combinedFrame.minX, visibleFrame.minX)
        assertControlLayout(in: layout)
    }

    func testClampsBothPanelsAtRightEdge() {
        // Given: 字幕パネルが画面右端から50ptはみ出す配置
        let visibleFrame = testVisibleFrame

        // When: 字幕レイアウトを算出する
        let layout = SubtitleWindowGeometry.layout(
            subtitleOrigin: CGPoint(x: 150, y: 0),
            subtitleSize: testSubtitleSize,
            in: visibleFrame
        )

        // Then: 字幕を右端へ寄せる
        XCTAssertEqual(layout.subtitleFrame.maxX, visibleFrame.maxX)
        XCTAssertEqual(layout.combinedFrame.maxX, visibleFrame.maxX)
        assertControlLayout(in: layout)
    }

    func testClampsAtBottomEdge() {
        // Given: 字幕（または操作パネル）が画面下端からはみ出す配置
        let visibleFrame = testVisibleFrame

        // When: 字幕レイアウトを算出する
        let layout = SubtitleWindowGeometry.layout(
            subtitleOrigin: CGPoint(x: -100, y: -240),
            subtitleSize: testSubtitleSize,
            in: visibleFrame
        )

        // Then: 下端へ寄せ、画面内に収める
        if SubtitleWindowGeometry.showsRecordingControl {
            XCTAssertEqual(layout.controlFrame.minY, visibleFrame.minY)
        } else {
            XCTAssertEqual(layout.subtitleFrame.minY, visibleFrame.minY)
        }
        XCTAssertEqual(layout.combinedFrame.minY, visibleFrame.minY)
        assertControlLayout(in: layout)
    }

    func testClampsBothPanelsAtTopEdge() {
        // Given: 字幕パネルが画面上端からはみ出す配置
        let visibleFrame = testVisibleFrame

        // When: 字幕レイアウトを算出する
        let layout = SubtitleWindowGeometry.layout(
            subtitleOrigin: CGPoint(x: -100, y: 400),
            subtitleSize: testSubtitleSize,
            in: visibleFrame
        )

        // Then: 字幕パネルを上端へ寄せる
        XCTAssertEqual(layout.subtitleFrame.maxY, visibleFrame.maxY)
        XCTAssertEqual(layout.combinedFrame.maxY, visibleFrame.maxY)
        assertControlLayout(in: layout)
    }

    func testSubtitleOriginDoesNotMoveAsSubtitleGrows() {
        // Given: 同じ下端位置にある短い字幕と長い字幕
        let visibleFrame = CGRect(x: 0, y: 0, width: 1_000, height: 800)
        let subtitleOrigin = CGPoint(x: 200, y: 104)

        // When: 内容高だけが変化したレイアウトを算出する
        let shortLayout = SubtitleWindowGeometry.layout(
            subtitleOrigin: subtitleOrigin,
            subtitleSize: CGSize(width: 600, height: 120),
            in: visibleFrame
        )
        let tallLayout = SubtitleWindowGeometry.layout(
            subtitleOrigin: subtitleOrigin,
            subtitleSize: CGSize(width: 600, height: 500),
            in: visibleFrame
        )

        // Then: 字幕下端（origin）は同じ位置を保つ
        XCTAssertEqual(tallLayout.subtitleFrame.origin, shortLayout.subtitleFrame.origin)
        if SubtitleWindowGeometry.showsRecordingControl {
            XCTAssertEqual(tallLayout.controlFrame, shortLayout.controlFrame)
        }
        assertControlLayout(in: tallLayout)
    }

    private var testVisibleFrame: CGRect {
        CGRect(x: -300, y: -200, width: 1_000, height: 800)
    }

    private var testSubtitleSize: CGSize {
        CGSize(width: 600, height: 200)
    }

    private func assertControlLayout(
        in layout: SubtitleWindowLayout,
        file: StaticString = #filePath,
        line: UInt = #line
    ) {
        guard SubtitleWindowGeometry.showsRecordingControl else {
            XCTAssertEqual(layout.controlFrame.size, .zero, file: file, line: line)
            XCTAssertEqual(
                layout.combinedFrame,
                layout.subtitleFrame,
                file: file,
                line: line
            )
            return
        }

        XCTAssertEqual(
            layout.subtitleFrame.minY - layout.controlFrame.maxY,
            SubtitleWindowGeometry.controlSpacing,
            file: file,
            line: line
        )
        XCTAssertEqual(
            layout.controlFrame.midX,
            layout.subtitleFrame.midX,
            file: file,
            line: line
        )
    }
}
