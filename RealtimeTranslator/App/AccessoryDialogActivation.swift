import AppKit

/// LSUIElement / accessory アプリが保存パネルやアラートを他アプリの背面へ出さないための活性化方針。
///
/// `NSSavePanel.begin` は呼び出し元が非アクティブのままだと Finder 等の背面に出る。
/// `NSApp.activate()` だけでは accessory アプリは前面化できないため、ダイアログ表示中だけ
/// `.regular` にし、閉じたら元の policy へ戻す。
struct AccessoryDialogActivation: Equatable, Sendable {
    let previousPolicy: NSApplication.ActivationPolicy

    var needsRegularPolicy: Bool {
        previousPolicy != .regular
    }

    /// 表示開始時に適用する policy。既に regular なら変更しない。
    var beginPolicy: NSApplication.ActivationPolicy? {
        needsRegularPolicy ? .regular : nil
    }

    /// 表示終了時に戻す policy。開始時に上げた場合だけ戻す。
    var endPolicy: NSApplication.ActivationPolicy? {
        needsRegularPolicy ? previousPolicy : nil
    }

    static func begin(currentPolicy: NSApplication.ActivationPolicy) -> Self {
        Self(previousPolicy: currentPolicy)
    }
}

/// accessory アプリ向けに保存パネル / アラートを前面へ出す。
@MainActor
enum AccessoryDialogPresenter {
    static func present(
        _ panel: NSSavePanel,
        application: NSApplication = .shared,
        completion: @escaping (NSApplication.ModalResponse) -> Void
    ) {
        let activation = AccessoryDialogActivation.begin(
            currentPolicy: application.activationPolicy()
        )
        applyBegin(activation, to: application)
        panel.level = .modalPanel
        panel.hidesOnDeactivate = false
        panel.begin { response in
            applyEnd(activation, to: application)
            completion(response)
        }
    }

    static func runModal(
        _ alert: NSAlert,
        application: NSApplication = .shared
    ) -> NSApplication.ModalResponse {
        let activation = AccessoryDialogActivation.begin(
            currentPolicy: application.activationPolicy()
        )
        applyBegin(activation, to: application)
        defer { applyEnd(activation, to: application) }
        return alert.runModal()
    }

    static func applyBegin(
        _ activation: AccessoryDialogActivation,
        to application: NSApplication
    ) {
        if let policy = activation.beginPolicy {
            application.setActivationPolicy(policy)
        }
        // accessory のままでは `activate()` が他アプリを下げない。既存の設定画面と同じ API を使う。
        application.activate(ignoringOtherApps: true)
    }

    static func applyEnd(
        _ activation: AccessoryDialogActivation,
        to application: NSApplication
    ) {
        if let policy = activation.endPolicy {
            application.setActivationPolicy(policy)
        }
    }
}
