import AppKit
import SwiftUI
import XCTest
@testable import RealtimeTranslator

@MainActor
final class SubtitlePresentationTests: XCTestCase {
    func testMetadataOnlyChangesKeepSamePresentation() {
        // Given: 表示文字が同じで、更新時刻・状態・freshnessだけが異なる字幕
        let first = snapshot(
            current: subtitle(
                source: "同じ原文",
                translation: "Same translation",
                isTranslationCurrent: false,
                state: .live,
                updatedAt: .distantPast
            )
        )
        let second = snapshot(
            current: subtitle(
                source: "同じ原文",
                translation: "Same translation",
                isTranslationCurrent: true,
                state: .finalized,
                updatedAt: Date()
            )
        )

        // When: 描画に必要な表示状態へ変換する
        let firstPresentation = first.presentation
        let secondPresentation = second.presentation

        // Then: 意味メタデータだけでは再描画対象にしない
        XCTAssertEqual(firstPresentation, secondPresentation)
    }

    func testSourceTextChangeChangesPresentation() {
        // Given: 訳文は同じで原文だけが更新された字幕
        let first = snapshot(
            current: subtitle(source: "短い", translation: "Translation")
        )
        let second = snapshot(
            current: subtitle(source: "短い原文", translation: "Translation")
        )

        // When: 表示状態を比較する
        // Then: 読者に見える原文変更を再描画対象にする
        XCTAssertNotEqual(first.presentation, second.presentation)
    }

    func testTranslationTextChangeChangesPresentation() {
        // Given: 原文は同じで訳文だけが更新された字幕
        let first = snapshot(
            current: subtitle(source: "原文", translation: "Old translation")
        )
        let second = snapshot(
            current: subtitle(source: "原文", translation: "New translation")
        )

        // When: 表示状態を比較する
        // Then: 読者に見える訳文変更を再描画対象にする
        XCTAssertNotEqual(first.presentation, second.presentation)
    }

    func testTranslationFreshnessDoesNotChangeVisibleOpacity() {
        // Given: 同じ非空訳文を持つ更新待ち字幕と最新字幕
        let stale = subtitle(
            source: "原文",
            translation: "Readable translation",
            isTranslationCurrent: false
        )
        let current = subtitle(
            source: "原文",
            translation: "Readable translation",
            isTranslationCurrent: true
        )

        // When: 訳文の表示opacityを算出する
        let staleOpacity = SubtitleVisualStyle.translatedTextOpacity(for: stale)
        let currentOpacity = SubtitleVisualStyle.translatedTextOpacity(for: current)

        // Then: freshness更新で明滅させず、どちらも完全表示する
        XCTAssertEqual(staleOpacity, 1)
        XCTAssertEqual(currentOpacity, 1)
    }

    func testEmptyTranslationRemainsHidden() {
        // Given: 翻訳結果がまだ空の字幕
        let subtitle = subtitle(source: "原文", translation: "")

        // When: 訳文の表示opacityを算出する
        let opacity = SubtitleVisualStyle.translatedTextOpacity(for: subtitle)

        // Then: 空のプレースホルダー文字は表示しない
        XCTAssertEqual(opacity, 0)
    }

    func testTextLayoutUsesCompactLimitsAndKeepsSentenceEnd() {
        // Given: 行数を超える現在文を表示する字幕
        // When: 最大行数と省略位置の設定を確認する
        let currentLineLimit = SubtitleTextLayout.currentLineLimit
        let truncationMode = SubtitleTextLayout.truncationMode

        // Then: 行数を抑えつつ文頭を省略し、必要な文末を残す
        XCTAssertEqual(currentLineLimit, 2)
        XCTAssertEqual(truncationMode, .head)
    }

    func testLongTranslationHeightIsBoundedByLineLimit() {
        // Given: 1行の訳文と、同じ幅で何行にも折り返す長文訳
        let shortView = SubtitleView(
            snapshot: snapshot(
                current: subtitle(source: "", translation: "Short translation")
            ),
            fontSize: 32,
            isEditingPosition: false
        )
        let longText = Array(
            repeating: "This translation should remain fully readable.",
            count: 30
        ).joined(separator: " ")
        let longView = SubtitleView(
            snapshot: snapshot(
                current: subtitle(source: "", translation: longText)
            ),
            fontSize: 32,
            isEditingPosition: false
        )

        // When: 同じ600pt幅でSwiftUIの固有高を計測する
        let shortHeight = measuredHeight(of: shortView, width: 600)
        let longHeight = measuredHeight(of: longView, width: 600)

        // Then: 固定スロットにより長文でも短文と同じ高さに留まる
        XCTAssertEqual(longHeight, shortHeight, accuracy: 0.5)
    }

    func testEmptyCurrentDoesNotChangeMeasuredHeight() {
        // Given: 現在字幕ありと、現在字幕が空のビュー
        let withCurrent = SubtitleView(
            snapshot: snapshot(
                current: subtitle(source: "現在の原文", translation: "Current translation")
            ),
            fontSize: 32,
            isEditingPosition: false
        )
        let emptyCurrent = SubtitleView(
            snapshot: snapshot(current: .empty),
            fontSize: 32,
            isEditingPosition: false
        )

        // When: 同じ幅で固有高を計測する
        let withHeight = measuredHeight(of: withCurrent, width: 600)
        let emptyHeight = measuredHeight(of: emptyCurrent, width: 600)

        // Then: currentスロットは空でも高さを保ち、レイアウトが縮まない
        XCTAssertEqual(emptyHeight, withHeight, accuracy: 0.5)
    }

    func testIdleBannerSitsBelowReservedCurrentSlot() {
        // Given: 待機バナーだけの字幕と、同じバナーに現在字幕を足したビュー
        let idleOnly = SubtitleView(
            snapshot: snapshot(
                current: .empty,
                statusBanner: "待機中 — Control + Option + Space で録音開始"
            ),
            fontSize: 32,
            isEditingPosition: false
        )
        let withCurrent = SubtitleView(
            snapshot: snapshot(
                current: subtitle(source: "現在の原文", translation: "Current translation"),
                statusBanner: "待機中 — Control + Option + Space で録音開始"
            ),
            fontSize: 32,
            isEditingPosition: false
        )

        // When: 同じ幅で固有高を計測する
        let idleHeight = measuredHeight(of: idleOnly, width: 600)
        let withCurrentHeight = measuredHeight(of: withCurrent, width: 600)

        // Then: currentスロットは確保したまま、バナーはその下に付く
        XCTAssertEqual(idleHeight, withCurrentHeight, accuracy: 0.5)
    }

    func testStatusBannerPresenceIncreasesMeasuredHeight() {
        // Given: バナーなしの字幕と、同じ本文でバナーありの字幕
        let withoutBanner = SubtitleView(
            snapshot: snapshot(
                current: subtitle(source: "現在の原文", translation: "Current translation")
            ),
            fontSize: 32,
            isEditingPosition: false
        )
        let withBanner = SubtitleView(
            snapshot: snapshot(
                current: subtitle(source: "現在の原文", translation: "Current translation"),
                statusBanner: "モデルを準備中…"
            ),
            fontSize: 32,
            isEditingPosition: false
        )

        // When: 同じ幅で固有高を計測する
        let withoutHeight = measuredHeight(of: withoutBanner, width: 600)
        let withHeight = measuredHeight(of: withBanner, width: 600)

        // Then: バナーは表示時だけ高さを取り、非表示時は余分な隙間を空けない
        XCTAssertGreaterThan(withHeight, withoutHeight + 10)
    }

    func testControllerKeepsLongSubtitlePanelInsideScreen() throws {
        // Given: 字幕ウィンドウ作成前のパネル一覧と、画面高を超える長文字幕
        let existingPanels = Set(
            NSApp.windows
                .compactMap { $0 as? SubtitlePanel }
                .map(ObjectIdentifier.init)
        )
        let controller = SubtitleWindowController()
        defer { controller.tearDown() }
        let longText = Array(
            repeating: "画面外へ押し出されない長文字幕を表示します。",
            count: 100
        ).joined()

        // When: 長文字幕を描画してAppKitのレイアウト更新を処理する
        controller.update(
            snapshot: snapshot(
                current: subtitle(source: longText, translation: longText)
            ),
            fontSize: 32,
            translationState: .listening
        )
        RunLoop.main.run(until: Date().addingTimeInterval(0.05))
        let subtitlePanel = try XCTUnwrap(
            createdPanels(excluding: existingPanels).max {
                $0.frame.width < $1.frame.width
            }
        )
        let visibleFrame = try XCTUnwrap(
            subtitlePanel.screen?.visibleFrame
                ?? NSScreen.main?.visibleFrame
                ?? NSScreen.screens.first?.visibleFrame
        )

        // Then: SwiftUIの固有高に再拡大されず、画面内に収まる
        let reserved = SubtitleWindowGeometry.showsRecordingControl
            ? SubtitleWindowGeometry.controlSize.height
                + SubtitleWindowGeometry.controlSpacing
            : 0
        XCTAssertNil(subtitlePanel.contentViewController)
        XCTAssertLessThanOrEqual(
            subtitlePanel.frame.height,
            visibleFrame.height - reserved
        )
        XCTAssertTrue(visibleFrame.contains(subtitlePanel.frame))
    }

    func testControllerKeepsSubtitleOriginWhileContentGrows() throws {
        // Given: 短文字幕を表示した字幕パネル
        let existingPanels = Set(
            NSApp.windows
                .compactMap { $0 as? SubtitlePanel }
                .map(ObjectIdentifier.init)
        )
        let controller = SubtitleWindowController()
        defer { controller.tearDown() }
        controller.update(
            snapshot: snapshot(
                current: subtitle(source: "短い原文", translation: "Short translation")
            ),
            fontSize: 32,
            translationState: .listening
        )
        let subtitlePanel = try XCTUnwrap(
            createdPanels(excluding: existingPanels).max {
                $0.frame.width < $1.frame.width
            }
        )
        let shortOrigin = subtitlePanel.frame.origin
        let longerText = Array(
            repeating: "通常の発話で字幕行が増えても下端位置を固定します。",
            count: 8
        ).joined()

        // When: 画面内に収まる範囲で字幕の行数だけを増やす
        controller.update(
            snapshot: snapshot(
                current: subtitle(source: longerText, translation: longerText)
            ),
            fontSize: 32,
            translationState: .listening
        )
        RunLoop.main.run(until: Date().addingTimeInterval(0.05))

        // Then: 字幕パネルの下端（origin）は変化しない
        XCTAssertEqual(subtitlePanel.frame.origin.x, shortOrigin.x, accuracy: 0.5)
        XCTAssertEqual(subtitlePanel.frame.origin.y, shortOrigin.y, accuracy: 0.5)
    }

    func testControllerHidesRecordingControlWhenDisabled() throws {
        // Given: 録音ボタン非表示設定で字幕コントローラを起動する
        guard !SubtitleWindowGeometry.showsRecordingControl else {
            throw XCTSkip("録音ボタン表示中はこの検証をスキップ")
        }
        let existingPanels = Set(
            NSApp.windows
                .compactMap { $0 as? SubtitlePanel }
                .map(ObjectIdentifier.init)
        )
        let controller = SubtitleWindowController()
        defer { controller.tearDown() }
        controller.show()

        // When: 生成されたパネルの可視状態を確認する
        let panels = createdPanels(excluding: existingPanels)
        let narrowPanel = try XCTUnwrap(
            panels.min { $0.frame.width < $1.frame.width }
        )

        // Then: 操作パネルは非表示のまま
        XCTAssertFalse(narrowPanel.isVisible)
    }

    private func snapshot(
        current: LiveSubtitle,
        statusBanner: String? = nil
    ) -> SubtitleSnapshot {
        SubtitleSnapshot(
            current: current,
            statusBanner: statusBanner
        )
    }

    private func subtitle(
        source: String,
        translation: String,
        isTranslationCurrent: Bool = true,
        state: SubtitleBlockState = .live,
        updatedAt: Date = Date()
    ) -> LiveSubtitle {
        LiveSubtitle(
            sourceText: source,
            translatedText: translation,
            lastUpdatedAt: updatedAt,
            state: state,
            isTranslationCurrent: isTranslationCurrent,
            canFinalize: isTranslationCurrent
        )
    }

    private func measuredHeight(of view: SubtitleView, width: CGFloat) -> CGFloat {
        let hostingController = NSHostingController(rootView: view)
        return hostingController.sizeThatFits(
            in: NSSize(
                width: width,
                height: CGFloat.greatestFiniteMagnitude
            )
        ).height
    }

    private func createdPanels(
        excluding existingPanels: Set<ObjectIdentifier>
    ) -> [SubtitlePanel] {
        NSApp.windows
            .compactMap { $0 as? SubtitlePanel }
            .filter { !existingPanels.contains(ObjectIdentifier($0)) }
    }
}
