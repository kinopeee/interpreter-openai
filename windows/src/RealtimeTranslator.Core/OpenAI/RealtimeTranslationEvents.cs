using System;

namespace RealtimeTranslator.Core.OpenAI;

/// <summary>session.update で送るセッション設定。</summary>
public sealed record RealtimeTranslationSessionConfig(
    RealtimeTranslationOutputLanguage OutputLanguage,
    string? InputTranscriptionModel,
    RealtimeTranslationNoiseReduction? NoiseReduction);

/// <summary>クライアントからサーバーへ送るイベント。</summary>
public abstract record RealtimeTranslationClientEvent
{
    private RealtimeTranslationClientEvent()
    {
    }

    public sealed record SessionUpdate(RealtimeTranslationSessionConfig Config) : RealtimeTranslationClientEvent;

    public sealed record InputAudioBufferAppend(string Base64Audio) : RealtimeTranslationClientEvent;

    public sealed record SessionClose : RealtimeTranslationClientEvent;
}

/// <summary>サーバーから届くイベント。</summary>
public abstract record RealtimeTranslationServerEvent
{
    private RealtimeTranslationServerEvent()
    {
    }

    /// <summary><c>session.expires_at</c>（unix 秒）。不明（欠落・非数値・範囲外）は null。</summary>
    public sealed record SessionCreated(long? ExpiresAtUnixSeconds) : RealtimeTranslationServerEvent;

    public sealed record SessionUpdated : RealtimeTranslationServerEvent;

    public sealed record InputTranscriptDelta(string Delta, string? EventId, int? ElapsedMs)
        : RealtimeTranslationServerEvent;

    public sealed record OutputTranscriptDelta(string Delta, string? EventId, int? ElapsedMs)
        : RealtimeTranslationServerEvent;

    /// <summary>字幕 MVP では音声 payload をデコードしないため、到着マーカーとしてのみ扱う。</summary>
    public sealed record OutputAudioDelta : RealtimeTranslationServerEvent;

    public sealed record SessionClosed : RealtimeTranslationServerEvent;

    /// <summary><c>error.code</c> と <c>error.type</c> は別々に保持し、分類は許可リストだけで行う。</summary>
    public sealed record ServerError(string Message, string? Code, string? ErrorType = null)
        : RealtimeTranslationServerEvent;

    public sealed record Unknown(string Type) : RealtimeTranslationServerEvent;
}

/// <summary>どの接続から届いたかと接続世代を付与したイベント。</summary>
public readonly record struct RealtimeTranslationLane(
    bool IsSource,
    RealtimeTranslationOutputLanguage? Target)
{
    public static RealtimeTranslationLane Source => new(true, null);

    public static RealtimeTranslationLane Translation(RealtimeTranslationOutputLanguage target) =>
        new(false, target);
}

public sealed record RealtimeTranslationStreamEvent(
    RealtimeTranslationLane Lane,
    RealtimeTranslationServerEvent Event,
    int Epoch)
{
    public RealtimeTranslationStreamEvent(
        RealtimeTranslationOutputLanguage target,
        RealtimeTranslationServerEvent @event,
        int epoch)
        : this(RealtimeTranslationLane.Translation(target), @event, epoch)
    {
    }

    public RealtimeTranslationOutputLanguage Target =>
        Lane.Target ?? throw new InvalidOperationException("source lane has no translation target");
}
