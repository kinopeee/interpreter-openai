import os
import XCTest
@testable import RealtimeTranslator

/// テスト用の単調クロック。手で進めた分だけ経過する。
final class ManualMonotonicClock: @unchecked Sendable {
    private let elapsed = OSAllocatedUnfairLock(initialState: Duration.zero)

    var now: ReconnectBudget.Now {
        { [elapsed] in elapsed.withLock { $0 } }
    }

    func set(_ value: Duration) {
        elapsed.withLock { $0 = value }
    }

    func advance(_ delta: Duration) {
        elapsed.withLock { $0 += delta }
    }
}

/// shared/fixtures/v1/reconnect.json を正本として、再接続予算が単調クロック上の
/// 失敗 / Listening の列だけで決まることを確認する。
final class ReconnectBudgetFixtureTests: XCTestCase {
    func testPolicyMatchesFixture() throws {
        let fixture = try SharedFixtures.load("reconnect")
        let policy = try XCTUnwrap(fixture["policy"] as? [String: Any])
        let actual = ReconnectPolicy.default

        XCTAssertEqual(actual.initialBackoff, .milliseconds(SharedFixtures.number(policy["initialBackoffMs"])))
        XCTAssertEqual(actual.backoffMultiplier, SharedFixtures.number(policy["backoffMultiplier"]))
        XCTAssertEqual(actual.maxBackoff, .milliseconds(SharedFixtures.number(policy["maxBackoffMs"])))
        XCTAssertEqual(actual.jitterMax, .milliseconds(SharedFixtures.number(policy["jitterMaxMs"])))
        XCTAssertEqual(actual.maxAttempts, SharedFixtures.number(policy["maxAttempts"]))
        XCTAssertEqual(actual.totalBudget, .milliseconds(SharedFixtures.number(policy["totalBudgetMs"])))
        XCTAssertEqual(actual.stablePeriod, .milliseconds(SharedFixtures.number(policy["stablePeriodMs"])))
        XCTAssertEqual(
            RealtimeTranslationConnection.defaultHandshakeTimeoutNanoseconds,
            UInt64(SharedFixtures.number(policy["attemptTimeoutMs"])) * 1_000_000
        )

        let budgetExhausted = try XCTUnwrap(fixture["budgetExhausted"] as? [String: Any])
        let attemptLimit = try XCTUnwrap(fixture["attemptLimit"] as? [String: Any])
        XCTAssertEqual(SharedFixtures.text(budgetExhausted["errorMessageKey"]), "error.reconnectBudgetExhausted")
        XCTAssertEqual(SharedFixtures.text(attemptLimit["errorMessageKey"]), "error.reconnectLimit")
        XCTAssertNotEqual(UiCopy.text("error.reconnectLimit"), UiCopy.text("error.reconnectBudgetExhausted"))
    }

    func testReplayMatchesFixture() throws {
        for name in try SharedFixtures.caseNames("reconnect", "cases") {
            let item = try SharedFixtures.case("reconnect", "cases", name)
            let clock = ManualMonotonicClock()
            var budget = ReconnectBudget(policy: .default, now: clock.now, jitter: { _ in .zero })
            budget.reset()

            let events = try XCTUnwrap(item["events"] as? [[String: Any]], name)
            for event in events {
                let atMs = SharedFixtures.number(event["atMs"])
                clock.set(.milliseconds(atMs))
                let step = "\(name) @ \(atMs)ms"
                switch SharedFixtures.text(event["kind"]) {
                case "listening":
                    budget.recordListening()
                case "failure":
                    let expected = try XCTUnwrap(event["expected"] as? [String: Any], step)
                    let decision = budget.recordFailure()
                    switch SharedFixtures.text(expected["decision"]) {
                    case "wait":
                        XCTAssertEqual(decision.kind, .wait, step)
                        XCTAssertEqual(decision.attempt, SharedFixtures.number(expected["attempt"]), step)
                        XCTAssertEqual(
                            decision.backoff,
                            .milliseconds(SharedFixtures.number(expected["backoffMs"])),
                            step
                        )
                        XCTAssertEqual(decision.jitter, .zero, step)
                    case "attemptLimit":
                        XCTAssertEqual(decision.kind, .attemptLimit, step)
                    case "budgetExhausted":
                        XCTAssertEqual(decision.kind, .budgetExhausted, step)
                    default:
                        XCTFail("unknown decision in \(step)")
                    }
                default:
                    XCTFail("unknown event kind in \(name)")
                }
            }
        }
    }

    func testWallClockIsNotConsulted() {
        // Given: 単調クロックだけを進める budget
        let clock = ManualMonotonicClock()
        var budget = ReconnectBudget(policy: .default, now: clock.now, jitter: { _ in .zero })

        // When: 壁時計相当の待ち（実時間）は一切挟まず、監視クロックも止めたまま失敗を重ねる
        XCTAssertEqual(budget.recordFailure().kind, .wait)
        budget.recordListening()
        for _ in 0..<4 {
            XCTAssertEqual(budget.recordFailure().kind, .wait)
        }

        // Then: 単調クロックが進まない限り安定期間にも予算にも達しない
        XCTAssertEqual(budget.recordFailure().kind, .attemptLimit)
    }

    func testDefaultJitterStaysWithinPolicyRange() {
        for _ in 0..<200 {
            var budget = ReconnectBudget(policy: .default)
            let decision = budget.recordFailure()
            XCTAssertGreaterThanOrEqual(decision.jitter, .zero)
            XCTAssertLessThanOrEqual(decision.jitter, ReconnectPolicy.default.jitterMax)
            XCTAssertEqual(decision.delay, decision.backoff + decision.jitter)
        }
    }

    func testBackoffIsCappedAtMaxBackoff() {
        let policy = ReconnectPolicy.default
        XCTAssertEqual(policy.backoff(forAttempt: 1), .milliseconds(500))
        XCTAssertEqual(policy.backoff(forAttempt: 5), .seconds(8))
        XCTAssertEqual(policy.backoff(forAttempt: 40), .seconds(8))
    }
}

@MainActor
final class InterpretationSessionReconnectBudgetTests: XCTestCase {
    private static let fastPolicy: ReconnectPolicy = {
        var policy = ReconnectPolicy.default
        policy.initialBackoff = .milliseconds(1)
        policy.maxBackoff = .milliseconds(1)
        return policy
    }()

    private func makeSession(
        dual: FakeDualRealtimeTranslationClient,
        clock: ManualMonotonicClock,
        policy: ReconnectPolicy = InterpretationSessionReconnectBudgetTests.fastPolicy
    ) -> (InterpretationSession, InterpretationSessionDelegateSpy) {
        let session = InterpretationSession(
            apiKeyStore: InMemoryAPIKeyStore(initialKey: "sk-test"),
            audioCapture: FakeRealtimeAudioCaptureService(),
            dualClient: dual,
            activeTickerIntervalNanoseconds: 20_000_000,
            reconnectBudget: ReconnectBudget(policy: policy, now: clock.now, jitter: { _ in .zero })
        )
        let delegate = InterpretationSessionDelegateSpy()
        session.delegate = delegate
        return (session, delegate)
    }

    private func failAndRecover(
        _ session: InterpretationSession,
        _ dual: FakeDualRealtimeTranslationClient,
        expectedStarts: Int
    ) async {
        dual.emit(target: .english, event: .error(message: "socket closed", code: "transport", errorType: nil))
        await waitUntil(timeout: 3) {
            dual.startCallCount >= expectedStarts && session.state == .listening
        }
    }

    func testShortListeningDoesNotResetAttemptCounter() async {
        // Given: 再接続には成功するが Listening が 30s に届かない
        let dual = FakeDualRealtimeTranslationClient()
        let clock = ManualMonotonicClock()
        let (session, delegate) = makeSession(dual: dual, clock: clock)
        await session.start()
        await waitUntil { session.state == .listening }

        // When: 上限回数まで失敗→回復を繰り返し、さらに 1 回失敗する
        var expectedStarts = 1
        for _ in 0..<ReconnectPolicy.default.maxAttempts {
            clock.advance(.seconds(5))
            expectedStarts += 1
            await failAndRecover(session, dual, expectedStarts: expectedStarts)
        }
        clock.advance(.seconds(5))
        dual.emit(target: .english, event: .error(message: "socket closed", code: "transport", errorType: nil))
        await waitUntil(timeout: 3) { session.state == .error }

        // Then: 試行回数はリセットされず上限で error.reconnectLimit
        XCTAssertEqual(dual.startCallCount, expectedStarts)
        XCTAssertEqual(delegate.messages.last, UiCopy.text("error.reconnectLimit"))
    }

    func testStableListeningResetsAttemptCounterBeforeLimit() async {
        // Given: 再接続後の Listening が毎回 30s 以上続く
        let dual = FakeDualRealtimeTranslationClient()
        let clock = ManualMonotonicClock()
        let (session, delegate) = makeSession(dual: dual, clock: clock)
        await session.start()
        await waitUntil { session.state == .listening }

        // When: 上限回数を超えて失敗→回復を繰り返す
        var expectedStarts = 1
        for _ in 0..<(ReconnectPolicy.default.maxAttempts + 1) {
            clock.advance(ReconnectPolicy.default.stablePeriod)
            expectedStarts += 1
            await failAndRecover(session, dual, expectedStarts: expectedStarts)
            XCTAssertNotEqual(session.state, .error)
        }

        // Then: 安定運転後の失敗でカウンタが戻り、Error に落ちない
        XCTAssertEqual(session.state, .listening)
        XCTAssertTrue(delegate.messages.isEmpty)
        await session.stop()
        XCTAssertEqual(session.state, .idle)
    }

    func testTotalBudgetExhaustionStopsBeforeAttemptLimit() async {
        // Given: 総予算 10s の policy
        let dual = FakeDualRealtimeTranslationClient()
        let clock = ManualMonotonicClock()
        var policy = Self.fastPolicy
        policy.totalBudget = .seconds(10)
        let (session, delegate) = makeSession(dual: dual, clock: clock, policy: policy)
        await session.start()
        await waitUntil { session.state == .listening }

        // When: 1 回回復した後、障害開始から 10s を超えて再び失敗する
        await failAndRecover(session, dual, expectedStarts: 2)
        clock.advance(.seconds(11))
        dual.emit(target: .english, event: .error(message: "socket closed", code: "transport", errorType: nil))
        await waitUntil(timeout: 3) { session.state == .error }

        // Then: 試行回数は上限前でも error.reconnectBudgetExhausted で停止
        XCTAssertEqual(dual.startCallCount, 2)
        XCTAssertEqual(delegate.messages.last, UiCopy.text("error.reconnectBudgetExhausted"))
    }

    func testStopDuringReconnectWaitCancelsImmediately() async {
        // Given: backoff が長い policy と、1 回目の start だけ失敗する dual
        let dual = FakeDualRealtimeTranslationClient()
        dual.startFailuresRemaining = 1
        let clock = ManualMonotonicClock()
        var policy = ReconnectPolicy.default
        policy.initialBackoff = .seconds(30)
        policy.maxBackoff = .seconds(30)
        let (session, _) = makeSession(dual: dual, clock: clock, policy: policy)

        // When: 再接続待ちの最中に stop する
        await session.start()
        await waitUntil { session.state == .reconnecting }
        let started = ContinuousClock.now
        await session.stop()

        // Then: backoff を待たずに idle へ戻り、その後も再接続は始まらない
        XCTAssertLessThan(started.duration(to: .now), .seconds(5))
        XCTAssertEqual(session.state, .idle)
        try? await Task.sleep(nanoseconds: 100_000_000)
        XCTAssertEqual(dual.startCallCount, 1)
        XCTAssertEqual(session.state, .idle)
    }
}
