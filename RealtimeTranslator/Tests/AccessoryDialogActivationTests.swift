import AppKit
import XCTest
@testable import RealtimeTranslator

final class AccessoryDialogActivationTests: XCTestCase {
    func testAccessoryPolicyPromotesToRegularAndRestores() {
        // Given: メニューバー常駐の accessory アプリ
        let activation = AccessoryDialogActivation.begin(currentPolicy: .accessory)

        // When: 保存パネルやアラートを出す
        // Then: 表示中だけ regular にし、閉じたら accessory へ戻す
        XCTAssertEqual(activation.beginPolicy, .regular)
        XCTAssertEqual(activation.endPolicy, .accessory)
        XCTAssertTrue(activation.needsRegularPolicy)
    }

    func testRegularPolicyDoesNotChange() {
        // Given: すでに regular で前面に出ている
        let activation = AccessoryDialogActivation.begin(currentPolicy: .regular)

        // When: 同じ活性化を重ねる
        // Then: Dock アイコンの出し入れをせず、policy は触らない
        XCTAssertNil(activation.beginPolicy)
        XCTAssertNil(activation.endPolicy)
        XCTAssertFalse(activation.needsRegularPolicy)
    }

    func testProhibitedPolicyAlsoPromotesToRegular() {
        // Given: 一時的に prohibited になっている
        let activation = AccessoryDialogActivation.begin(currentPolicy: .prohibited)

        // When: ダイアログを前面へ出す
        // Then: regular へ上げ、終了時は prohibited へ戻す
        XCTAssertEqual(activation.beginPolicy, .regular)
        XCTAssertEqual(activation.endPolicy, .prohibited)
    }

    func testSessionRestoresAccessoryAfterSinglePresentation() {
        // Given: accessory から保存パネルを1枚だけ開く
        var session = AccessoryDialogSession()
        let activation = session.begin(currentPolicy: .accessory)

        // When / Then: 閉じたら accessory へ戻す
        XCTAssertEqual(activation.beginPolicy, .regular)
        XCTAssertEqual(session.end(), .accessory)
        XCTAssertEqual(session.depth, 0)
    }

    func testNestedSessionsRestoreOnlyWhenLastCloses() {
        // Given: accessory のまま保存パネルを2枚重ねて開ける
        var session = AccessoryDialogSession()

        // When: 1枚目で regular へ上げ、2枚目はすでに regular
        let first = session.begin(currentPolicy: .accessory)
        XCTAssertEqual(first.beginPolicy, .regular)
        let second = session.begin(currentPolicy: .regular)
        XCTAssertNil(second.beginPolicy)

        // Then: 先に閉じたパネルでは戻さず、最後に閉じたとき accessory へ戻す
        XCTAssertNil(session.end())
        XCTAssertEqual(session.end(), .accessory)
        XCTAssertEqual(session.depth, 0)
    }

    func testNestedSessionsKeepOriginalRestorePolicy() {
        // Given: 重ね開きの2枚目が currentPolicy を accessory と誤って渡す
        var session = AccessoryDialogSession()
        _ = session.begin(currentPolicy: .accessory)
        _ = session.begin(currentPolicy: .accessory)

        // When / Then: 最初に記録した restore を使い、depth 0 で1回だけ戻す
        XCTAssertNil(session.end())
        XCTAssertEqual(session.end(), .accessory)
        XCTAssertNil(session.end())
    }

    func testRegularSessionDoesNotRestoreOnNestedClose() {
        // Given: すでに regular
        var session = AccessoryDialogSession()
        _ = session.begin(currentPolicy: .regular)
        _ = session.begin(currentPolicy: .regular)

        // When / Then: どちらを閉じても policy は触らない
        XCTAssertNil(session.end())
        XCTAssertNil(session.end())
    }

    func testFollowUpAlertNestsBeforeOuterSessionEnds() {
        // Given: 保存パネルの completion でエラーアラートを出す
        var session = AccessoryDialogSession()
        _ = session.begin(currentPolicy: .accessory)

        // When: applyEnd より先に follow-up の begin が走る
        _ = session.begin(currentPolicy: .regular)
        XCTAssertNil(session.end())

        // Then: アラートを閉じてから accessory へ戻す
        XCTAssertEqual(session.end(), .accessory)
        XCTAssertEqual(session.depth, 0)
    }

    func testSettingsHoldThenAlertRestoresOnlyAfterSettingsClose() {
        // Given: 設定画面が regular を保持したまま同意アラートを重ねる
        var session = AccessoryDialogSession()
        _ = session.begin(currentPolicy: .accessory)

        // When: アラートを閉じてから設定を閉じる
        _ = session.begin(currentPolicy: .regular)
        XCTAssertNil(session.end())

        // Then: 設定が開いている間は戻さず、閉じたとき accessory へ戻す
        XCTAssertEqual(session.end(), .accessory)
        XCTAssertEqual(session.depth, 0)
    }
}
