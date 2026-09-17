using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using RealtimeTranslator.Core.Localization;
using RealtimeTranslator.Core.OpenAI;

namespace RealtimeTranslator.Core.Realtime;

public enum EventDeliveryStage
{
    Source,
    Translation,
    Merge,
    StopDrain,
}

public enum EventDeliveryTermination
{
    None = 0,
    TransportFailure,
    RecoverableServerError,
    ReceiveOverflow,
    FatalServerError,
    AuthenticationFailed,
}

public sealed class EventDeliveryState
{
    private readonly object _sync = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _didLoseEvents;
    private EventDeliveryStage _lossStage;
    private int _lossCapacity;
    private EventDeliveryTermination _termination;
    private string? _terminationMessage;
    private readonly Dictionary<RealtimeTranslationLane, int> _receiveCounts = new();
    private readonly Dictionary<RealtimeTranslationLane, long> _sessionExpiries = new();
    private int _pendingSourceFailureCount;

    public EventDeliveryState(int epoch)
    {
        Epoch = epoch;
    }

    public int Epoch { get; }

    public bool DidLoseEvents
    {
        get
        {
            lock (_sync)
            {
                return _didLoseEvents;
            }
        }
    }

    public EventDeliveryStage LossStage
    {
        get
        {
            lock (_sync)
            {
                return _lossStage;
            }
        }
    }

    public int LossCapacity
    {
        get
        {
            lock (_sync)
            {
                return _lossCapacity;
            }
        }
    }

    public EventDeliveryTermination Termination
    {
        get
        {
            lock (_sync)
            {
                return _termination;
            }
        }
    }

    public string? TerminationMessage
    {
        get
        {
            lock (_sync)
            {
                return _terminationMessage;
            }
        }
    }

    public int PendingSourceFailureCount
    {
        get
        {
            lock (_sync)
            {
                return _pendingSourceFailureCount;
            }
        }
    }

    public bool HasPendingSourceFailure
    {
        get
        {
            lock (_sync)
            {
                return _pendingSourceFailureCount > 0;
            }
        }
    }

    public void NoteSourceFailureQueued()
    {
        lock (_sync)
        {
            _pendingSourceFailureCount++;
        }
    }

    public void NoteSourceFailureConsumed()
    {
        lock (_sync)
        {
            if (_pendingSourceFailureCount > 0)
            {
                _pendingSourceFailureCount--;
            }
        }
    }

    public Task Completion => _completion.Task;

    public bool TryRecordTermination(EventDeliveryTermination termination, string? sanitizedMessage = null)
    {
        if (termination == EventDeliveryTermination.None)
        {
            return false;
        }

        bool upgraded;
        lock (_sync)
        {
            upgraded = termination > _termination;
            if (upgraded)
            {
                _termination = termination;
                _terminationMessage =
                    termination == EventDeliveryTermination.FatalServerError
                        ? RealtimeTranslationException.SanitizeServerMessage(sanitizedMessage ?? string.Empty)
                        : null;
            }
        }

        _completion.TrySetResult();
        return upgraded;
    }

    /// <summary>decode した全メッセージ（handshake 受信・keepAlive error・unknown を含む）で +1。</summary>
    public void RecordReceive(RealtimeTranslationLane lane)
    {
        lock (_sync)
        {
            _receiveCounts[lane] = _receiveCounts.TryGetValue(lane, out var count) ? count + 1 : 1;
        }
    }

    public int ReceiveCount(RealtimeTranslationLane lane)
    {
        lock (_sync)
        {
            return _receiveCounts.TryGetValue(lane, out var count) ? count : 0;
        }
    }

    /// <summary>`session.created` handshake 時に呼ぶ。不明なら null を記録する。</summary>
    public void RecordSessionExpiry(RealtimeTranslationLane lane, long? expiresAtUnixSeconds)
    {
        lock (_sync)
        {
            if (expiresAtUnixSeconds is { } value)
            {
                _sessionExpiries[lane] = value;
            }
            else
            {
                _sessionExpiries.Remove(lane);
            }
        }
    }

    public long? SessionExpiry(RealtimeTranslationLane lane)
    {
        lock (_sync)
        {
            return _sessionExpiries.TryGetValue(lane, out var value) ? value : null;
        }
    }

    public void RecordLoss(EventDeliveryStage stage, int capacity)
    {
        lock (_sync)
        {
            if (!_didLoseEvents)
            {
                _didLoseEvents = true;
                _lossStage = stage;
                _lossCapacity = capacity;
            }

            if (EventDeliveryTermination.ReceiveOverflow > _termination)
            {
                _termination = EventDeliveryTermination.ReceiveOverflow;
                _terminationMessage = null;
            }
        }

        _completion.TrySetResult();
    }

    public void CompleteNormally() => _completion.TrySetResult();

    public RealtimeTranslationException ToException()
    {
        EventDeliveryTermination termination;
        string? message;
        lock (_sync)
        {
            termination = _termination;
            message = _terminationMessage;
        }

        return termination switch
        {
            EventDeliveryTermination.AuthenticationFailed => new RealtimeTranslationException(
                RealtimeTranslationErrorKind.AuthenticationFailed
            ),
            EventDeliveryTermination.FatalServerError => new RealtimeTranslationException(
                RealtimeTranslationErrorKind.FatalServerError,
                message
            ),
            EventDeliveryTermination.ReceiveOverflow => new RealtimeTranslationException(
                RealtimeTranslationErrorKind.ReceiveOverflow
            ),
            EventDeliveryTermination.RecoverableServerError => new RealtimeTranslationException(
                RealtimeTranslationErrorKind.RecoverableServerError
            ),
            EventDeliveryTermination.TransportFailure => new RealtimeTranslationException(
                RealtimeTranslationErrorKind.RecoverableTransportFailure
            ),
            _ => throw new InvalidOperationException("Event delivery has no termination."),
        };
    }

    public static RealtimeServerErrorClassification Classify(RealtimeTranslationServerEvent.ServerError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return RealtimeServerErrorClassification.Classify(error.ErrorType, error.Code, error.Message);
    }

    public static RealtimeServerErrorClassification ClassifyTranscriptionFailure(string? errorType, string? code) =>
        RealtimeServerErrorClassification.ClassifyTranscriptionFailure(errorType, code);

    /// <summary>分類結果を記録する。接続維持なら何も記録せず false。</summary>
    public bool TryRecordTermination(RealtimeServerErrorClassification classification) =>
        classification.Disposition != RealtimeServerErrorDisposition.KeepAlive
        && TryRecordTermination(classification.Termination, classification.SanitizedMessage);
}

internal sealed class EventDeliveryWriter
{
    private readonly ChannelWriter<RealtimeTranslationStreamEvent> _writer;
    private readonly EventDeliveryState _state;
    private readonly EventDeliveryStage _stage;
    private readonly int _capacity;
    private int _completed;

    public EventDeliveryWriter(
        ChannelWriter<RealtimeTranslationStreamEvent> writer,
        EventDeliveryState state,
        EventDeliveryStage stage,
        int capacity
    )
    {
        _writer = writer;
        _state = state;
        _stage = stage;
        _capacity = capacity;
    }

    public bool TryDeliver(RealtimeTranslationStreamEvent streamEvent)
    {
        if (Volatile.Read(ref _completed) != 0)
        {
            return false;
        }

        if (_writer.TryWrite(streamEvent))
        {
            return true;
        }

        if (Volatile.Read(ref _completed) != 0)
        {
            return false;
        }

        _state.RecordLoss(_stage, _capacity);
        Complete();
        return false;
    }

    public void Complete()
    {
        if (Interlocked.Exchange(ref _completed, 1) == 0)
        {
            _writer.TryComplete();
        }
    }
}

public sealed record RealtimeEventFeed(
    ChannelReader<RealtimeTranslationStreamEvent> Events,
    int Epoch,
    EventDeliveryState DeliveryState
);
