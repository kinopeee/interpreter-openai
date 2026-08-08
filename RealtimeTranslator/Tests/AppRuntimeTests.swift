import XCTest
@testable import RealtimeTranslator

@MainActor
final class AppRuntimeTests: XCTestCase {
    func testDetectsXCTestConfigurationPath() {
        // Given: XCTestが設定ファイルパスを注入した実行環境
        let environment = ["XCTestConfigurationFilePath": "/tmp/test.xctestconfiguration"]

        // When: テスト実行環境かを判定する
        let isRunningXCTest = AppRuntimeEnvironment.isRunningXCTest(
            environment: environment
        )

        // Then: XCTestとして判定する
        XCTAssertTrue(isRunningXCTest)
    }

    func testDetectsXCTestBundlePath() {
        // Given: XCTestがテストバンドルパスだけを注入した実行環境
        let environment = ["XCTestBundlePath": "/tmp/RealtimeTranslatorTests.xctest"]

        // When: テスト実行環境かを判定する
        let isRunningXCTest = AppRuntimeEnvironment.isRunningXCTest(
            environment: environment
        )

        // Then: XCTestとして判定する
        XCTAssertTrue(isRunningXCTest)
    }

    func testDoesNotDetectRegularAppEnvironmentAsXCTest() {
        // Given: XCTest固有キーを含まない通常起動の実行環境
        let environment = ["PATH": "/usr/bin:/bin"]

        // When: テスト実行環境かを判定する
        let isRunningXCTest = AppRuntimeEnvironment.isRunningXCTest(
            environment: environment
        )

        // Then: 通常のアプリ起動として判定する
        XCTAssertFalse(isRunningXCTest)
    }

    func testHostedXCTestDoesNotBootstrapAppRuntime() {
        // Given: アプリ実行ファイルをTEST_HOSTとして利用するXCTest
        XCTAssertTrue(AppRuntimeEnvironment.isRunningXCTest())

        // When: 通常起動と同じランタイム開始経路を呼ぶ
        AppRuntime.start()

        // Then: Coordinatorを生成せず、UIやホットキーを起動しない
        XCTAssertNil(AppRuntime.coordinator)
    }

    func testTerminationGateStartsUnpreparedAndCanBeMarked() {
        // Given: テスト用に終了ゲートを初期化
        AppTerminationGate.resetForTests()

        // When: まだ prepare していない
        // Then: terminateLater 側へ進める
        XCTAssertFalse(AppTerminationGate.isPrepared)

        // When: セッション停止後に prepared を立てる
        AppTerminationGate.markPrepared()

        // Then: 二重 stop せず terminateNow できる
        XCTAssertTrue(AppTerminationGate.isPrepared)

        AppTerminationGate.resetForTests()
    }
}
