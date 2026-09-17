using System;

namespace RealtimeTranslator.Core.Audio;

public readonly record struct CapturedAudioFrame(
    int Generation,
    long Sequence,
    ReadOnlyMemory<byte> Pcm16,
    int DiscardedMilliseconds,
    long CapturedAtTimestamp
);
