using System;
using System.Collections.Generic;
using RealtimeTranslator.Core.OpenAI;

namespace RealtimeTranslator.Core.Realtime;

/// <summary>
/// 言語判定の遅延を吸収する rolling preroll と、翻訳送信待ちの pending frame 行列。
/// `DualRealtimeTranslationClient` の `_sync` ロック下だけで触るコンポーネント。
/// </summary>
internal sealed class TranslationFrameQueues
{
    private readonly Queue<PendingTranslationFrame> _pendingFrames = new();
    private readonly Queue<ReadOnlyMemory<byte>> _prerollFrames = new();
    private readonly int _prerollLimit;
    private readonly int _pendingLimit;

    public TranslationFrameQueues(DualRealtimeTranslationClientTuning tuning)
    {
        ArgumentNullException.ThrowIfNull(tuning);
        tuning.EnsureValid();
        _prerollLimit = tuning.PrerollFrameLimit;
        _pendingLimit = tuning.PendingFrameLimit;
    }

    public int PendingCount => _pendingFrames.Count;

    public bool HasPendingCapacity => _pendingFrames.Count < _pendingLimit;

    public IEnumerable<ReadOnlyMemory<byte>> PrerollFrames => _prerollFrames;

    public void AppendPreroll(ReadOnlyMemory<byte> frame)
    {
        _prerollFrames.Enqueue(frame);
        while (_prerollFrames.Count > _prerollLimit)
        {
            _prerollFrames.Dequeue();
        }
    }

    /// <summary>上限判定は呼び出し側（ポンプ停止の副作用を伴うため）。</summary>
    public void EnqueuePending(ReadOnlyMemory<byte> frame, RealtimeTranslationOutputLanguage target) =>
        _pendingFrames.Enqueue(new PendingTranslationFrame(frame, target));

    public PendingTranslationFrame DequeuePending() => _pendingFrames.Dequeue();

    public void ClearPending() => _pendingFrames.Clear();

    public void ClearAll()
    {
        _prerollFrames.Clear();
        _pendingFrames.Clear();
    }
}

internal readonly record struct PendingTranslationFrame(
    ReadOnlyMemory<byte> Frame,
    RealtimeTranslationOutputLanguage Target
);
