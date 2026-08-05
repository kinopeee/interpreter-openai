import SwiftUI

enum SubtitleTextLayout {
    static let currentLineLimit = 2
    static let truncationMode: Text.TruncationMode = .head
}

enum SubtitleVisualStyle {
    static let sourceTextOpacity = 0.7
    static let visibleTranslatedTextOpacity = 1.0

    static func translatedTextOpacity(for subtitle: LiveSubtitle) -> Double {
        subtitle.translatedText.isEmpty ? 0 : visibleTranslatedTextOpacity
    }
}

struct SubtitleView: View {
    let snapshot: SubtitleSnapshot
    let fontSize: Double
    let isEditingPosition: Bool

    var body: some View {
        // Banner is last so idle prompts sit under the reserved current slot.
        VStack(alignment: .leading, spacing: 8) {
            currentSlot

            if let banner = trimmedStatusBanner {
                statusBanner(banner)
            }
        }
        .padding(.horizontal, 20)
        .padding(.top, 8)
        .padding(.bottom, 12)
        .frame(maxWidth: 1200, alignment: .leading)
        .overlay {
            if isEditingPosition {
                RoundedRectangle(cornerRadius: 16, style: .continuous)
                    .strokeBorder(
                        Color.white.opacity(0.72),
                        style: StrokeStyle(lineWidth: 1.5, dash: [8, 6])
                    )
                    .background(
                        RoundedRectangle(cornerRadius: 16, style: .continuous)
                            .fill(Color.black.opacity(0.14))
                    )
            }
        }
        .multilineTextAlignment(.leading)
    }

    private var trimmedStatusBanner: String? {
        let banner = snapshot.statusBanner?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        return banner.isEmpty ? nil : banner
    }

    private func statusBanner(_ banner: String) -> some View {
        HStack(spacing: 8) {
            ProgressView()
                .controlSize(.small)
            Text(banner)
                .font(.system(size: max(14, fontSize * 0.45), weight: .semibold))
                .foregroundStyle(Color.yellow.opacity(0.95))
                .lineLimit(1)
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 7)
        .background(
            Capsule(style: .continuous)
                .fill(Color.black.opacity(0.46))
        )
        .shadow(color: .black.opacity(0.7), radius: 5, y: 2)
    }

    private var currentSlot: some View {
        let showListeningPlaceholder = snapshot.current.isEmpty
            && (snapshot.statusBanner == nil || snapshot.statusBanner?.isEmpty == true)

        return subtitleBlock(snapshot.current)
            .opacity(snapshot.current.isEmpty ? 0 : 1)
            .accessibilityHidden(snapshot.current.isEmpty)
            .overlay(alignment: .leading) {
                if showListeningPlaceholder {
                    Text("録音中…")
                        .font(.system(size: max(14, fontSize * 0.45), weight: .medium))
                        .foregroundStyle(Color.white.opacity(0.82))
                        .shadow(color: .black, radius: 3, y: 1)
                        .padding(.horizontal, 18)
                }
            }
    }

    @ViewBuilder
    private func subtitleBlock(_ subtitle: LiveSubtitle) -> some View {
        let clippedSource = SubtitleTailClipper.clip(subtitle.sourceText)
        let clippedTranslation = SubtitleTailClipper.clip(subtitle.translatedText)
        let sourceText = clippedSource.isEmpty ? " " : clippedSource
        let translatedText = clippedTranslation.isEmpty ? " " : clippedTranslation

        VStack(alignment: .leading, spacing: 4) {
            Text(sourceText)
                .font(
                    .system(
                        size: fontSize * 0.85,
                        weight: .medium
                    )
                )
                .foregroundStyle(Color.white.opacity(SubtitleVisualStyle.sourceTextOpacity))
                .lineLimit(SubtitleTextLayout.currentLineLimit, reservesSpace: true)
                .truncationMode(SubtitleTextLayout.truncationMode)
                .frame(maxWidth: .infinity, alignment: .leading)
                .subtitleHalo()
                .opacity(subtitle.sourceText.isEmpty ? 0 : 1)
                .accessibilityHidden(subtitle.sourceText.isEmpty)

            Text(translatedText)
                .font(.system(size: fontSize, weight: .semibold))
                .foregroundStyle(.white)
                .lineLimit(SubtitleTextLayout.currentLineLimit, reservesSpace: true)
                .truncationMode(SubtitleTextLayout.truncationMode)
                .frame(maxWidth: .infinity, alignment: .leading)
                .subtitleHalo()
                .opacity(SubtitleVisualStyle.translatedTextOpacity(for: subtitle))
                .accessibilityHidden(subtitle.translatedText.isEmpty)
        }
        .padding(.horizontal, 18)
        .padding(.vertical, 8)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(
            RoundedRectangle(cornerRadius: 12, style: .continuous)
                .fill(Color.black.opacity(0.30))
        )
        .shadow(color: .black.opacity(0.58), radius: 8, y: 3)
    }
}

private extension View {
    func subtitleHalo() -> some View {
        self
            .shadow(color: .black.opacity(0.98), radius: 2, x: 1, y: 1)
            .shadow(color: .black.opacity(0.9), radius: 5)
    }
}

struct RecordingControlView: View {
    let state: TranslationState
    let onToggleRecording: () -> Void

    var body: some View {
        Button(action: onToggleRecording) {
            Label(buttonTitle, systemImage: buttonIcon)
                .font(.system(size: 14, weight: .semibold))
                .frame(minWidth: 112)
        }
        .buttonStyle(.borderedProminent)
        .controlSize(.large)
        .tint(isRecording ? .red : .green)
        .disabled(state == .closing)
        .accessibilityLabel(buttonTitle)
    }

    private var isRecording: Bool {
        switch state {
        case .connecting, .listening, .reconnecting, .closing:
            return true
        case .idle, .error:
            return false
        }
    }

    private var buttonTitle: String {
        isRecording ? "録音終了" : "録音開始"
    }

    private var buttonIcon: String {
        isRecording ? "stop.fill" : "mic.fill"
    }
}
