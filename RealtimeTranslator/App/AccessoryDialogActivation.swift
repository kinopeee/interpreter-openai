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

/// 重ねて開いた保存パネル / アラートの活性化を1本にまとめる。
///
/// `NSSavePanel.begin` は modeless なので、メニューから2枚目を開ける。
/// 呼び出しごとの snapshot だと、先に閉じたパネルが `.accessory` へ戻して
/// 残りのダイアログが背面へ落ちる。depth が 0 になるまで戻さない。
struct AccessoryDialogSession: Equatable, Sendable {
    private(set) var depth = 0
    private(set) var restorePolicy: NSApplication.ActivationPolicy?

    /// 最初の表示だけ policy 変更を記録する。重ね開きでは activate だけ行う。
    mutating func begin(
        currentPolicy: NSApplication.ActivationPolicy
    ) -> AccessoryDialogActivation {
        let activation = AccessoryDialogActivation.begin(currentPolicy: currentPolicy)
        if depth == 0 {
            restorePolicy = activation.endPolicy
        }
        depth += 1
        return activation
    }

    /// 最後の表示が閉じたときだけ、開始前の policy を返す。
    mutating func end() -> NSApplication.ActivationPolicy? {
        guard depth > 0 else {
            return nil
        }
        depth -= 1
        guard depth == 0 else {
            return nil
        }
        let policy = restorePolicy
        restorePolicy = nil
        return policy
    }
}

/// accessory アプリ向けに保存パネル / アラートを前面へ出す。
@MainActor
enum AccessoryDialogPresenter {
    private static var session = AccessoryDialogSession()

    static func present(
        _ panel: NSSavePanel,
        application: NSApplication = .shared,
        completion: @escaping (NSApplication.ModalResponse) -> Void
    ) {
        applyBegin(session.begin(currentPolicy: application.activationPolicy()), to: application)
        panel.level = .modalPanel
        panel.hidesOnDeactivate = false
        panel.begin { response in
            applyEnd(to: application)
            completion(response)
        }
    }

    static func runModal(
        _ alert: NSAlert,
        application: NSApplication = .shared
    ) -> NSApplication.ModalResponse {
        applyBegin(session.begin(currentPolicy: application.activationPolicy()), to: application)
        defer { applyEnd(to: application) }
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

    static func applyEnd(to application: NSApplication) {
        if let policy = session.end() {
            application.setActivationPolicy(policy)
        }
    }
}
