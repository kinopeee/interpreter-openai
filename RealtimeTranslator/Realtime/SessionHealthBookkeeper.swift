import Foundation

/// 受信停止監視の帳簿。`SessionHealthMonitor`（struct）を所有し、
/// 接続試行・世代の診断窓もここで管理する。@MainActor 閉じ込め。
@MainActor
final class SessionHealthBookkeeper {
    /// 受信監視で数える対象 lane（source + 全 target）。
    static let healthLanes: [RealtimeTranslationLane] = [
        .source,
        .translation(.english),
        .translation(.japanese),
        .translation(.spanish),
    ]

    private let healthNow: ReconnectBudget.Now
    private let wallClockNow: @Sendable () -> TimeInterval
    private let reservedEpochProvider: () async -> Int

    private var healthMonitor = SessionHealthMonitor()
    private var healthReceiveCounts: [RealtimeTranslationLane: Int] = [:]
    private var healthGenerationEnded = true
    /// 世代未開始（pre-Listening）の終了診断用に、接続試行の開始時刻と意図 epoch を保持する。
    /// epoch は dual client の予約済み epoch（handshake 失敗時も予約値を保持し、
    /// APIキー欠落時は直前の予約 epoch）。
    private var healthAttemptStart: Duration?
    private var healthAttemptEpoch = 0
    private var healthAttemptGeneration = 0
    private var lastHealthSnapshotLogAt: Duration?
    private var connectionCountInGeneration = 0

    /// テスト・診断用の最新 snapshot（検知には使わない）。
    private(set) var latestHealthSnapshot: SessionHealthSnapshot?
    /// テスト・診断用の最新 termination diagnostic（各試行で最大 1 件）。
    private(set) var latestHealthTermination: SessionTerminationDiagnostic?

    init(
        thresholds: SessionHealthThresholds,
        healthNow: @escaping ReconnectBudget.Now,
        wallClockNow: @escaping @Sendable () -> TimeInterval,
        reservedEpochProvider: @escaping () async -> Int
    ) {
        self.healthMonitor.thresholds = thresholds
        self.healthNow = healthNow
        self.wallClockNow = wallClockNow
        self.reservedEpochProvider = reservedEpochProvider
    }

    var thresholds: SessionHealthThresholds { healthMonitor.thresholds }

    /// 新しい録音世代の開始時に接続試行カウンタを戻す。
    func beginRecordingGeneration() {
        connectionCountInGeneration = 0
    }

    /// 接続試行の開始を記録する。epoch は呼び出し側が読んだ Dual 側の予約値
    /// （RequireApiKey 失敗（missing key）も試行の終了診断へ乗せるため先に記録する）。
    func beginAttempt(lifecycleGeneration: Int, reservedEpoch: Int) {
        connectionCountInGeneration += 1
        healthAttemptStart = healthNow()
        healthAttemptEpoch = reservedEpoch
        healthAttemptGeneration = lifecycleGeneration
    }

    /// handshake 中に接続側の予約 epoch が進んだあと読み直した値で更新する。
    func updateAttemptEpoch(_ reservedEpoch: Int) {
        healthAttemptEpoch = reservedEpoch
    }

    /// Listening 確定時に monitor 世代を開始し、受信数・期限をシードする。
    func beginGeneration(generation: Int, epoch: Int, deliveryState: EventDeliveryState) {
        let monitorNow = healthNow()
        healthMonitor.beginGeneration(
            generation: generation,
            epoch: epoch,
            isRecovery: connectionCountInGeneration > 1,
            now: monitorNow
        )
        healthGenerationEnded = false
        lastHealthSnapshotLogAt = nil
        // handshake の受信を初回 tick で「新規受信」と誤認しないよう現数でシードする。
        for lane in Self.healthLanes {
            healthReceiveCounts[lane] = deliveryState.receiveCount(lane)
        }
        // 世代が始まった attempt の診断窓は monitor 側へ移す。
        healthAttemptStart = nil
        // 期限の remaining は受信時に一度だけ壁時計で算出し、以後は単調時計で追う。
        let wallNow = wallClockNow()
        for lane in Self.healthLanes {
            let remaining = deliveryState.sessionExpiry(lane).flatMap {
                RealtimeSessionExpiry.remaining(
                    expiresAtUnixSeconds: $0,
                    wallNowUnixSeconds: Int64(wallNow)
                )
            }
            healthMonitor.recordSessionExpiry(lane: lane, remaining: remaining, now: monitorNow)
        }
    }

    /// 各 lane の decode 受信数の差分で recordReceive し、evaluate を回す。
    /// 検知に対して再接続や lane 変更は行わない（診断のみ）。
    /// 戻り値は呼び出し側が delegate へ流す検知列。
    func tick(feed: EventFeed?) -> [SessionHealthDetection] {
        guard let feed else { return [] }
        let now = healthNow()
        for lane in Self.healthLanes {
            let count = feed.deliveryState.receiveCount(lane)
            if count > (healthReceiveCounts[lane] ?? 0) {
                healthMonitor.recordReceive(lane: lane, now: now)
            }
            healthReceiveCounts[lane] = count
        }

        let (snapshot, detections) = healthMonitor.evaluate(now: now)
        latestHealthSnapshot = snapshot
        for detection in detections {
            #if DEBUG
            AppLogger.session.notice(
                "DBG_HEALTH \(detection.description, privacy: .public)"
            )
            #endif
        }
        #if DEBUG
        if lastHealthSnapshotLogAt == nil || now - lastHealthSnapshotLogAt! >= .seconds(5) {
            lastHealthSnapshotLogAt = now
            AppLogger.session.notice(
                "DBG_HEALTH_SNAPSHOT \(snapshot.description, privacy: .public)"
            )
        }
        #endif
        return detections
    }

    /// セッションループ終了・停止時の診断。kind は自前 enum のみ（生 message は渡さない）。
    /// 各世代で最初の終了経路だけを記録する。
    func recordTermination(_ error: Error) async {
        let kind: SessionTerminationKind
        if let error = error as? RealtimeTranslationError {
            kind = SessionTerminationKind(error)
        } else {
            kind = .other
        }
        await recordTermination(kind: kind)
    }

    func recordTermination(kind: SessionTerminationKind) async {
        let now = healthNow()
        let diagnostic: SessionTerminationDiagnostic
        if !healthGenerationEnded {
            diagnostic = healthMonitor.recordTermination(kind: kind, now: now)
            endGeneration()
        } else if let attemptStart = healthAttemptStart {
            // 世代未開始（pre-Listening / handshake 失敗）の終了は attempt の
            // 開始時刻・意図 epoch で記録する。monitor の stall 状態には触れない。
            // handshake 中の stop では接続側の予約 epoch が先に進むため記録時に読み直す。
            healthAttemptEpoch = await reservedEpochProvider()
            diagnostic = SessionTerminationDiagnostic(
                kind: kind,
                connectionDuration: max(.zero, now - attemptStart),
                generation: healthAttemptGeneration,
                epoch: healthAttemptEpoch
            )
        } else {
            return
        }
        // 1 試行につき 1 件だけ。
        healthAttemptStart = nil
        latestHealthTermination = diagnostic
        #if DEBUG
        AppLogger.session.notice(
            "DBG_HEALTH_TERMINATION \(diagnostic.description, privacy: .public)"
        )
        #endif
    }

    /// 健康世代を閉じる。
    func endGeneration() {
        guard !healthGenerationEnded else { return }
        healthGenerationEnded = true
        healthMonitor.endGeneration(now: healthNow())
        latestHealthSnapshot = healthMonitor.evaluate(now: healthNow()).snapshot
    }

    // 以下は monitor 記録の直通 forward。now はここで単調時計から読む。

    func recordCapture(hasAudioActivity: Bool) {
        healthMonitor.recordCapture(now: healthNow(), hasAudioActivity: hasAudioActivity)
    }

    func recordSendStart() {
        healthMonitor.recordSendStart(now: healthNow())
    }

    func recordSendSuccess() {
        healthMonitor.recordSendSuccess(now: healthNow())
    }

    func recordSourceProgress() {
        healthMonitor.recordSourceProgress(now: healthNow())
    }

    func recordTranslationProgress(lane: RealtimeTranslationLane) {
        healthMonitor.recordTranslationProgress(lane: lane, now: healthNow())
    }

    func setSelectedLane(_ lane: RealtimeTranslationLane?) {
        healthMonitor.setSelectedLane(lane, now: healthNow())
    }
}
