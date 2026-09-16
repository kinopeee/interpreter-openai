import XCTest
@testable import RealtimeTranslator

/// shared/fixtures/v1/health.json を正本として、SessionHealthMonitor の
/// 位相・検知系列を単調時計上で検証する。
final class SessionHealthMonitorFixtureTests: XCTestCase {
    // Given: health.json の thresholds セクション
    // When: SessionHealthThresholds の既定値を見る
    // Then: fixture の thresholds と一致する
    func testDefaultThresholdsMatchFixture() throws {
        let root = try SharedFixtures.load("health")
        let thresholds = try XCTUnwrap(root["thresholds"] as? [String: Any])

        let defaults = SessionHealthThresholds()
        XCTAssertEqual(defaults.captureStall, .milliseconds(number(thresholds, "captureStallMs")))
        XCTAssertEqual(defaults.sendStall, .milliseconds(number(thresholds, "sendStallMs")))
        XCTAssertEqual(defaults.receiveStall, .milliseconds(number(thresholds, "receiveStallMs")))
        XCTAssertEqual(defaults.sourceStall, .milliseconds(number(thresholds, "sourceStallMs")))
        XCTAssertEqual(
            defaults.translationStall,
            .milliseconds(number(thresholds, "translationStallMs"))
        )
        XCTAssertEqual(defaults.silence, .milliseconds(number(thresholds, "silenceMs")))
        XCTAssertEqual(defaults.connectGrace, .milliseconds(number(thresholds, "connectGraceMs")))
        XCTAssertEqual(defaults.expiryNear, .milliseconds(number(thresholds, "expiryNearMs")))
        XCTAssertEqual(
            defaults.audioActivityPeakFloor,
            SharedFixtures.real(thresholds["audioActivityPeakFloor"]),
            accuracy: 1e-9
        )
    }

    // Given: health.json の各 scenario（単調時刻 ms の op 列）
    // When: 時刻順に op を SessionHealthMonitor へ適用する
    // Then: 各 evaluate の phase と新規検知（kind 定義順）が期待と一致する
    func testScenariosMatchFixture() throws {
        for name in try SharedFixtures.caseNames("health", "scenarios") {
            let scenario = try SharedFixtures.case("health", "scenarios", name)
            var monitor = SessionHealthMonitor()

            // steps は op ごとにまとまって並ぶため、単調時刻で安定ソートする。
            let rawSteps = try XCTUnwrap(scenario["steps"] as? [[String: Any]])
            let steps = rawSteps.enumerated().sorted {
                number($0.element, "at") == number($1.element, "at")
                    ? $0.offset < $1.offset
                    : number($0.element, "at") < number($1.element, "at")
            }

            for (_, step) in steps {
                let at = Duration.milliseconds(number(step, "at"))
                switch SharedFixtures.text(step["op"]) {
                case "begin":
                    monitor.beginGeneration(
                        generation: number(step, "generation"),
                        epoch: number(step, "epoch"),
                        isRecovery: step["isRecovery"] as? Bool ?? false,
                        now: at
                    )
                case "capture":
                    monitor.recordCapture(now: at, hasAudioActivity: step["active"] as? Bool ?? false)
                case "send":
                    monitor.recordSendSuccess(now: at)
                case "sendStart":
                    monitor.recordSendStart(now: at)
                case "receive":
                    monitor.recordReceive(lane: try lane(step["lane"], stepName: name), now: at)
                case "source":
                    monitor.recordSourceProgress(now: at)
                case "translation":
                    monitor.recordTranslationProgress(
                        lane: try lane(step["lane"], stepName: name),
                        now: at
                    )
                case "select":
                    monitor.setSelectedLane(
                        step["lane"] is NSNull || step["lane"] == nil
                            ? nil
                            : try lane(step["lane"], stepName: name),
                        now: at
                    )
                case "expiry":
                    let remaining: Duration? =
                        step["remainingMs"] is NSNull || step["remainingMs"] == nil
                            ? nil
                            : .milliseconds(number(step, "remainingMs"))
                    monitor.recordSessionExpiry(
                        lane: try lane(step["lane"], stepName: name),
                        remaining: remaining,
                        now: at
                    )
                case "evaluate":
                    let (snapshot, detections) = monitor.evaluate(now: at)
                    let expect = try XCTUnwrap(step["expect"] as? [String: Any])
                    XCTAssertEqual(
                        snapshot.phase.rawValue,
                        SharedFixtures.text(expect["phase"]),
                        "phase mismatch at \(number(step, "at")) in \(name)"
                    )
                    let expectedDetections = try XCTUnwrap(expect["detections"] as? [Any])
                        .map { entry -> (kind: String, lane: String?) in
                            if let text = entry as? String {
                                return (text, nil)
                            }
                            let object = entry as? [String: Any]
                            return (
                                SharedFixtures.text(object?["kind"]),
                                SharedFixtures.optionalText(object?["lane"])
                            )
                        }
                    XCTAssertEqual(
                        detections.map { $0.kind.rawValue },
                        expectedDetections.map { $0.kind },
                        "detection kinds mismatch at \(number(step, "at")) in \(name)"
                    )
                    // lane が fixture 側で指定されている検知だけ lane を照合する。
                    for (detection, expected) in zip(detections, expectedDetections)
                    where expected.lane != nil {
                        XCTAssertEqual(
                            detection.lane?.healthLogName,
                            expected.lane,
                            "detection lane mismatch at \(number(step, "at")) in \(name)"
                        )
                    }
                default:
                    XCTFail("unknown op in \(name): \(step)")
                }
            }
        }
    }

    // Given: en lane に残り 130 秒の期限が記録された monitor
    // When: evaluate の snapshot を description する
    // Then: expiryRemainingMs=en:130000 と sinceCapture/sinceSendSuccess を含む
    func testSnapshotDescriptionIncludesExpiryRemaining() {
        var monitor = SessionHealthMonitor()
        monitor.beginGeneration(generation: 1, epoch: 1, isRecovery: false, now: .zero)
        monitor.recordSessionExpiry(
            lane: .translation(.english),
            remaining: .seconds(130),
            now: .zero
        )

        let (snapshot, _) = monitor.evaluate(now: .zero)
        XCTAssertTrue(snapshot.description.contains("expiryRemainingMs=en:130000"))
        XCTAssertTrue(snapshot.description.contains("sinceCaptureMs=-"))
        XCTAssertTrue(snapshot.description.contains("sinceSendSuccessMs=-"))
    }

    // Given: send が in-flight で capture が止まっている monitor
    // When: captureStall・sendStall の閾値を超える時刻で evaluate する
    // Then: captureStalled は出ず、sendStalled は in-flight 開始から 10s で発火する
    func testInFlightSendSuppressesCaptureStalled() {
        var monitor = SessionHealthMonitor()
        monitor.beginGeneration(generation: 1, epoch: 1, isRecovery: false, now: .zero)
        monitor.recordCapture(now: .milliseconds(100), hasAudioActivity: true)
        monitor.recordSendSuccess(now: .milliseconds(100))
        monitor.recordCapture(now: .milliseconds(200), hasAudioActivity: true)
        monitor.recordSendStart(now: .milliseconds(200))

        // in-flight 開始から 3.1s 後: captureStalled は出ない。
        var (_, detections) = monitor.evaluate(now: .milliseconds(3_300))
        XCTAssertFalse(detections.contains { $0.kind == .captureStalled })

        // in-flight 開始から 10s: sendStalled が発火する。
        (_, detections) = monitor.evaluate(now: .milliseconds(10_200))
        XCTAssertTrue(detections.contains { $0.kind == .sendStalled })
        XCTAssertFalse(detections.contains { $0.kind == .captureStalled })
    }

    private func number(_ object: [String: Any], _ key: String) -> Int {
        SharedFixtures.number(object[key])
    }

    private func lane(_ value: Any?, stepName: String) throws -> RealtimeTranslationLane {
        switch SharedFixtures.text(value) {
        case "source": return .source
        case "en": return .translation(.english)
        case "ja": return .translation(.japanese)
        case "es": return .translation(.spanish)
        default: throw SharedFixtures.FixtureError.invalidCase(
            fixture: "health",
            section: "scenarios",
            name: stepName
        )
        }
    }
}
