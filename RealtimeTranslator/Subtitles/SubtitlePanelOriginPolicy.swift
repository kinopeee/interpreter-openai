enum SubtitlePanelOriginPolicy {
    /// 編集 OFF へ遷移し、かつミラーリング中でないときだけ位置を永続化する。
    static func shouldPersistOrigin(isEditingPosition: Bool, isMirroringActive: Bool) -> Bool {
        !isEditingPosition && !isMirroringActive
    }
}
