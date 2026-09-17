using System;
using System.Threading.Channels;

namespace RealtimeTranslator.Core.Audio;

public static class AudioFrameChannel
{
    public static Channel<CapturedAudioFrame> CreateBounded(Action<CapturedAudioFrame>? itemDropped = null) =>
        Channel.CreateBounded<CapturedAudioFrame>(
            new BoundedChannelOptions(AudioLossPolicy.SendQueueFrameCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true,
                AllowSynchronousContinuations = false,
            },
            itemDropped
        );
}
