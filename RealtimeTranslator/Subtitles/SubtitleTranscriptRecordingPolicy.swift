enum SubtitleTranscriptRecordingPolicy {
    static func shouldRecord(
        subtitle: LiveSubtitle,
        recordSubtitles: Bool,
        state: TranslationState
    ) -> Bool {
        recordSubtitles
            && subtitle.state == .finalized
            && !subtitle.isEmpty
            && state != .idle
    }
}
