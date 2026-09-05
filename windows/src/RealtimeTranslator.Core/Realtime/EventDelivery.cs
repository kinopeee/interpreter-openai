using System;
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
    ReceiveOverflow,
    FatalServerError,
    AuthenticationFailed,
}

public sealed class EventDeliveryState
{
    private readonly object _sync = new();
    private readonly TaskCompletionSource _completion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _didLoseEvents;
    private EventDeliveryStage _lossStage;
    private int _lossCapacity;
    private EventDeliveryTermination _termination;
    private string? _terminationMessage;

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

    public Task Completion => _completion.Task;

    public bool TryRecordTermination(
        EventDeliveryTermination termination,
        string? sanitizedMessage = null)
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
                _terminationMessage = termination == EventDeliveryTermination.FatalServerError
                    ? RealtimeTranslationException.SanitizeServerMessage(sanitizedMessage ?? string.Empty)
                    : null;
            }
        }

        _completion.TrySetResult();
        return upgraded;
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
            EventDeliveryTermination.AuthenticationFailed =>
                new RealtimeTranslationException(RealtimeTranslationErrorKind.AuthenticationFailed),
            EventDeliveryTermination.FatalServerError =>
                new RealtimeTranslationException(
                    RealtimeTranslationErrorKind.FatalServerError,
                    message),
            EventDeliveryTermination.ReceiveOverflow =>
                new RealtimeTranslationException(RealtimeTranslationErrorKind.ReceiveOverflow),
            EventDeliveryTermination.TransportFailure =>
                new RealtimeTranslationException(RealtimeTranslationErrorKind.RecoverableTransportFailure),
            _ => throw new InvalidOperationException("Event delivery has no termination."),
        };
    }

    public static (EventDeliveryTermination Termination, string? SanitizedMessage) Classify(
        RealtimeTranslationServerEvent.ServerError error)
    {
        ArgumentNullException.ThrowIfNull(error);

        if (error.Code == DualRealtimeTranslationClient.TransportErrorCode)
        {
            return (EventDeliveryTermination.TransportFailure, null);
        }

        if (RealtimeTranslationException.IsAuthenticationFailure(error.Code, error.Message))
        {
            return (EventDeliveryTermination.AuthenticationFailed, null);
        }

        return (
            EventDeliveryTermination.FatalServerError,
            RealtimeTranslationException.SanitizeServerMessage(error.Message));
    }
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
        int capacity)
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
    EventDeliveryState DeliveryState);
