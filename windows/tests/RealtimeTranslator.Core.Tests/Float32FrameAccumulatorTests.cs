using System;
using System.Linq;
using RealtimeTranslator.Core.Audio;
using Xunit;

namespace RealtimeTranslator.Core.Tests;

public sealed class Float32FrameAccumulatorTests
{
    // Given: 2,400 samples ちょうどの入力
    // When: 追加する
    // Then: 100 ms frame が 1 つ出て端数は残らない
    [Fact]
    public void ExactlyOneFrameIsEmitted()
    {
        var accumulator = new Float32FrameAccumulator();

        var frames = accumulator.Append(Ramp(Float32FrameAccumulator.SamplesPerFrame));

        Assert.Single(frames);
        Assert.Equal(Float32FrameAccumulator.SamplesPerFrame, frames[0].Length);
        Assert.Equal(0, accumulator.PendingSampleCount);
    }

    // Given: 長さの違うチャンクに分けた 2.5 frame 分の入力
    // When: 順に追加し、最後に flush する
    // Then: 一括で追加した場合と同じ frame 列になり、端数は無音 padding される
    [Fact]
    public void ChunkBoundariesDoNotChangeFrames()
    {
        var input = Ramp(Float32FrameAccumulator.SamplesPerFrame * 5 / 2);
        var whole = new Float32FrameAccumulator();
        var chunked = new Float32FrameAccumulator();

        var expected = whole.Append(input).ToList();
        var actual = chunked
            .Append(input.AsSpan(0, 1000))
            .Concat(chunked.Append(input.AsSpan(1000, 2700)))
            .Concat(chunked.Append(input.AsSpan(3700)))
            .ToList();
        var padded = chunked.FlushWithSilencePadding();

        Assert.Equal(2, expected.Count);
        Assert.Equal(expected.Count, actual.Count);
        for (var index = 0; index < expected.Count; index += 1)
        {
            Assert.Equal(expected[index], actual[index]);
        }

        Assert.NotNull(padded);
        Assert.Equal(Float32FrameAccumulator.SamplesPerFrame, padded.Length);
        Assert.Equal(input[4800..], padded[..1200]);
        Assert.All(padded[1200..], sample => Assert.Equal(0f, sample));
        Assert.Equal(0, chunked.PendingSampleCount);
    }

    // Given: 端数が無い状態、または reset 後
    // When: flush する
    // Then: frame を返さない
    [Fact]
    public void FlushWithoutRemainderOrAfterResetReturnsNull()
    {
        var accumulator = new Float32FrameAccumulator();
        Assert.Null(accumulator.FlushWithSilencePadding());

        accumulator.Append(Ramp(100));
        Assert.Equal(100, accumulator.PendingSampleCount);
        accumulator.Reset();

        Assert.Equal(0, accumulator.PendingSampleCount);
        Assert.Null(accumulator.FlushWithSilencePadding());
        Assert.Empty(accumulator.Append([]));
    }

    private static float[] Ramp(int count) => Enumerable.Range(0, count).Select(index => index / 10_000f).ToArray();
}
