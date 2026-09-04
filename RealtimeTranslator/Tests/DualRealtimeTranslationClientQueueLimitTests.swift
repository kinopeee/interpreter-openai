import Foundation
import XCTest
@testable import RealtimeTranslator

final class DualRealtimeTranslationClientQueueLimitTests: XCTestCase {
    // Given: 翻訳キュー契約 fixture と UI ローカライズ
    // When: キュー上限、preroll 上限、transport error を読み取る
    // Then: Swift 実装と共有契約が一致する
    func testQ01ConstantsMatchFixture() throws {
        let fixture = try SharedFixtures.load("translation-queue")
        let overflow = try XCTUnwrap(fixture["overflow"] as? [String: Any])
        XCTAssertEqual(
            SharedFixtures.number(fixture["pendingFrameLimit"]),
            DualRealtimeTranslationClient.translationPendingFrameLimit
        )
        XCTAssertEqual(
            SharedFixtures.number(fixture["prerollFrameLimit"]),
            DualRealtimeTranslationClient.translationPrerollFrameLimit
        )
        XCTAssertEqual(SharedFixtures.text(overflow["errorCode"]), "transport")
        XCTAssertEqual(
            UiCopy.text("error.translationBacklog"),
            "翻訳音声の送信待ちが上限に達しました。"
        )
    }

    // Given: 起動済みで target 未選択の client
    // When: 音声 frame を追加せず少し待つ
    // Then: 翻訳送信、transport error、pending はすべて 0 になる
    func testQ02NoTargetHasNoTranslationSends() async throws {
        let harness = try await QueueHarness.start()
        try await Task.sleep(nanoseconds: 20_000_000)
        let translationCount = await harness.englishAppendCount()
        let errorCount = await harness.transportErrorCount()
        let pendingCount = await harness.dual.pendingTranslationFrameCount
        XCTAssertEqual(translationCount, 0)
        XCTAssertEqual(errorCount, 0)
        XCTAssertEqual(pendingCount, 0)
        await harness.forceClose()
    }

    // Given: in-flight frame と共有 fixture の境界値
    // When: 境界を 1 frame ずつ追加する
    // Then: pending、停止状態、error 数が fixture と一致する
    func testQ02ThroughQ04BoundariesMatchFixture() async throws {
        let fixture = try SharedFixtures.load("translation-queue")
        let boundaries = try XCTUnwrap(fixture["boundaries"] as? [[String: Any]])
        for boundary in boundaries {
            let pendingBefore = SharedFixtures.number(boundary["pendingBefore"])
            let expectedPending = SharedFixtures.number(boundary["expectedPending"])
            let expectedErrors = SharedFixtures.number(boundary["expectedTransportErrorCount"])
            let expectedHalted = SharedFixtures.flag(boundary["expectedHalted"])
            let harness = try await QueueHarness.start()
            try await harness.select(.english)
            await harness.english.setHoldAudioAppends(true)
            try await harness.append(seed: 1)
            try await waitUntil { await harness.english.heldAudioAppendCount == 1 }
            for index in 0..<pendingBefore {
                try await harness.append(seed: UInt8(index & 0xff))
            }
            try await harness.append(seed: 0xfe)

            if expectedHalted {
                try await waitUntil { await harness.transportErrorCount() == expectedErrors }
            }
            let actualPending = await harness.dual.pendingTranslationFrameCount
            let actualHalted = await harness.dual.isTranslationPumpHalted
            let actualErrors = await harness.transportErrorCount()
            let sourceCount = await harness.sourceAppendCount()
            XCTAssertEqual(actualPending, expectedPending)
            XCTAssertEqual(actualHalted, expectedHalted)
            XCTAssertEqual(actualErrors, expectedErrors)
            XCTAssertEqual(sourceCount, pendingBefore + 2)
            if expectedHalted {
                let errors = await harness.transportErrors()
                let error = try XCTUnwrap(errors.first)
                let epoch = await harness.dual.connectionEpoch
                XCTAssertEqual(error.epoch, epoch)
                XCTAssertEqual(error.lane, .translation(.english))
                guard case .error(let message, let code) = error.event else {
                    XCTFail("expected transport error")
                    await harness.forceClose()
                    continue
                }
                XCTAssertEqual(message, UiCopy.text("error.translationBacklog"))
                XCTAssertEqual(code, "transport")
            }
            await harness.english.setHoldAudioAppends(false)
            await harness.english.releaseAllAudioAppends()
            try await harness.dual.waitForTranslationDrain()
            let sendCount = await harness.englishAppendCount()
            XCTAssertEqual(sendCount, expectedHalted ? 1 : pendingBefore + 2)
            await harness.forceClose()
        }
    }

    // Given: fixture が指定する 40 frame の rolling preroll
    // When: target を en、ja、es の順に選択する
    // Then: preroll は選択 target だけへ順序を保って flush される
    func testQ05PrerollFlushesToEachFixtureTarget() async throws {
        let fixture = try SharedFixtures.load("translation-queue")
        let preroll = try XCTUnwrap(fixture["prerollFlush"] as? [String: Any])
        let count = SharedFixtures.number(preroll["frameCount"])
        let targets = try XCTUnwrap(preroll["targets"] as? [String])
        for rawTarget in targets {
            let target = try XCTUnwrap(RealtimeTranslationOutputLanguage(rawValue: rawTarget))
            let pair: LanguagePair = target == .spanish ? .jaEs : .jaEn
            let harness = try await QueueHarness.start(pair: pair)
            let expectedPayloads = (0..<count).map {
                frame(seed: UInt8($0 & 0xff)).base64EncodedString()
            }
            for index in 0..<count {
                try await harness.append(seed: UInt8(index & 0xff))
            }
            try await harness.select(target)
            let selectedCount = await harness.appendCount(for: target)
            let selectedPayloads = try await harness.appendPayloads(for: target)
            XCTAssertEqual(selectedCount, count)
            XCTAssertEqual(selectedPayloads, expectedPayloads)
            for other in [RealtimeTranslationOutputLanguage.english, .japanese, .spanish]
            where other != target {
                let otherCount = await harness.appendCount(for: other)
                XCTAssertEqual(otherCount, 0)
            }
            let errors = await harness.transportErrorCount()
            let pending = await harness.dual.pendingTranslationFrameCount
            XCTAssertEqual(errors, 0)
            XCTAssertEqual(pending, 0)
            await harness.forceClose()
        }
    }

    // Given: 翻訳ポンプが backlog overflow で停止した client
    // When: overflow 後も fixture の frame 数を source へ追加する
    // Then: source は継続し、翻訳送信と error 数は増えない
    func testQ06AfterOverflowSourceContinues() async throws {
        let harness = try await QueueHarness.start()
        try await harness.overflow()
        let before = await harness.englishAppendCount()
        let errors = await harness.transportErrorCount()
        for index in 0..<5 {
            try await harness.append(seed: UInt8(0xa0 + index))
        }
        let after = await harness.englishAppendCount()
        let afterErrors = await harness.transportErrorCount()
        let sourceCount = await harness.sourceAppendCount()
        let pending = await harness.dual.pendingTranslationFrameCount
        XCTAssertEqual(after, before)
        XCTAssertEqual(afterErrors, errors)
        XCTAssertEqual(sourceCount, 87)
        XCTAssertEqual(pending, 0)
        await harness.forceClose()
    }

    // Given: overflow により翻訳ポンプが停止した client
    // When: 別 target へ変更する
    // Then: 停止したポンプは再開しない
    func testQ07TargetChangeDoesNotResumeAfterOverflow() async throws {
        let harness = try await QueueHarness.start()
        try await harness.overflow()
        try await harness.select(.japanese)
        let japaneseCount = await harness.japaneseAppendCount()
        let halted = await harness.dual.isTranslationPumpHalted
        let errors = await harness.transportErrorCount()
        XCTAssertEqual(japaneseCount, 0)
        XCTAssertTrue(halted)
        XCTAssertEqual(errors, 1)
        await harness.forceClose()
    }

    // Given: overflow により翻訳ポンプが停止した client
    // When: routing reset 後に同じ target を選ぶ
    // Then: 停止したポンプは再開しない
    func testQ08RoutingResetDoesNotResumeAfterOverflow() async throws {
        let harness = try await QueueHarness.start()
        try await harness.overflow()
        let englishCountBefore = await harness.englishAppendCount()
        await harness.dual.resetAudioRouting()
        try await harness.select(.english)
        let englishCount = await harness.englishAppendCount()
        let halted = await harness.dual.isTranslationPumpHalted
        let errors = await harness.transportErrorCount()
        XCTAssertEqual(englishCount, englishCountBefore)
        XCTAssertLessThanOrEqual(englishCount, 1)
        XCTAssertTrue(halted)
        XCTAssertEqual(errors, 1)
        await harness.forceClose()
    }

    // Given: in-flight append を hold した状態で overflow した client
    // When: in-flight append を明示的に失敗させる
    // Then: backlog error は重複せず、send failure は追加されない
    func testQ09InFlightFailureDoesNotDuplicateOverflowError() async throws {
        let harness = try await QueueHarness.start()
        try await harness.overflow()
        let failed = await harness.english.failOneHeldAudioAppend()
        XCTAssertTrue(failed)
        try await waitUntil { await harness.english.heldAudioAppendCount == 0 }
        try await waitUntil { await harness.transportErrorCount() == 1 }
        let errors = await harness.transportErrorCount()
        let halted = await harness.dual.isTranslationPumpHalted
        let transportErrors = await harness.transportErrors()
        let error = try XCTUnwrap(transportErrors.first)
        let epoch = await harness.dual.connectionEpoch
        XCTAssertEqual(errors, 1)
        XCTAssertTrue(halted)
        XCTAssertEqual(error.epoch, epoch)
        guard case .error(let message, let code) = error.event else {
            XCTFail("expected transport error")
            await harness.forceClose()
            return
        }
        XCTAssertEqual(message, UiCopy.text("error.translationBacklog"))
        XCTAssertEqual(code, "transport")
        await harness.forceClose()
    }

    // Given: overflow により in-flight frame だけが保持された client
    // When: in-flight frame を完了させて後続 frame を追加する
    // Then: 後続 frame は翻訳送信されない
    func testQ10CompletedInFlightFrameDoesNotResumePump() async throws {
        let harness = try await QueueHarness.start()
        try await harness.overflow()
        let released = await harness.english.releaseOneAudioAppend()
        XCTAssertTrue(released)
        try await harness.dual.waitForTranslationDrain()
        let sent = await harness.englishAppendCount()
        try await harness.append(seed: 0xaa)
        let after = await harness.englishAppendCount()
        XCTAssertEqual(after, sent)
        await harness.forceClose()
    }

    // Given: overflow 後で close 応答を返す transport
    // When: hold を解除して closeGracefully を呼ぶ
    // Then: 5 秒以内に完了し、追加翻訳送信は発生しない
    func testQ11GracefulCloseCompletesAfterOverflow() async throws {
        let harness = try await QueueHarness.start(
            autoCloseResponses: true,
            translationDrainTimeoutNanoseconds: 100_000_000,
            closeTimeoutNanoseconds: 500_000_000
        )
        try await harness.overflow()
        await harness.english.setHoldAudioAppends(false)
        await harness.english.releaseAllAudioAppends()
        try await harness.dual.waitForTranslationDrain()
        let before = await harness.englishAppendCount()
        let started = Date()
        _ = await harness.dual.closeGracefully()
        let elapsed = Date().timeIntervalSince(started)
        let after = await harness.englishAppendCount()
        XCTAssertLessThan(elapsed, 5)
        XCTAssertEqual(after, before)
    }

    // Given: overflow により停止した client
    // When: 新しい start を実行して target と frame を追加する
    // Then: 新しい epoch では翻訳送信が復旧する
    func testQ12StartAgainRecoversTranslation() async throws {
        let harness = try await QueueHarness.start()
        try await harness.overflow()
        await harness.english.setHoldAudioAppends(false)
        await harness.english.releaseAllAudioAppends()
        try await QueueHarness.startDual(
            harness.dual,
            sourceTransport: harness.source,
            englishTransport: harness.english,
            japaneseTransport: harness.japanese,
            spanishTransport: harness.spanish,
            pair: .jaEn
        )
        try await harness.select(.english)
        try await harness.append(seed: 0xab)
        let count = await harness.englishAppendCount()
        let halted = await harness.dual.isTranslationPumpHalted
        let pending = await harness.dual.pendingTranslationFrameCount
        XCTAssertGreaterThanOrEqual(count, 1)
        XCTAssertFalse(halted)
        XCTAssertEqual(pending, 0)
        await harness.forceClose()
    }

    // Given: English の in-flight 送信を hold した状態
    // When: forceClose 後に start し、古い continuation を解放する
    // Then: 新しい pump が追跡され、送信と drain が一度ずつ成功する
    func testForceCloseDoesNotClearNewPumpAfterRestart() async throws {
        let harness = try await QueueHarness.start()
        try await harness.select(.english)
        await harness.english.setHoldAudioAppends(true)
        try await harness.append(seed: 0xad)
        try await waitUntil {
            await harness.english.heldAudioAppendCount == 1
        }

        await harness.dual.forceClose()
        await harness.english.setHoldAudioAppends(false)
        try await QueueHarness.startDual(
            harness.dual,
            sourceTransport: harness.source,
            englishTransport: harness.english,
            japaneseTransport: harness.japanese,
            spanishTransport: harness.spanish,
            pair: .jaEn
        )
        try await harness.select(.english)
        try await harness.append(seed: 0xae)
        let sentAfterRestart = await harness.englishAppendCount()
        XCTAssertEqual(sentAfterRestart, 1)

        await harness.english.releaseAllAudioAppends()
        let sentBeforeFollowUp = await harness.englishAppendCount()
        try await harness.append(seed: 0xaf)
        let sentAfterFollowUp = await harness.englishAppendCount()
        XCTAssertEqual(sentAfterFollowUp, sentBeforeFollowUp + 1)
        try await harness.dual.waitForTranslationDrain()
        let errors = await harness.transportErrorCount()
        XCTAssertEqual(errors, 0)
        await harness.forceClose()
    }

    // Given: hold 中の transport と共有 fixture の post-overflow append 数
    // When: 2 frame ごとに 1 append を解放して最大 400 frame 追加する
    // Then: queue は上限で停止し、新しい start で再び送信できる
    func testQ14IntermittentReleaseStillBoundsQueue() async throws {
        let harness = try await QueueHarness.start()
        try await harness.select(.english)
        await harness.english.setHoldAudioAppends(true)
        var appended = 0
        while await harness.transportErrorCount() == 0, appended < 400 {
            try await harness.append(seed: UInt8(appended & 0xff))
            appended += 1
            if appended.isMultiple(of: 2) {
                _ = await harness.english.releaseOneAudioAppend()
            }
        }
        let errors = await harness.transportErrorCount()
        let halted = await harness.dual.isTranslationPumpHalted
        let pending = await harness.dual.pendingTranslationFrameCount
        let sent = await harness.englishAppendCount()
        XCTAssertEqual(errors, 1)
        XCTAssertTrue(halted)
        XCTAssertEqual(pending, 0)
        XCTAssertLessThan(sent, appended)
        await harness.english.setHoldAudioAppends(false)
        await harness.english.releaseAllAudioAppends()
        try await QueueHarness.startDual(
            harness.dual,
            sourceTransport: harness.source,
            englishTransport: harness.english,
            japaneseTransport: harness.japanese,
            spanishTransport: harness.spanish,
            pair: .jaEn
        )
        try await harness.select(.english)
        try await harness.append(seed: 0xac)
        let recovered = await harness.englishAppendCount()
        XCTAssertGreaterThanOrEqual(recovered, 1)
        await harness.forceClose()
    }

    // Given: overflow 後も旧 epoch の in-flight send が残ったまま再 start する
    // When: 新ポンプを動かしたあと、旧 send を成功または失敗で完了させる
    // Then: 新ポンプの追跡は残り、drain は旧エピローグで空にならない
    func testQ15StalePumpEpilogueDoesNotDropNewPump() async throws {
        try await assertStalePumpEpilogueKeepsNewPump(failStaleSend: false)
        try await assertStalePumpEpilogueKeepsNewPump(failStaleSend: true)
    }

    private func assertStalePumpEpilogueKeepsNewPump(failStaleSend: Bool) async throws {
        let harness = try await QueueHarness.start()
        await harness.english.setPersistHeldAudioAppendsAcrossCancel(true)
        try await harness.select(.english)
        await harness.english.setHoldAudioAppends(true)
        try await harness.append(seed: 0x01)
        try await waitUntil { await harness.english.heldAudioAppendCount == 1 }
        for index in 0..<81 {
            try await harness.append(seed: UInt8(index & 0xff))
        }
        try await waitUntil { await harness.dual.isTranslationPumpHalted }
        try await waitUntil { await harness.transportErrorCount() == 1 }

        try await QueueHarness.startDual(
            harness.dual,
            sourceTransport: harness.source,
            englishTransport: harness.english,
            japaneseTransport: harness.japanese,
            spanishTransport: harness.spanish,
            pair: .jaEn
        )
        try await harness.select(.english)
        try await harness.append(seed: 0xb0)
        try await waitUntil { await harness.english.heldAudioAppendCount == 2 }
        try await harness.append(seed: 0xb1)
        try await harness.append(seed: 0xb2)
        let trackedBeforeStaleCompletion = await harness.dual.isTranslationPumpTracked
        let pendingBeforeStaleCompletion = await harness.dual.pendingTranslationFrameCount
        XCTAssertTrue(trackedBeforeStaleCompletion)
        XCTAssertEqual(pendingBeforeStaleCompletion, 2)

        if failStaleSend {
            XCTAssertTrue(await harness.english.failOneHeldAudioAppend())
        } else {
            XCTAssertTrue(await harness.english.releaseOneAudioAppend())
        }
        try await Task.sleep(nanoseconds: 50_000_000)
        let trackedAfterStaleCompletion = await harness.dual.isTranslationPumpTracked
        let heldAfterStaleCompletion = await harness.english.heldAudioAppendCount
        XCTAssertTrue(trackedAfterStaleCompletion)
        XCTAssertEqual(heldAfterStaleCompletion, 1)
        do {
            try await harness.dual.waitForTranslationDrain(timeoutNanoseconds: 80_000_000)
            XCTFail("live pump is still in-flight")
        } catch {
            // 新ポンプが追跡されていれば、短い drain は timeout する。
        }

        await harness.english.setHoldAudioAppends(false)
        await harness.english.releaseAllAudioAppends()
        try await harness.dual.waitForTranslationDrain()
        try await harness.append(seed: 0xb3)
        try await harness.dual.waitForTranslationDrain()
        let payloads = try await harness.appendPayloads(for: .english)
        let recovered = DualRealtimeTranslationClientQueueLimitTests.frame(seed: 0xb3)
            .base64EncodedString()
        XCTAssertEqual(payloads.filter { $0 == recovered }.count, 1)
        let pending = await harness.dual.pendingTranslationFrameCount
        let halted = await harness.dual.isTranslationPumpHalted
        XCTAssertEqual(pending, 0)
        XCTAssertFalse(halted)
        await harness.forceClose()
    }

    private static func frame(seed: UInt8) -> Data {
        Data(repeating: seed, count: PCM16FramePacketizer.bytesPerFrame)
    }

    private func waitUntil(
        timeout: TimeInterval = 5,
        condition: @escaping @Sendable () async -> Bool
    ) async throws {
        let deadline = Date().addingTimeInterval(timeout)
        while Date() < deadline {
            if await condition() {
                return
            }
            try await Task.sleep(nanoseconds: 10_000_000)
        }
        XCTFail("condition timed out")
    }

    private final class QueueHarness: @unchecked Sendable {
        let source: FakeRealtimeWebSocketTransport
        let english: FakeRealtimeWebSocketTransport
        let japanese: FakeRealtimeWebSocketTransport
        let spanish: FakeRealtimeWebSocketTransport
        let dual: DualRealtimeTranslationClient

        private let eventCollector: QueueEventCollector
        private var collectorTask: Task<Void, Never>?

        private init(
            source: FakeRealtimeWebSocketTransport,
            english: FakeRealtimeWebSocketTransport,
            japanese: FakeRealtimeWebSocketTransport,
            spanish: FakeRealtimeWebSocketTransport,
            dual: DualRealtimeTranslationClient,
            eventCollector: QueueEventCollector
        ) {
            self.source = source
            self.english = english
            self.japanese = japanese
            self.spanish = spanish
            self.dual = dual
            self.eventCollector = eventCollector
        }

        static func start(
            pair: LanguagePair = .jaEn,
            autoCloseResponses: Bool = false,
            translationDrainTimeoutNanoseconds: UInt64 = DualRealtimeTranslationClient
                .defaultTranslationDrainTimeoutNanoseconds,
            closeTimeoutNanoseconds: UInt64 = 500_000_000
        ) async throws -> QueueHarness {
            let source = FakeRealtimeWebSocketTransport()
            let english = FakeRealtimeWebSocketTransport()
            let japanese = FakeRealtimeWebSocketTransport()
            let spanish = FakeRealtimeWebSocketTransport()
            await source.setAutoCloseResponses(autoCloseResponses)
            await english.setAutoCloseResponses(autoCloseResponses)
            await japanese.setAutoCloseResponses(autoCloseResponses)
            await spanish.setAutoCloseResponses(autoCloseResponses)
            let dual = makeDual(
                source: source,
                english: english,
                japanese: japanese,
                spanish: spanish,
                translationDrainTimeoutNanoseconds: translationDrainTimeoutNanoseconds,
                closeTimeoutNanoseconds: closeTimeoutNanoseconds
            )
            try await startDual(
                dual,
                sourceTransport: source,
                englishTransport: english,
                japaneseTransport: japanese,
                spanishTransport: spanish,
                pair: pair
            )
            let collector = QueueEventCollector()
            let stream = await dual.events
            let task = Task {
                for await event in stream {
                    await collector.append(event)
                }
            }
            let harness = QueueHarness(
                source: source,
                english: english,
                japanese: japanese,
                spanish: spanish,
                dual: dual,
                eventCollector: collector
            )
            harness.collectorTask = task
            return harness
        }

        private static func makeDual(
            source: FakeRealtimeWebSocketTransport,
            english: FakeRealtimeWebSocketTransport,
            japanese: FakeRealtimeWebSocketTransport,
            spanish: FakeRealtimeWebSocketTransport,
            translationDrainTimeoutNanoseconds: UInt64 = DualRealtimeTranslationClient
                .defaultTranslationDrainTimeoutNanoseconds,
            closeTimeoutNanoseconds: UInt64 = 500_000_000
        ) -> DualRealtimeTranslationClient {
            DualRealtimeTranslationClient(
                sourceConnection: RealtimeSourceTranscriptionConnection(
                    transport: source,
                    safetyIdentifier: "test-safety",
                    handshakeTimeoutNanoseconds: 1_000_000_000,
                    closeTimeoutNanoseconds: closeTimeoutNanoseconds
                ),
                englishConnection: RealtimeTranslationConnection(
                    target: .english,
                    transport: english,
                    safetyIdentifier: "test-safety",
                    sessionUpdateTimeoutNanoseconds: 1_000_000_000,
                    closeTimeoutNanoseconds: closeTimeoutNanoseconds
                ),
                japaneseConnection: RealtimeTranslationConnection(
                    target: .japanese,
                    transport: japanese,
                    safetyIdentifier: "test-safety",
                    sessionUpdateTimeoutNanoseconds: 1_000_000_000,
                    closeTimeoutNanoseconds: closeTimeoutNanoseconds
                ),
                spanishConnection: RealtimeTranslationConnection(
                    target: .spanish,
                    transport: spanish,
                    safetyIdentifier: "test-safety",
                    sessionUpdateTimeoutNanoseconds: 1_000_000_000,
                    closeTimeoutNanoseconds: closeTimeoutNanoseconds
                ),
                translationDrainTimeoutNanoseconds: translationDrainTimeoutNanoseconds
            )
        }

        static func startDual(
            _ dual: DualRealtimeTranslationClient,
            sourceTransport: FakeRealtimeWebSocketTransport,
            englishTransport: FakeRealtimeWebSocketTransport,
            japaneseTransport: FakeRealtimeWebSocketTransport,
            spanishTransport: FakeRealtimeWebSocketTransport,
            pair: LanguagePair
        ) async throws {
            let sourceSentBefore = await sourceTransport.sent.count
            let englishSentBefore = await englishTransport.sent.count
            let japaneseSentBefore = await japaneseTransport.sent.count
            let spanishSentBefore = await spanishTransport.sent.count
            try await sourceTransport.enqueueJSON(["type": "session.created"])
            try await englishTransport.enqueueJSON(["type": "session.created"])
            try await japaneseTransport.enqueueJSON(["type": "session.created"])
            try await spanishTransport.enqueueJSON(["type": "session.created"])
            let startTask = Task {
                try await dual.start(apiKey: "sk-test", tuning: .default, pair: pair)
            }
            try await waitUntilSent(sourceTransport, minimum: sourceSentBefore + 1)
            let targets = Set(pair.languages.compactMap { pair.translationTarget(for: $0) })
            if targets.contains(.english) {
                try await waitUntilSent(englishTransport, minimum: englishSentBefore + 1)
            }
            if targets.contains(.japanese) {
                try await waitUntilSent(japaneseTransport, minimum: japaneseSentBefore + 1)
            }
            if targets.contains(.spanish) {
                try await waitUntilSent(spanishTransport, minimum: spanishSentBefore + 1)
            }
            try await sourceTransport.enqueueJSON(["type": "session.updated"])
            if targets.contains(.english) {
                try await englishTransport.enqueueJSON(["type": "session.updated"])
            }
            if targets.contains(.japanese) {
                try await japaneseTransport.enqueueJSON(["type": "session.updated"])
            }
            if targets.contains(.spanish) {
                try await spanishTransport.enqueueJSON(["type": "session.updated"])
            }
            try await startTask.value
        }

        func select(_ target: RealtimeTranslationOutputLanguage) async throws {
            try await dual.selectTranslationTarget(target)
            if !(await isHolding()) {
                try await dual.waitForTranslationDrain()
            }
        }

        func append(seed: UInt8) async throws {
            try await dual.appendAudioFrame(
                DualRealtimeTranslationClientQueueLimitTests.frame(seed: seed)
            )
            if !(await isHolding()) {
                try await dual.waitForTranslationDrain()
            }
        }

        func overflow() async throws {
            try await select(.english)
            await english.setHoldAudioAppends(true)
            try await append(seed: 1)
            try await Self.waitUntil { await self.english.heldAudioAppendCount == 1 }
            for index in 0..<81 {
                try await append(seed: UInt8(index & 0xff))
            }
            try await Self.waitUntil { await self.dual.isTranslationPumpHalted }
            try await Self.waitUntil { await self.transportErrorCount() == 1 }
        }

        func sourceAppendCount() async -> Int {
            await Self.appendCount(source)
        }

        func englishAppendCount() async -> Int {
            await Self.appendCount(english)
        }

        func japaneseAppendCount() async -> Int {
            await Self.appendCount(japanese)
        }

        func appendCount(for target: RealtimeTranslationOutputLanguage) async -> Int {
            switch target {
            case .english: return await englishAppendCount()
            case .japanese: return await japaneseAppendCount()
            case .spanish: return await Self.appendCount(spanish)
            }
        }

        func appendPayloads(
            for target: RealtimeTranslationOutputLanguage
        ) async throws -> [String] {
            let transport: FakeRealtimeWebSocketTransport
            switch target {
            case .english: transport = english
            case .japanese: transport = japanese
            case .spanish: transport = spanish
            }
            let sent = await transport.sent
            return try sent.compactMap { data in
                guard
                    let object = try XCTUnwrap(
                        JSONSerialization.jsonObject(with: data) as? [String: Any]
                    ),
                    let type = object["type"] as? String,
                    type == "session.input_audio_buffer.append"
                        || type == "input_audio_buffer.append"
                else {
                    return nil
                }
                return object["audio"] as? String
            }
        }

        func transportErrors() async -> [RealtimeTranslationStreamEvent] {
            await eventCollector.transportErrors()
        }

        func transportErrorCount() async -> Int {
            let errors = await eventCollector.transportErrors()
            return errors.count
        }

        func forceClose() async {
            collectorTask?.cancel()
            await source.releaseAllAudioAppends()
            await english.releaseAllAudioAppends()
            await japanese.releaseAllAudioAppends()
            await spanish.releaseAllAudioAppends()
            await dual.forceClose()
        }

        private func isHolding() async -> Bool {
            let englishHolding = await english.holdAudioAppends
            let japaneseHolding = await japanese.holdAudioAppends
            let spanishHolding = await spanish.holdAudioAppends
            return englishHolding || japaneseHolding || spanishHolding
        }

        private static func waitUntil(
            timeout: TimeInterval = 5,
            condition: @escaping @Sendable () async -> Bool
        ) async throws {
            let deadline = Date().addingTimeInterval(timeout)
            while Date() < deadline {
                if await condition() {
                    return
                }
                try await Task.sleep(nanoseconds: 10_000_000)
            }
            XCTFail("condition timed out")
        }

        private static func appendCount(_ transport: FakeRealtimeWebSocketTransport) async -> Int {
            let sent = await transport.sent
            return sent.filter { data in
                guard
                    let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                    let type = object["type"] as? String
                else {
                    return false
                }
                return type == "input_audio_buffer.append"
                    || type == "session.input_audio_buffer.append"
            }.count
        }

        private static func waitUntilSent(
            _ transport: FakeRealtimeWebSocketTransport,
            minimum: Int,
            timeout: TimeInterval = 5
        ) async throws {
            let deadline = Date().addingTimeInterval(timeout)
            while Date() < deadline {
                if await transport.sent.count >= minimum {
                    return
                }
                try await Task.sleep(nanoseconds: 10_000_000)
            }
            XCTFail("timed out waiting for sent count")
        }
    }
}

private actor QueueEventCollector {
    private var events: [RealtimeTranslationStreamEvent] = []

    func append(_ event: RealtimeTranslationStreamEvent) {
        events.append(event)
    }

    func transportErrors() -> [RealtimeTranslationStreamEvent] {
        events.filter {
            guard case .error(_, let code) = $0.event else { return false }
            return code == "transport"
        }
    }
}
