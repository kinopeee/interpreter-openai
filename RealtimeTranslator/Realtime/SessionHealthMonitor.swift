import Foundation

/// 受信停止監視の検知種別。契約は shared/fixtures/v1/health.json。
enum SessionHealthDetectionKind: String, Sendable, Equatable, Hashable, CaseIterable {
    case captureStalled
    case sendStalled
    case receiveStalled
    case sourceStalled
    case translationStalled
    case expiryNear
    case expired
}

enum SessionHealthPhase: String, Sendable, Equatable {
    case idle
    case catchingUp
    case silence
    case awaitingLanguageDetection
    case active
}

/// セッションループ終了理由の自前分類。サーバー文言や生のエラー文字列は保持しない。
enum SessionTerminationKind: String, Sendable, Equatable {
    case missingAPIKey
    case notConnected
    case invalidMessage
    case authenticationFailed
    case fatalServerError
    case recoverableServerError
    case receiveOverflow
    case recoverableTransportFailure
    case sessionUpdateTimeout
    case closeTimeout
    case cancelled
    /// 再接続 budget の枯渇（RealtimeTranslationError にはない終了分類）。
    case reconnectBudgetExhausted
    /// 再接続試行回数の上限（RealtimeTranslationError にはない終了分類）。
    case reconnectAttemptLimit
    /// ユーザーによる停止。
    case userStopped
    /// RealtimeTranslationError 以外の失敗（デバイス・未知のエラー）。
    case other

    init(_ error: RealtimeTranslationError) {
        switch error {
        case .missingAPIKey: self = .missingAPIKey
        case .notConnected: self = .notConnected
        case .invalidMessage: self = .invalidMessage
        case .authenticationFailed: self = .authenticationFailed
        case .fatalServerError: self = .fatalServerError
        case .recoverableServerError: self = .recoverableServerError
        case .receiveOverflow: self = .receiveOverflow
        case .recoverableTransportFailure: self = .recoverableTransportFailure
        case .sessionUpdateTimeout: self = .sessionUpdateTimeout
        case .closeTimeout: self = .closeTimeout
        case .cancelled: self = .cancelled
        }
    }
}

struct SessionHealthDetection: Sendable, Equatable {
    let kind: SessionHealthDetectionKind
    let generation: Int
    let epoch: Int
    let lane: RealtimeTranslationLane?
    /// 検知時点の接続経過時間。
    let elapsed: Duration
    /// 期限系検知のみ: 期限までの残り（expired では負）。その他は nil。
    let remaining: Duration?
}

struct SessionTerminationDiagnostic: Sendable, Equatable {
    let kind: SessionTerminationKind
    let connectionDuration: Duration
    let generation: Int
    let epoch: Int
}

struct SessionHealthSnapshot: Sendable, Equatable {
    var generation: Int = 0
    var epoch: Int = 0
    var phase: SessionHealthPhase = .idle
    var connectionElapsed: Duration = .zero
    var sinceCapture: Duration?
    var sinceSendSuccess: Duration?
    var captureToSendGap: Duration?
    var sinceReceive: Duration?
    var sinceSourceProgress: Duration?
    var sinceSelectedTranslationProgress: Duration?
    var selectedLane: RealtimeTranslationLane?
    var laneExpiryRemaining: [RealtimeTranslationLane: Duration?] = [:]
    var sourceProgressCount: Int = 0
    var translationProgressCount: Int = 0
}

/// shared/fixtures/v1/health.json `thresholds` が既定値の正本。
struct SessionHealthThresholds: Sendable, Equatable {
    var captureStall: Duration = .milliseconds(3_000)
    var sendStall: Duration = .milliseconds(10_000)
    var receiveStall: Duration = .milliseconds(20_000)
    var sourceStall: Duration = .milliseconds(20_000)
    var translationStall: Duration = .milliseconds(15_000)
    var silence: Duration = .milliseconds(5_000)
    var connectGrace: Duration = .milliseconds(10_000)
    var expiryNear: Duration = .milliseconds(120_000)
    var audioActivityPeakFloor: Double = 0.005
}

/// 時計を持たない純粋ロジックの受信停止監視。now は呼び出し側が与える単調時計。
/// 検知に対して再接続・lane 変更は行わない（診断のみ）。
struct SessionHealthMonitor: Sendable {
    var thresholds = SessionHealthThresholds()

    private var active = false
    private var generation = 0
    private var epoch = 0
    private var isRecovery = false
    private var connectedAt: Duration = .zero

    private var lastCapture: Duration?
    private var lastSendSuccess: Duration?
    private var lastReceive: Duration?
    private var lastSourceProgress: Duration?
    private var lastTranslationProgress: [RealtimeTranslationLane: Duration] = [:]
    private var lastAudioActivityAt: Duration?
    private var sendInFlightSince: Duration?
    private var firstActivityAfterLastReceive: Duration?
    private var firstActivityAfterLastSourceProgress: Duration?
    private var selectedLane: RealtimeTranslationLane?
    private var selectedAt: Duration?
    private var expiryDeadlines: [RealtimeTranslationLane: Duration] = [:]
    private var sourceProgressCount = 0
    private var translationProgressCount = 0
    private var emitted: Set<EmittedKey> = []

    private struct EmittedKey: Hashable {
        var kind: SessionHealthDetectionKind
        var lane: RealtimeTranslationLane?
    }

    private var graceEnd: Duration {
        connectedAt + thresholds.connectGrace
    }

    /// 全 timestamp・selectedLane・expiry・emitted をクリアする
    /// （再接続で言語判定がリセットされる既存契約に合わせる）。
    mutating func beginGeneration(generation: Int, epoch: Int, isRecovery: Bool, now: Duration) {
        self.active = true
        self.generation = generation
        self.epoch = epoch
        self.isRecovery = isRecovery
        self.connectedAt = now
        lastCapture = nil
        lastSendSuccess = nil
        sendInFlightSince = nil
        lastReceive = nil
        lastSourceProgress = nil
        lastTranslationProgress = [:]
        lastAudioActivityAt = nil
        firstActivityAfterLastReceive = nil
        firstActivityAfterLastSourceProgress = nil
        selectedLane = nil
        selectedAt = nil
        expiryDeadlines = [:]
        sourceProgressCount = 0
        translationProgressCount = 0
        emitted = []
    }

    mutating func recordCapture(now: Duration, hasAudioActivity: Bool) {
        lastCapture = now
        guard hasAudioActivity else { return }
        lastAudioActivityAt = now
        // grace 中の活動は graceEnd に置く。
        let activityAt = max(now, graceEnd)
        if firstActivityAfterLastReceive == nil {
            firstActivityAfterLastReceive = activityAt
        }
        if firstActivityAfterLastSourceProgress == nil {
            firstActivityAfterLastSourceProgress = activityAt
        }
    }

    /// 直列 feedAudio が send を await し始めた時刻。
    /// in-flight 中は capture 停滞を「送信中」と解釈して captureStalled を出さない。
    mutating func recordSendStart(now: Duration) {
        sendInFlightSince = now
    }

    mutating func recordSendSuccess(now: Duration) {
        lastSendSuccess = now
        sendInFlightSince = nil
    }

    mutating func recordReceive(lane _: RealtimeTranslationLane, now: Duration) {
        lastReceive = max(lastReceive ?? now, now)
        firstActivityAfterLastReceive = nil
    }

    mutating func recordSourceProgress(now: Duration) {
        lastSourceProgress = now
        sourceProgressCount += 1
        firstActivityAfterLastSourceProgress = nil
    }

    mutating func recordTranslationProgress(lane: RealtimeTranslationLane, now: Duration) {
        lastTranslationProgress[lane] = now
        translationProgressCount += 1
    }

    mutating func setSelectedLane(_ lane: RealtimeTranslationLane?, now: Duration) {
        selectedLane = lane
        selectedAt = now
    }

    /// remaining nil = 不明（期限検知をしない）。
    mutating func recordSessionExpiry(
        lane: RealtimeTranslationLane,
        remaining: Duration?,
        now: Duration
    ) {
        if let remaining {
            expiryDeadlines[lane] = now + remaining
        } else {
            expiryDeadlines.removeValue(forKey: lane)
        }
    }

    @discardableResult
    mutating func recordTermination(
        kind: SessionTerminationKind,
        now: Duration
    ) -> SessionTerminationDiagnostic {
        SessionTerminationDiagnostic(
            kind: kind,
            connectionDuration: now - connectedAt,
            generation: generation,
            epoch: epoch
        )
    }

    mutating func endGeneration(now _: Duration) {
        active = false
        emitted = []
    }

    /// その時点の snapshot と、この evaluate で新たに発火した検知（kind の定義順）を返す。
    mutating func evaluate(
        now: Duration
    ) -> (snapshot: SessionHealthSnapshot, newDetections: [SessionHealthDetection]) {
        let phase = currentPhase(now: now)
        var snapshot = SessionHealthSnapshot(
            generation: generation,
            epoch: epoch,
            phase: phase,
            connectionElapsed: active ? now - connectedAt : .zero,
            sinceCapture: lastCapture.map { now - $0 },
            sinceSendSuccess: lastSendSuccess.map { now - $0 },
            captureToSendGap: captureToSendGap(now: now),
            sinceReceive: lastReceive.map { now - $0 },
            sinceSourceProgress: lastSourceProgress.map { now - $0 },
            selectedLane: selectedLane,
            sourceProgressCount: sourceProgressCount,
            translationProgressCount: translationProgressCount
        )
        if let selectedLane, let lastSelected = lastTranslationProgress[selectedLane] {
            snapshot.sinceSelectedTranslationProgress = now - lastSelected
        }
        for (lane, deadline) in expiryDeadlines {
            snapshot.laneExpiryRemaining[lane] = deadline - now
        }

        guard active else {
            return (snapshot, [])
        }

        var detections: [SessionHealthDetection] = []
        for kind in SessionHealthDetectionKind.allCases {
            switch kind {
            case .captureStalled:
                // send が in-flight の間は capture が記録されないのは直列 send の待ち
                // によるもので capture 停止ではない。
                if sendInFlightSince == nil,
                    now - (lastCapture ?? connectedAt) >= thresholds.captureStall
                {
                    emit(&detections, kind: kind, lane: nil, now: now)
                }
            case .sendStalled:
                let sinceCapture = now - (lastCapture ?? connectedAt)
                // in-flight send は開始時刻から測る（開始自体が停滞の起点）。
                let sendReference = max(
                    lastSendSuccess ?? connectedAt,
                    sendInFlightSince ?? connectedAt
                )
                let sinceSend = now - sendReference
                // in-flight 中は直列 send が capture 記録を止めるため、
                // in-flight 自体を capture 生存の証拠とする。
                if now >= graceEnd,
                    sendInFlightSince != nil || sinceCapture < thresholds.captureStall,
                    sinceSend >= thresholds.sendStall
                {
                    emit(&detections, kind: kind, lane: nil, now: now)
                }
            case .receiveStalled:
                if now >= graceEnd,
                    let first = firstActivityAfterLastReceive,
                    now - first >= thresholds.receiveStall
                {
                    emit(&detections, kind: kind, lane: nil, now: now)
                }
            case .sourceStalled:
                let receiveStalled =
                    firstActivityAfterLastReceive != nil
                    && now - firstActivityAfterLastReceive! >= thresholds.receiveStall
                if now >= graceEnd,
                    !receiveStalled,
                    let first = firstActivityAfterLastSourceProgress,
                    now - first >= thresholds.sourceStall
                {
                    emit(&detections, kind: kind, lane: nil, now: now)
                }
            case .translationStalled:
                guard now >= graceEnd,
                    let selectedLane,
                    let selectedAt,
                    let lastSourceProgress,
                    lastSourceProgress >= selectedAt,
                    now - lastSourceProgress >= thresholds.translationStall
                else {
                    break
                }
                // selectedAt より古い lane 進捗は無視（nil 扱い）。
                let lastTranslation = lastTranslationProgress[selectedLane].flatMap {
                    $0 >= selectedAt ? $0 : nil
                }
                if lastTranslation == nil
                    || lastSourceProgress - lastTranslation! >= thresholds.translationStall
                {
                    emit(&detections, kind: kind, lane: selectedLane, now: now)
                }
            case .expiryNear, .expired:
                for (lane, deadline) in expiryDeadlines.sorted(by: { laneOrder($0.key) < laneOrder($1.key) }) {
                    let remaining = deadline - now
                    if kind == .expiryNear, remaining > .zero, remaining <= thresholds.expiryNear {
                        emit(&detections, kind: kind, lane: lane, now: now, remaining: remaining)
                    } else if kind == .expired, remaining <= .zero {
                        emit(&detections, kind: kind, lane: lane, now: now, remaining: remaining)
                    }
                }
            }
        }
        return (snapshot, detections)
    }

    private mutating func emit(
        _ detections: inout [SessionHealthDetection],
        kind: SessionHealthDetectionKind,
        lane: RealtimeTranslationLane?,
        now: Duration,
        remaining: Duration? = nil
    ) {
        let key = EmittedKey(kind: kind, lane: lane)
        guard emitted.insert(key).inserted else { return }
        detections.append(
            SessionHealthDetection(
                kind: kind,
                generation: generation,
                epoch: epoch,
                lane: lane,
                elapsed: now - connectedAt,
                remaining: remaining
            )
        )
    }

    private func currentPhase(now: Duration) -> SessionHealthPhase {
        guard active else { return .idle }
        if now < graceEnd, isRecovery, lastSourceProgress == nil {
            return .catchingUp
        }
        if lastAudioActivityAt == nil || now - lastAudioActivityAt! >= thresholds.silence {
            return .silence
        }
        if selectedLane == nil {
            return .awaitingLanguageDetection
        }
        return .active
    }

    private func captureToSendGap(now _: Duration) -> Duration? {
        guard let lastCapture, let lastSendSuccess else { return nil }
        return lastSendSuccess - lastCapture
    }
}

extension RealtimeTranslationLane {
    /// 診断用の content-free な lane 名。
    var healthLogName: String {
        switch self {
        case .source:
            return "source"
        case .translation(let target):
            return target.rawValue
        }
    }
}

extension Duration {
    /// 診断表示用の切り捨てミリ秒。
    var wholeMillisecondsTruncated: Int64 {
        let components = components
        return components.seconds * 1_000 + components.attoseconds / 1_000_000_000_000_000
    }
}

extension SessionHealthDetection {
    /// DEBUG status file の3行目 `health=` 以降の content-free な断片。
    var statusLineFragment: String {
        "\(kind.rawValue) gen=\(generation) lane=\(lane?.healthLogName ?? "-")"
    }
}

extension SessionHealthDetection: CustomStringConvertible {
    /// 数値と enum 名だけの content-free な表現（ログ用）。
    var description: String {
        "kind=\(kind.rawValue) generation=\(generation) epoch=\(epoch) "
            + "lane=\(lane?.healthLogName ?? "-") "
            + "elapsedMs=\(elapsed.wholeMillisecondsTruncated) "
            + "remainingMs=\(remaining.map { String($0.wholeMillisecondsTruncated) } ?? "-")"
    }
}

extension SessionHealthSnapshot: CustomStringConvertible {
    var description: String {
        "phase=\(phase.rawValue) generation=\(generation) epoch=\(epoch) "
            + "elapsedMs=\(connectionElapsed.wholeMillisecondsTruncated) "
            + "lane=\(selectedLane?.healthLogName ?? "-") "
            + "sinceReceiveMs=\(sinceReceive.map { String($0.wholeMillisecondsTruncated) } ?? "-") "
            + "sinceSourceProgressMs=\(sinceSourceProgress.map { String($0.wholeMillisecondsTruncated) } ?? "-") "
            + "sourceProgressCount=\(sourceProgressCount) "
            + "translationProgressCount=\(translationProgressCount) "
            + "sinceCaptureMs=\(sinceCapture.map { String($0.wholeMillisecondsTruncated) } ?? "-") "
            + "sinceSendSuccessMs=\(sinceSendSuccess.map { String($0.wholeMillisecondsTruncated) } ?? "-") "
            + "expiryRemainingMs=\(expiryRemainingDescription)"
    }

    /// `expiryRemainingMs=<lane>:<ms>[,...]`（lane 順固定、値不明は `-`、map 空なら全体 `-`）。
    private var expiryRemainingDescription: String {
        if laneExpiryRemaining.isEmpty { return "-" }
        return laneExpiryRemaining.keys
            .sorted { laneOrder($0) < laneOrder($1) }
            .map { lane in
                let value =
                    laneExpiryRemaining[lane].flatMap { $0 }
                    .map { String($0.wholeMillisecondsTruncated) } ?? "-"
                return "\(lane.healthLogName):\(value)"
            }
            .joined(separator: ",")
    }
}

extension SessionTerminationDiagnostic: CustomStringConvertible {
    var description: String {
        "kind=\(kind.rawValue) durationMs=\(connectionDuration.wholeMillisecondsTruncated) "
            + "generation=\(generation) epoch=\(epoch)"
    }
}

/// expiry 検知の lane 反復順を固定する（検知列は kind 順→lane 順）。
private func laneOrder(_ lane: RealtimeTranslationLane) -> Int {
    switch lane {
    case .source: return 0
    case .translation(.english): return 1
    case .translation(.japanese): return 2
    case .translation(.spanish): return 3
    }
}
