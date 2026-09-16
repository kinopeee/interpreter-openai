using System;
using RealtimeTranslator.Core.OpenAI;
using RealtimeTranslator.Core.Realtime;
using Xunit;

namespace RealtimeTranslator.Core.Tests;

public sealed class DualRealtimeTranslationClientTuningTests
{
    // Given: 既定 tuning
    // When: 抽出コンポーネントと Dual を組み立てる
    // Then: 検証を通り、既定容量がそのまま使える
    [Fact]
    public void DefaultTuningIsValid()
    {
        DualRealtimeTranslationClientTuning.Default.EnsureValid();
        var queues = new TranslationFrameQueues(DualRealtimeTranslationClientTuning.Default);
        Assert.Equal(0, queues.PendingCount);
        Assert.Empty(queues.PrerollFrames);
        _ = new TranslationPumpSupervisor(DualRealtimeTranslationClientTuning.Default);
        using var dual = CreateDual();
    }

    // Given: 負の preroll 上限
    // When: TranslationFrameQueues を組み立てる
    // Then: ArgumentOutOfRangeException になり、空 dequeue には到達しない
    [Fact]
    public void NegativePrerollLimitIsRejectedBeforeAppend()
    {
        var tuning = DualRealtimeTranslationClientTuning.Default with { PrerollFrameLimit = -1 };
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => new TranslationFrameQueues(tuning));
        Assert.Equal(nameof(DualRealtimeTranslationClientTuning.PrerollFrameLimit), error.ParamName);
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateDual(tuning));
    }

    // Given: preroll 上限 0
    // When: frame を積む
    // Then: 保持せず、例外にもならない
    [Fact]
    public void ZeroPrerollLimitDisablesRetention()
    {
        var queues = new TranslationFrameQueues(
            DualRealtimeTranslationClientTuning.Default with { PrerollFrameLimit = 0 });
        queues.AppendPreroll(new byte[] { 0x11 });
        Assert.Empty(queues.PrerollFrames);
    }

    // Given: 負の pending / 連続失敗 / drain 予算
    // When: 抽出コンポーネントへ渡す
    // Then: いずれも ArgumentOutOfRangeException になる
    [Theory]
    [InlineData(-1, 80, 3, 250)]
    [InlineData(40, -1, 3, 250)]
    [InlineData(40, 80, -1, 250)]
    [InlineData(40, 80, 3, -1)]
    public void NegativeCapacityAndBudgetAreRejected(
        int preroll,
        int pending,
        int failures,
        int drainMs)
    {
        var tuning = DualRealtimeTranslationClientTuning.Default with
        {
            PrerollFrameLimit = preroll,
            PendingFrameLimit = pending,
            ConsecutiveFailureLimit = failures,
            DrainTimeoutMillisecondsPerPendingFrame = drainMs,
        };
        Assert.Throws<ArgumentOutOfRangeException>(tuning.EnsureValid);
        Assert.Throws<ArgumentOutOfRangeException>(() => new TranslationFrameQueues(tuning));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TranslationPumpSupervisor(tuning));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateDual(tuning));
    }

    // Given: 負の TimeSpan 予算
    // When: EnsureValid する
    // Then: ArgumentOutOfRangeException になる
    [Fact]
    public void NegativeDrainDurationsAreRejected()
    {
        var negativeCap = DualRealtimeTranslationClientTuning.Default with
        {
            DrainTimeoutCap = TimeSpan.FromMilliseconds(-1),
        };
        var capError = Assert.Throws<ArgumentOutOfRangeException>(negativeCap.EnsureValid);
        Assert.Equal(nameof(DualRealtimeTranslationClientTuning.DrainTimeoutCap), capError.ParamName);

        var negativeClose = DualRealtimeTranslationClientTuning.Default with
        {
            DefaultCloseDrainTimeout = TimeSpan.FromMilliseconds(-1),
        };
        var closeError = Assert.Throws<ArgumentOutOfRangeException>(negativeClose.EnsureValid);
        Assert.Equal(nameof(DualRealtimeTranslationClientTuning.DefaultCloseDrainTimeout), closeError.ParamName);
    }

    // Given: pending 上限 0
    // When: 抽出コンポーネントへ渡す
    // Then: 即 backlog halt になる値なので ArgumentOutOfRangeException になる
    [Fact]
    public void ZeroPendingLimitIsRejected()
    {
        var tuning = DualRealtimeTranslationClientTuning.Default with { PendingFrameLimit = 0 };
        var error = Assert.Throws<ArgumentOutOfRangeException>(tuning.EnsureValid);
        Assert.Equal(nameof(DualRealtimeTranslationClientTuning.PendingFrameLimit), error.ParamName);
        Assert.Throws<ArgumentOutOfRangeException>(() => new TranslationFrameQueues(tuning));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateDual(tuning));
    }

    private static DualRealtimeTranslationClient CreateDual(
        DualRealtimeTranslationClientTuning? tuning = null) =>
        new(
            new RealtimeSourceTranscriptionConnection(new FakeRealtimeServerTransport(), "test-safety"),
            new RealtimeTranslationConnection(
                RealtimeTranslationOutputLanguage.English,
                new FakeRealtimeServerTransport(),
                "test-safety"),
            new RealtimeTranslationConnection(
                RealtimeTranslationOutputLanguage.Japanese,
                new FakeRealtimeServerTransport(),
                "test-safety"),
            clientTuning: tuning);
}
