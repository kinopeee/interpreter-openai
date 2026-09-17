using System;
using System.Threading;

namespace RealtimeTranslator.Core.Tests;

/// <summary>GetTimestamp と GetUtcNow を別々に進められる TimeProvider。</summary>
internal sealed class MonotonicClock : TimeProvider
{
    private long _timestamp;
    private long _utcTicks = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).UtcTicks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Interlocked.Read(ref _timestamp);

    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _utcTicks), TimeSpan.Zero);

    public void SetElapsed(TimeSpan elapsed)
    {
        Interlocked.Exchange(ref _timestamp, elapsed.Ticks);
        Interlocked.Exchange(
            ref _utcTicks,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).UtcTicks + elapsed.Ticks
        );
    }

    public void Advance(TimeSpan delta)
    {
        Interlocked.Add(ref _timestamp, delta.Ticks);
        Interlocked.Add(ref _utcTicks, delta.Ticks);
    }

    public void AdvanceWallClockOnly(TimeSpan delta) => Interlocked.Add(ref _utcTicks, delta.Ticks);
}
