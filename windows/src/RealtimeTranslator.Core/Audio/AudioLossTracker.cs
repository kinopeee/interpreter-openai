using System;
using System.Collections.Generic;

namespace RealtimeTranslator.Core.Audio;

public readonly record struct AudioLossPolicy(
    int FrameDurationMilliseconds = 100,
    int ReconnectLostMillisecondsThreshold = 6400,
    int ReconnectWindowMilliseconds = 30000)
{
    public const int SendQueueFrameCapacity = 32;

    public static AudioLossPolicy Default => new(100, 6400, 30000);
}

public readonly record struct AudioLossObservation(
    int DroppedFrames,
    int LostMilliseconds,
    int QueueWaitMilliseconds,
    bool ShouldReconnect)
{
    public bool DidLose => LostMilliseconds > 0;
}

public readonly record struct AudioLossMetrics(
    int DroppedFrames,
    int LostMilliseconds,
    int LossEvents,
    int MaxQueueWaitMilliseconds);

public sealed class AudioLossTracker
{
    private readonly AudioLossPolicy _policy;
    private readonly List<LossEvent> _lossEvents = [];
    private AudioLossMetrics _metrics;
    private int? _lastGeneration;
    private long _lastSequence;
    private int _lastDiscardedMilliseconds;

    public AudioLossTracker(AudioLossPolicy? policy = null)
    {
        _policy = policy ?? AudioLossPolicy.Default;
    }

    public AudioLossMetrics Metrics => _metrics;

    public AudioLossObservation Observe(
        int generation,
        long sequence,
        int discardedMilliseconds,
        int queueWaitMilliseconds,
        long atMilliseconds)
    {
        var isFirstFrame = _lastGeneration != generation;
        var droppedFrames = isFirstFrame
            ? 0
            : (int)Math.Max(0, sequence - (_lastSequence + 1));
        var discardedDelta = isFirstFrame
            ? Math.Max(0, discardedMilliseconds)
            : Math.Max(0, discardedMilliseconds - _lastDiscardedMilliseconds);
        var lostMilliseconds = checked(
            droppedFrames * _policy.FrameDurationMilliseconds + discardedDelta);
        var clampedQueueWait = Math.Max(0, queueWaitMilliseconds);

        _lastGeneration = generation;
        _lastSequence = sequence;
        _lastDiscardedMilliseconds = Math.Max(0, discardedMilliseconds);
        _metrics = _metrics with
        {
            DroppedFrames = checked(_metrics.DroppedFrames + droppedFrames),
            LostMilliseconds = checked(_metrics.LostMilliseconds + lostMilliseconds),
            MaxQueueWaitMilliseconds = Math.Max(
                _metrics.MaxQueueWaitMilliseconds,
                clampedQueueWait),
        };

        if (lostMilliseconds > 0)
        {
            _metrics = _metrics with { LossEvents = checked(_metrics.LossEvents + 1) };
            _lossEvents.Add(new LossEvent(atMilliseconds, lostMilliseconds));
        }

        var oldestAllowed = atMilliseconds - _policy.ReconnectWindowMilliseconds;
        _lossEvents.RemoveAll(item => item.AtMilliseconds < oldestAllowed);
        var windowLoss = 0;
        foreach (var item in _lossEvents)
        {
            windowLoss = checked(windowLoss + item.LostMilliseconds);
        }

        var shouldReconnect = windowLoss >= _policy.ReconnectLostMillisecondsThreshold;
        if (shouldReconnect)
        {
            _lossEvents.Clear();
        }

        return new AudioLossObservation(
            droppedFrames,
            lostMilliseconds,
            clampedQueueWait,
            shouldReconnect);
    }

    public void Reset()
    {
        _lossEvents.Clear();
        _metrics = default;
        _lastGeneration = null;
        _lastSequence = 0;
        _lastDiscardedMilliseconds = 0;
    }

    private readonly record struct LossEvent(long AtMilliseconds, int LostMilliseconds);
}
