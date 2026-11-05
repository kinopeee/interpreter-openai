using System;
using System.Threading;
using System.Threading.Tasks;
using RealtimeTranslator.Core.Audio;
using RealtimeTranslator.Core.Localization;
using RealtimeTranslator.Core.OpenAI;

namespace RealtimeTranslator.Core.Realtime;

/// <summary>
/// 接続試行中の音声フィード。キャプチャ frame を読み、capture/send の健全性を
/// 記録し、音声ロス観測と <see cref="IDualRealtimeTranslationClient.AppendAudioFrameAsync"/>
/// への送信を行う。フレーム経路の背圧や capture 停止は recoverable として上位へ投げる。
/// </summary>
internal sealed class SessionAudioFeeder
{
    private readonly SessionRuntimeContext _context;

    internal SessionAudioFeeder(SessionRuntimeContext context)
    {
        _context = context;
    }

    public async Task RunAsync(int generation, CancellationToken cancellationToken)
    {
        await foreach (var frame in _context.AudioCapture.Frames.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!_context.IsCurrentGeneration(generation) || _context.State != TranslationState.Listening)
            {
                return;
            }

            lock (_context.Sync)
            {
                _context.HealthBookkeeper.RecordCapture(
                    Pcm16AudioActivity.NormalizedPeakAmplitude(frame.Pcm16.Span)
                        > _context.HealthBookkeeper.Thresholds.AudioActivityPeakFloor
                );
            }

            var queueWaitMilliseconds = Math.Max(
                0,
                (int)_context.TimeProvider.GetElapsedTime(frame.CapturedAtTimestamp).TotalMilliseconds
            );
            var atMilliseconds = Math.Max(
                0,
                (long)_context.TimeProvider.GetElapsedTime(_context.StartTimestamp).TotalMilliseconds
            );
            AudioLossObservation observation;
            RealtimeSubtitleUpdate? invalidation = null;
            lock (_context.Sync)
            {
                observation = _context.SubtitlePipeline.ObserveAudio(
                    frame.Generation,
                    frame.Sequence,
                    frame.DiscardedMilliseconds,
                    queueWaitMilliseconds,
                    atMilliseconds
                );
                if (observation.DidLose)
                {
                    invalidation = _context.SubtitlePipeline.MarkAudioLoss();
                }
            }

            if (invalidation is { } lossUpdate)
            {
                _context.SubtitlePipeline.EmitSubtitleUpdate(lossUpdate);
                await _context
                    .AudioRouting.ResetAfterAudioLossAsync(_context.BeforeAudioLossRoutingResetForTests)
                    .ConfigureAwait(false);
            }

            if (observation.ShouldReconnect)
            {
                throw new RealtimeTranslationException(
                    RealtimeTranslationErrorKind.RecoverableTransportFailure,
                    UserCopy.Current.Text("error.audioInputStopped")
                );
            }

            lock (_context.Sync)
            {
                _context.HealthBookkeeper.RecordSendStart();
            }

            await _context.DualClient.AppendAudioFrameAsync(frame.Pcm16, cancellationToken).ConfigureAwait(false);
            lock (_context.Sync)
            {
                _context.HealthBookkeeper.RecordSendSuccess();
            }
        }

        if (!_context.IsCurrentGeneration(generation))
        {
            return;
        }

        throw new RealtimeTranslationException(
            RealtimeTranslationErrorKind.RecoverableTransportFailure,
            UserCopy.Current.Text("error.audioInputStopped")
        );
    }
}
