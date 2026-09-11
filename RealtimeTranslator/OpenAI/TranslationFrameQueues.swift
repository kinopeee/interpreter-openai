import Foundation

/// 言語判定の遅延を吸収する rolling preroll と、翻訳送信待ちの pending frame 行列。
/// `DualRealtimeTranslationClient` の actor 隔離下だけで触る値型コンポーネント。
struct TranslationFrameQueues {
    private(set) var prerollFrames: [Data] = []
    private(set) var pendingFrames: [(Data, RealtimeTranslationOutputLanguage)] = []
    private let prerollLimit: Int
    private let pendingLimit: Int

    init(prerollLimit: Int, pendingLimit: Int) {
        self.prerollLimit = prerollLimit
        self.pendingLimit = pendingLimit
    }

    var pendingCount: Int {
        pendingFrames.count
    }

    var hasCapacityForPending: Bool {
        pendingFrames.count < pendingLimit
    }

    mutating func appendPreroll(_ pcm16LE: Data) {
        prerollFrames.append(pcm16LE)
        if prerollFrames.count > prerollLimit {
            prerollFrames.removeFirst(prerollFrames.count - prerollLimit)
        }
    }

    /// 上限判定は呼び出し側（ポンプ停止の副作用を伴うため）。
    mutating func enqueuePending(
        _ pcm16LE: Data,
        target: RealtimeTranslationOutputLanguage
    ) {
        pendingFrames.append((pcm16LE, target))
    }

    mutating func popPending() -> (Data, RealtimeTranslationOutputLanguage)? {
        guard !pendingFrames.isEmpty else { return nil }
        return pendingFrames.removeFirst()
    }

    mutating func clearPending() {
        pendingFrames.removeAll(keepingCapacity: true)
    }

    mutating func clearAll() {
        prerollFrames.removeAll(keepingCapacity: true)
        pendingFrames.removeAll(keepingCapacity: true)
    }
}
