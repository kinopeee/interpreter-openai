import SwiftUI

enum SubtitleTextLayout {
    static let currentLineLimit = 2
    static let truncationMode: Text.TruncationMode = .head
}

enum SubtitleVisualStyle {
    static let sourceTextOpacity = 0.7
    static let visibleTranslatedTextOpacity = 1.0
    /// 訳文が未確定である間だけ末尾へ添える記号。
    static let pendingMarker = "…"
    static let pendingMarkerOpacity = 0.55
    static let translationFadeDuration = 0.12

    static func translatedTextOpacity(for subtitle: LiveSubtitle) -> Double {
        // 空白だけの訳文は「未着」と同じ扱いにし、マーカー行だけを出す。
        if hasVisibleTranslation(subtitle.translatedText) {
            return visibleTranslatedTextOpacity
        }
        return showsTranslationPendingMarker(for: subtitle)
            ? visibleTranslatedTextOpacity
            : 0
    }

    /// 確定前だけマーカーを出す。確定は finalizePair / forceFinalize 経由でしか起きないため、
    /// セグメント途中で切り替わらず明滅しない。
    static func showsTranslationPendingMarker(for subtitle: LiveSubtitle) -> Bool {
        guard subtitle.state == .live, !subtitle.isEmpty else { return false }
        // 文末記号で終わる訳文へ足すと「are.…」のような誤記に見えるため出さない。
        // この条件は確定直前にしか成立せず、確定後の見た目と一致する。
        return !endsWithTerminalPunctuation(subtitle.translatedText)
    }

    /// 空白・改行だけの訳文は表示本文なしとみなす。
    static func hasVisibleTranslation(_ text: String) -> Bool {
        !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
    }

    /// 固定値だとフォントサイズ設定（18〜48pt）で行間比率が変わるため、サイズ比例にする。
    static func lineSpacing(forFontSize fontSize: Double) -> Double {
        fontSize / 10
    }

    /// Aggregator の確定句読点に加え、表示用マーカー抑制のため末尾の `…` も見る。
    /// （`……` の誤記を避ける。Aggregator の確定条件自体は変えない。）
    private static func endsWithTerminalPunctuation(_ text: String) -> Bool {
        guard let last = text.trimmingCharacters(in: .whitespacesAndNewlines).last else {
            return false
        }
        return "。．.!？?！…".contains(last)
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
                    Text(UiCopy.text("overlay.recording"))
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
        let sourceText = clippedSource.isEmpty ? " " : clippedSource
        let lineSpacing = SubtitleVisualStyle.lineSpacing(forFontSize: fontSize)
        let translatedOpacity = SubtitleVisualStyle.translatedTextOpacity(for: subtitle)

        VStack(alignment: .leading, spacing: 4) {
            reservedTextSlot(
                Text(sourceText),
                font: .system(size: fontSize * 0.85, weight: .medium),
                lineSpacing: lineSpacing
            )
            .foregroundStyle(Color.white.opacity(SubtitleVisualStyle.sourceTextOpacity))
            .subtitleHalo()
            .opacity(subtitle.sourceText.isEmpty ? 0 : 1)
            .accessibilityHidden(subtitle.sourceText.isEmpty)

            reservedTextSlot(
                translatedText(for: subtitle),
                font: .system(size: fontSize, weight: .semibold),
                lineSpacing: lineSpacing
            )
            .foregroundStyle(.white)
            .subtitleHalo()
            .opacity(translatedOpacity)
            .animation(
                .easeOut(duration: SubtitleVisualStyle.translationFadeDuration),
                value: translatedOpacity
            )
            .accessibilityHidden(translatedOpacity == 0)
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

    /// 実際の行数で高さが変わらないよう、行間込みの最大行数分を常に確保する。
    /// `lineLimit(_:reservesSpace:)` の予約高は行間を含まないため、
    /// 非表示のsizerで高さを決めて本文をその上へ重ねる。
    private func reservedTextSlot(
        _ text: Text,
        font: Font,
        lineSpacing: Double
    ) -> some View {
        Text(
            verbatim: String(
                repeating: "\n",
                count: SubtitleTextLayout.currentLineLimit - 1
            )
        )
        .font(font)
        .lineSpacing(lineSpacing)
        .lineLimit(SubtitleTextLayout.currentLineLimit, reservesSpace: true)
        .hidden()
        .frame(maxWidth: .infinity, alignment: .leading)
        .overlay(alignment: .topLeading) {
            text
                .font(font)
                .lineSpacing(lineSpacing)
                .lineLimit(SubtitleTextLayout.currentLineLimit)
                .truncationMode(SubtitleTextLayout.truncationMode)
                .frame(maxWidth: .infinity, alignment: .leading)
        }
    }

    /// 訳文本体と未確定マーカーを1つのTextへ連結する。
    /// SubtitleTailClipper が付ける先頭の「…」とは別物で、こちらは末尾に付く。
    private func translatedText(for subtitle: LiveSubtitle) -> Text {
        let clipped = SubtitleTailClipper.clip(subtitle.translatedText)
        // clip は空白のみ入力をそのまま返すため、表示可否は trim 後で判定する。
        let hasBody = SubtitleVisualStyle.hasVisibleTranslation(clipped)
        guard SubtitleVisualStyle.showsTranslationPendingMarker(for: subtitle) else {
            return Text(hasBody ? clipped : " ")
        }

        let marker = Text(SubtitleVisualStyle.pendingMarker)
            .foregroundStyle(
                Color.white.opacity(SubtitleVisualStyle.pendingMarkerOpacity)
            )
        return hasBody ? Text(clipped) + marker : marker
    }
}

private extension View {
    /// オフセットなしの影を重ねて左上だけ薄くならない対称アウトラインにする。
    /// 太さとにじみは半径ではなくopacityで調整する。
    func subtitleHalo() -> some View {
        self
            .shadow(color: .black.opacity(0.8), radius: 1.5)
            .shadow(color: .black.opacity(0.8), radius: 1.5)
            .shadow(color: .black.opacity(0.85), radius: 5)
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
        isRecording ? UiCopy.text("overlay.stopRecording") : UiCopy.text("overlay.startRecording")
    }

    private var buttonIcon: String {
        isRecording ? "stop.fill" : "mic.fill"
    }
}
