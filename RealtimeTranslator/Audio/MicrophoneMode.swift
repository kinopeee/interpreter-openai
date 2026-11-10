import AVFoundation

@MainActor
protocol MicrophoneModeProviding {
    var prefersVoiceIsolation: Bool { get }
    func showSystemMicrophoneModes()
}

@MainActor
struct SystemMicrophoneModeProvider: MicrophoneModeProviding {
    var prefersVoiceIsolation: Bool {
        AVCaptureDevice.preferredMicrophoneMode == .voiceIsolation
    }

    func showSystemMicrophoneModes() {
        AVCaptureDevice.showSystemUserInterface(.microphoneModes)
    }
}

enum MicrophoneModeHintPolicy {
    static func shouldShowHint(
        state: TranslationState,
        statusBanner: String?,
        listeningBanner: String,
        prefersVoiceIsolation: Bool
    ) -> Bool {
        prefersVoiceIsolation && state == .listening && statusBanner == listeningBanner
    }
}
