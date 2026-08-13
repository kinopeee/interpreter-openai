import XCTest
@testable import RealtimeTranslator

/// テストは常に ja カタログを Current に載せる。本番は Bundle.main のみ。
@objc(JapaneseUserCopyTestObserver)
final class JapaneseUserCopyTestObserver: NSObject, XCTestObservation {
    override init() {
        super.init()
        XCTestObservationCenter.shared.addTestObserver(self)
        Self.installJapaneseCatalog()
    }

    func testBundleWillStart(_ testBundle: Bundle) {
        Self.installJapaneseCatalog()
    }

    static func installJapaneseCatalog() {
        do {
            let json = try SharedFixtures.uiCatalogJSON()
            UserCopyStore.install(try UserCopy.parse(json: json, locale: .ja))
        } catch {
            fatalError("ja UserCopy をテストへ載せられません")
        }
    }
}
