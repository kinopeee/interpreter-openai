using System;

namespace RealtimeTranslator.Core.Realtime;

/// <summary>
/// `DualRealtimeTranslationClient` の送信キュー・停止時 drain のチューニング正本。
/// 容量値は `shared/fixtures/v1/routing.json`（preroll / 連続失敗上限）、
/// `shared/fixtures/v1/translation-queue.json`（pending frame 上限）と対応する。
/// </summary>
public sealed record DualRealtimeTranslationClientTuning
{
    /// <summary>100 ms frame × 40 = 直近 4 秒。言語判定の遅れがあっても発話冒頭を翻訳へ届ける。</summary>
    public const int DefaultPrerollFrameLimit = 40;

    public const int DefaultPendingFrameLimit = 80;

    public const int DefaultConsecutiveFailureLimit = 3;

    /// <summary>停止時 drain で未送信 frame 1 枚あたりに足す予算。preroll flush 後の短い停滞で訳文を落とさない。</summary>
    public const int DefaultDrainTimeoutMillisecondsPerPendingFrame = 250;

    /// <summary>停止時 drain の上限。Send 停滞でも Stop が無期限待ちしない。</summary>
    public static readonly TimeSpan DefaultDrainTimeoutCap = TimeSpan.FromSeconds(30);

    /// <summary>既定 5 秒。送信停滞でも CloseGracefully が session.close へ進める上限。</summary>
    public static readonly TimeSpan DefaultDrainTimeout = TimeSpan.FromSeconds(5);

    public int PrerollFrameLimit { get; init; } = DefaultPrerollFrameLimit;

    public int PendingFrameLimit { get; init; } = DefaultPendingFrameLimit;

    public int ConsecutiveFailureLimit { get; init; } = DefaultConsecutiveFailureLimit;

    public int DrainTimeoutMillisecondsPerPendingFrame { get; init; } =
        DefaultDrainTimeoutMillisecondsPerPendingFrame;

    public TimeSpan DrainTimeoutCap { get; init; } = DefaultDrainTimeoutCap;

    public TimeSpan DefaultCloseDrainTimeout { get; init; } = DefaultDrainTimeout;

    public static DualRealtimeTranslationClientTuning Default { get; } = new();

    /// <summary>
    /// 停止時 drain 予算。base（既定5秒）に未送信 frame 分を足し、cap（30秒）で打ち切る。
    /// テストが短い base を注入しているときはその base を下限・基準にする。
    /// </summary>
    public TimeSpan ResolveDrainTimeout(TimeSpan baseTimeout, int pendingFrameCount)
    {
        var baseMs = Math.Max(0, baseTimeout.TotalMilliseconds);
        var pending = Math.Max(0, pendingFrameCount);
        var scaledMs = baseMs + (pending * (double)DrainTimeoutMillisecondsPerPendingFrame);
        var capMs = Math.Max(baseMs, DrainTimeoutCap.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(Math.Clamp(scaledMs, baseMs, capMs));
    }
}
