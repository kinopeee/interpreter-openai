import Foundation
import os
import XCTest
@testable import RealtimeTranslator

/// `shared/fixtures/v1/routing.json` の routing 契約を DualRealtimeTranslationClient で検証する。
final class RoutingFixtureTests: XCTestCase {
    // Given: fixture の routing シナリオ（言語切替 / preroll / 送信失敗を含む）
    // When: DualRealtimeTranslationClient に手順どおり適用する
    // Then: 原文・英語・日本語それぞれの lane へ届く frame 列が期待どおりになる
    func testRoutingCasesMatchFixture() async throws {
        for name in try SharedFixtures.caseNames("routing", "cases") {
            let fixtureCase = try SharedFixtures.case("routing", "cases", name)
            let harness = try await RoutingHarness.start()

            do {
                let steps = try XCTUnwrap(fixtureCase["steps"] as? [Any])
                for stepItem in steps {
                    let step = try XCTUnwrap(stepItem as? [String: Any])
                    try await harness.apply(step)
                }

                let expected = try XCTUnwrap(fixtureCase["expected"] as? [String: Any])
                let sourceFrames = await harness.appendedFrameTexts(from: harness.sourceTransport)
                let englishFrames = await harness.appendedFrameTexts(from: harness.englishTransport)
                let japaneseFrames = await harness.appendedFrameTexts(from: harness.japaneseTransport)
                XCTAssertEqual(
                    frameNames(expected["sourceFrames"]),
                    sourceFrames,
                    name
                )
                XCTAssertEqual(
                    frameNames(expected["englishFrames"]),
                    englishFrames,
                    name
                )
                XCTAssertEqual(
                    frameNames(expected["japaneseFrames"]),
                    japaneseFrames,
                    name
                )

                let expectedTransportErrors =
                    SharedFixtures.optionalNumber(expected["transportErrorCount"]) ?? 0
                let actualTransportErrors = await harness.transportErrorCount()
                XCTAssertEqual(expectedTransportErrors, actualTransportErrors, name)

                if let halted = expected["translationPumpHalted"], SharedFixtures.flag(halted) {
                    let englishBefore = await harness.appendedFrameTexts(from: harness.englishTransport)
                    let japaneseBefore = await harness.appendedFrameTexts(from: harness.japaneseTransport)
                    try await harness.appendFrame("probeAfterHalt")
                    let englishAfter = await harness.appendedFrameTexts(from: harness.englishTransport)
                    let japaneseAfter = await harness.appendedFrameTexts(from: harness.japaneseTransport)
                    XCTAssertEqual(englishBefore, englishAfter, name)
                    XCTAssertEqual(japaneseBefore, japaneseAfter, name)
                }

                if let reconnect = expected["signalsSessionReconnect"] {
                    XCTAssertEqual(
                        SharedFixtures.flag(reconnect),
                        expectedTransportErrors > 0,
                        name
                    )
                }
            } catch {
                await harness.forceClose()
                throw error
            }
            await harness.forceClose()
        }
    }

    // Given: preroll 上限を超える連続 frame
    // When: 上限超過後に発話言語を確定させる
    // Then: 直近 40 frame だけが新しい target へ flush される
    func testRollingPrerollKeepsOnlyTheMostRecentFrames() async throws {
        let window = try XCTUnwrap(
            try SharedFixtures.load("routing")["prerollWindow"] as? [String: Any]
        )
        let appendCount = SharedFixtures.number(window["appendFrameCount"])
        let expectedCount = SharedFixtures.number(window["expectedFlushedFrameCount"])
        let firstIndex = SharedFixtures.number(window["expectedFirstFlushedFrameIndex"])
        let lastIndex = SharedFixtures.number(window["expectedLastFlushedFrameIndex"])

        let harness = try await RoutingHarness.start()
        for index in 0..<appendCount {
            try await harness.appendFrame("frame-\(index)")
        }
        try await harness.setSpokenLanguage(SharedFixtures.text(window["thenSetSpokenLanguage"]))

        let flushed = await harness.appendedFrameTexts(from: harness.englishTransport)
        XCTAssertEqual(expectedCount, flushed.count)
        XCTAssertEqual("frame-\(firstIndex)", flushed.first)
        XCTAssertEqual("frame-\(lastIndex)", flushed.last)
        XCTAssertEqual(DualRealtimeTranslationClient.translationPrerollFrameLimit, expectedCount)
        await harness.forceClose()
    }

    // Given: 呼び出し側が同じバッファを再利用する
    // When: Append 後にバッファを上書きしてから言語を確定する
    // Then: preroll flush は上書き前の内容を翻訳 lane へ届ける
    func testPrerollRetainsOwnedCopiesWhenCallerReusesBuffer() async throws {
        let harness = try await RoutingHarness.start()
        var buffer = Data("frame-original".utf8)
        try await harness.dual.appendAudioFrame(buffer)
        let mutated = Array(Data("frame-mutated!".utf8))
        for index in buffer.indices {
            buffer[index] = mutated[index]
        }

        try await harness.setSpokenLanguage("japanese")

        let flushed = await harness.appendedFrameTexts(from: harness.englishTransport)
        XCTAssertEqual(flushed, ["frame-original"])
        await harness.forceClose()
    }

    // Given: fixture の preroll / 連続失敗上限
    // When: 実装定数と突き合わせる
    // Then: 契約値と一致する
    func testConstantsMatchFixture() throws {
        let routing = try SharedFixtures.load("routing")
        XCTAssertEqual(
            SharedFixtures.number(routing["prerollFrameLimit"]),
            DualRealtimeTranslationClient.translationPrerollFrameLimit
        )
        XCTAssertEqual(
            SharedFixtures.number(routing["consecutiveTranslationFailureLimit"]),
            DualRealtimeTranslationClient.consecutiveTranslationFailureLimit
        )
    }

    private func frameNames(_ value: Any?) -> [String] {
        guard let array = value as? [Any] else { return [] }
        return array.map(SharedFixtures.text)
    }
}

private final class RoutingHarness: @unchecked Sendable {
    let sourceTransport: FakeRealtimeWebSocketTransport
    let englishTransport: FakeRealtimeWebSocketTransport
    let japaneseTransport: FakeRealtimeWebSocketTransport
    let dual: DualRealtimeTranslationClient

    private let transportErrors = OSAllocatedUnfairLock(initialState: 0)
    private var collector: Task<Void, Never>?
    private var selectedTarget: RealtimeTranslationOutputLanguage?

    private init(
        sourceTransport: FakeRealtimeWebSocketTransport,
        englishTransport: FakeRealtimeWebSocketTransport,
        japaneseTransport: FakeRealtimeWebSocketTransport,
        dual: DualRealtimeTranslationClient
    ) {
        self.sourceTransport = sourceTransport
        self.englishTransport = englishTransport
        self.japaneseTransport = japaneseTransport
        self.dual = dual
    }

    static func start() async throws -> RoutingHarness {
        let sourceTransport = FakeRealtimeWebSocketTransport()
        let englishTransport = FakeRealtimeWebSocketTransport()
        let japaneseTransport = FakeRealtimeWebSocketTransport()
        let dual = DualRealtimeTranslationClient(
            sourceConnection: RealtimeSourceTranscriptionConnection(
                transport: sourceTransport,
                safetyIdentifier: "test-safety",
                handshakeTimeoutNanoseconds: 1_000_000_000,
                closeTimeoutNanoseconds: 500_000_000
            ),
            englishConnection: RealtimeTranslationConnection(
                target: .english,
                transport: englishTransport,
                safetyIdentifier: "test-safety",
                sessionUpdateTimeoutNanoseconds: 1_000_000_000,
                closeTimeoutNanoseconds: 500_000_000
            ),
            japaneseConnection: RealtimeTranslationConnection(
                target: .japanese,
                transport: japaneseTransport,
                safetyIdentifier: "test-safety",
                sessionUpdateTimeoutNanoseconds: 1_000_000_000,
                closeTimeoutNanoseconds: 500_000_000
            )
        )

        try await startDual(
            dual,
            sourceTransport: sourceTransport,
            englishTransport: englishTransport,
            japaneseTransport: japaneseTransport
        )

        let harness = RoutingHarness(
            sourceTransport: sourceTransport,
            englishTransport: englishTransport,
            japaneseTransport: japaneseTransport,
            dual: dual
        )
        let stream = await dual.events
        harness.collector = Task {
            for await event in stream {
                if case .error(_, let code) = event.event, code == "transport" {
                    harness.transportErrors.withLock { $0 += 1 }
                }
            }
        }
        return harness
    }

    func apply(_ step: [String: Any]) async throws {
        switch SharedFixtures.text(step["kind"]) {
        case "appendFrame":
            try await appendFrame(SharedFixtures.text(step["frame"]))
        case "setSpokenLanguage":
            try await setSpokenLanguage(SharedFixtures.text(step["language"]))
        case "resetAudioRouting":
            await dual.resetAudioRouting()
        case "translationSendFailure":
            await targetTransport().failNextSendOnce()
        default:
            throw NSError(
                domain: "RoutingFixtureTests",
                code: 1,
                userInfo: [NSLocalizedDescriptionKey: "unknown routing step"]
            )
        }
    }

    func appendFrame(_ frameName: String) async throws {
        try await dual.appendAudioFrame(Data(frameName.utf8))
        try await dual.waitForTranslationDrain()
    }

    func setSpokenLanguage(_ language: String) async throws {
        let spoken: SpokenLanguage
        switch language {
        case "japanese":
            spoken = .japanese
        case "english":
            spoken = .english
        case "unknown":
            spoken = .unknown
        default:
            throw NSError(
                domain: "RoutingFixtureTests",
                code: 2,
                userInfo: [NSLocalizedDescriptionKey: "unknown spoken language \(language)"]
            )
        }

        switch spoken {
        case .japanese:
            selectedTarget = .english
        case .english:
            selectedTarget = .japanese
        case .unknown:
            break
        }

        try await dual.setSpokenLanguage(spoken)
        try await dual.waitForTranslationDrain()
    }

    func transportErrorCount() async -> Int {
        // ポンプの非同期 emit を少し待つ。
        try? await Task.sleep(nanoseconds: 80_000_000)
        return transportErrors.withLock { $0 }
    }

    func appendedFrameTexts(from transport: FakeRealtimeWebSocketTransport) async -> [String] {
        let sent = await transport.sent
        return sent.compactMap { data in
            guard let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                let type = object["type"] as? String,
                type == "input_audio_buffer.append"
                    || type == "session.input_audio_buffer.append",
                let audio = object["audio"] as? String,
                let decoded = Data(base64Encoded: audio),
                let label = String(data: decoded, encoding: .utf8)
            else {
                return nil
            }
            return label
        }
    }

    func forceClose() async {
        collector?.cancel()
        await dual.forceClose()
    }

    private func targetTransport() -> FakeRealtimeWebSocketTransport {
        selectedTarget == .japanese ? japaneseTransport : englishTransport
    }

    private static func startDual(
        _ dual: DualRealtimeTranslationClient,
        sourceTransport: FakeRealtimeWebSocketTransport,
        englishTransport: FakeRealtimeWebSocketTransport,
        japaneseTransport: FakeRealtimeWebSocketTransport
    ) async throws {
        try await sourceTransport.enqueueJSON(["type": "session.created"])
        try await englishTransport.enqueueJSON(["type": "session.created"])
        try await japaneseTransport.enqueueJSON(["type": "session.created"])

        let startTask = Task {
            try await dual.start(apiKey: "sk-test", tuning: .default)
        }

        try await waitUntilSent(sourceTransport, minimum: 1)
        try await waitUntilSent(englishTransport, minimum: 1)
        try await waitUntilSent(japaneseTransport, minimum: 1)
        try await sourceTransport.enqueueJSON(["type": "session.updated"])
        try await englishTransport.enqueueJSON(["type": "session.updated"])
        try await japaneseTransport.enqueueJSON(["type": "session.updated"])
        try await startTask.value
    }

    private static func waitUntilSent(
        _ transport: FakeRealtimeWebSocketTransport,
        minimum: Int,
        timeout: TimeInterval = 1.0
    ) async throws {
        let deadline = Date().addingTimeInterval(timeout)
        while Date() < deadline {
            if await transport.sent.count >= minimum { return }
            try await Task.sleep(nanoseconds: 10_000_000)
        }
        throw NSError(
            domain: "RoutingFixtureTests",
            code: 3,
            userInfo: [NSLocalizedDescriptionKey: "Timed out waiting for sent count \(minimum)"]
        )
    }
}
