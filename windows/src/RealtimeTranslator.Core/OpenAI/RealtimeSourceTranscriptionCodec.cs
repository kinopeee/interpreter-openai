using System;
using System.Buffers;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using RealtimeTranslator.Core.Audio;
using RealtimeTranslator.Core.Localization;

namespace RealtimeTranslator.Core.OpenAI;

/// <summary>原文 transcription 接続でクライアントが送るイベント。翻訳接続とは語彙が別。</summary>
public abstract record RealtimeSourceTranscriptionClientEvent
{
    private RealtimeSourceTranscriptionClientEvent()
    {
    }

    public sealed record SessionUpdate(
        RealtimeSessionTuning Tuning,
        LanguagePair Pair = LanguagePair.JaEn) : RealtimeSourceTranscriptionClientEvent;

    public sealed record InputAudioBufferAppend(string Base64Audio) : RealtimeSourceTranscriptionClientEvent;

    /// <summary>終了要求。翻訳接続の <c>session.close</c> に相当する。</summary>
    public sealed record Commit : RealtimeSourceTranscriptionClientEvent;
}

/// <summary>原文 transcription 接続から届くイベント。</summary>
public abstract record RealtimeSourceTranscriptionServerEvent
{
    private RealtimeSourceTranscriptionServerEvent()
    {
    }

    public sealed record SessionCreated : RealtimeSourceTranscriptionServerEvent;

    public sealed record SessionUpdated : RealtimeSourceTranscriptionServerEvent;

    /// <summary>字幕の原文 authority。<c>elapsed_ms</c> は付かない。</summary>
    public sealed record InputTranscriptDelta(string Delta, string? EventId) : RealtimeSourceTranscriptionServerEvent;

    /// <summary>commit の待ち合わせを解除する。</summary>
    public sealed record TranscriptionCompleted : RealtimeSourceTranscriptionServerEvent;

    public sealed record ServerError(string Message, string? Code) : RealtimeSourceTranscriptionServerEvent;

    /// <summary>この接続では意味を持たない payload。破棄する。</summary>
    public sealed record Ignored : RealtimeSourceTranscriptionServerEvent;
}

/// <summary>`wss://api.openai.com/v1/realtime?intent=transcription` の JSON 変換。</summary>
public static class RealtimeSourceTranscriptionCodec
{
    public const string TranscriptionModel = "gpt-live-transcribe";

    public const string ErrorCode = "transcription";

    /// <summary>認識対象言語。相手言語を確定する前から両方受け付ける。</summary>
    public static ImmutableArray<string> Languages(LanguagePair pair) =>
        [.. pair.Languages().Select(language => language switch
        {
            SpokenLanguage.Japanese => "ja",
            SpokenLanguage.English => "en",
            SpokenLanguage.Spanish => "es",
            _ => throw new ArgumentOutOfRangeException(nameof(pair), pair, null),
        })];

    private static string DefaultErrorMessage => UserCopy.Current.Text("error.sourceSessionGeneric");

    public static byte[] Encode(RealtimeSourceTranscriptionClientEvent clientEvent)
    {
        ArgumentNullException.ThrowIfNull(clientEvent);

        JsonObject payload = clientEvent switch
        {
            RealtimeSourceTranscriptionClientEvent.SessionUpdate sessionUpdate =>
                SessionUpdatePayload(sessionUpdate.Tuning, sessionUpdate.Pair),
            RealtimeSourceTranscriptionClientEvent.InputAudioBufferAppend append => new JsonObject
            {
                ["type"] = "input_audio_buffer.append",
                ["audio"] = append.Base64Audio,
            },
            RealtimeSourceTranscriptionClientEvent.Commit => new JsonObject
            {
                ["type"] = "input_audio_buffer.commit",
            },
            _ => throw new ArgumentOutOfRangeException(nameof(clientEvent), clientEvent, null),
        };

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            payload.WriteTo(writer);
        }

        return buffer.WrittenSpan.ToArray();
    }

    public static RealtimeSourceTranscriptionServerEvent DecodeServerEvent(ReadOnlySpan<byte> utf8Json)
    {
        JsonNode? node;
        try
        {
            var reader = new Utf8JsonReader(utf8Json);
            node = JsonNode.Parse(ref reader);
        }
        catch (JsonException)
        {
            throw new RealtimeTranslationException(RealtimeTranslationErrorKind.InvalidMessage);
        }

        if (node is not JsonObject payload)
        {
            throw new RealtimeTranslationException(RealtimeTranslationErrorKind.InvalidMessage);
        }

        var type = payload["type"] as JsonValue;
        if (type is null || !type.TryGetValue(out string? typeName))
        {
            throw new RealtimeTranslationException(RealtimeTranslationErrorKind.InvalidMessage);
        }

        switch (typeName)
        {
            case "session.created":
                return new RealtimeSourceTranscriptionServerEvent.SessionCreated();

            case "session.updated":
                return new RealtimeSourceTranscriptionServerEvent.SessionUpdated();

            case "conversation.item.input_audio_transcription.delta":
            {
                var delta = ReadString(payload["delta"]) ?? string.Empty;
                if (delta.Length == 0)
                {
                    return new RealtimeSourceTranscriptionServerEvent.Ignored();
                }

                // item_id は同一 turn の全 delta で共通なので重複排除に使わない。
                return new RealtimeSourceTranscriptionServerEvent.InputTranscriptDelta(
                    delta,
                    ReadString(payload["event_id"]));
            }

            case "conversation.item.input_audio_transcription.completed":
                return new RealtimeSourceTranscriptionServerEvent.TranscriptionCompleted();

            case "error":
                return new RealtimeSourceTranscriptionServerEvent.ServerError(
                    ClassifyError(payload).Message,
                    ErrorCode);

            default:
                return new RealtimeSourceTranscriptionServerEvent.Ignored();
        }
    }

    /// <summary>ハンドシェイク応答が期待した type でないときに投げる例外を組み立てる。</summary>
    public static RealtimeTranslationException ClassifyError(JsonObject payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var body = payload["error"] as JsonObject;
        var message = ReadString(body?["message"]) ?? DefaultErrorMessage;
        var code = ReadString(body?["code"]);

        return RealtimeTranslationException.IsAuthenticationFailure(code, message)
            ? new RealtimeTranslationException(RealtimeTranslationErrorKind.AuthenticationFailed)
            : new RealtimeTranslationException(
                RealtimeTranslationErrorKind.FatalServerError,
                RealtimeTranslationException.SanitizeServerMessage(message));
    }

    public static JsonObject SessionUpdatePayload(
        RealtimeSessionTuning tuning,
        LanguagePair pair = LanguagePair.JaEn)
    {
        ArgumentNullException.ThrowIfNull(tuning);

        var languages = StringArray(Languages(pair));
        var keywords = StringArray(tuning.TranscriptionKeywords);

        return new JsonObject
        {
            ["type"] = "session.update",
            ["session"] = new JsonObject
            {
                ["type"] = "transcription",
                ["audio"] = new JsonObject
                {
                    ["input"] = new JsonObject
                    {
                        ["format"] = new JsonObject
                        {
                            ["type"] = "audio/pcm",
                            ["rate"] = Audio.Pcm16FramePacketizer.SampleRate,
                        },
                        ["transcription"] = new JsonObject
                        {
                            ["model"] = TranscriptionModel,
                            ["languages"] = languages,
                            ["delay"] = tuning.TranscriptionDelay.ToWireValue(),
                            ["prompt"] = tuning.TranscriptionPrompt,
                            ["keywords"] = keywords,
                        },
                        ["noise_reduction"] = new JsonObject
                        {
                            ["type"] = tuning.NoiseReduction.ToWireValue(),
                        },

                        // turn_detection は明示的に null。サーバ側 VAD が無音を捨てないようにする。
                        ["turn_detection"] = null,
                    },
                },
            },
        };
    }

    /// <summary>trimming/AOT 解析が通るよう、非ジェネリックな <see cref="JsonNode"/> 要素だけで配列を組む。</summary>
    private static JsonArray StringArray(ImmutableArray<string> values)
    {
        var nodes = new JsonNode?[values.Length];
        for (var index = 0; index < values.Length; index += 1)
        {
            nodes[index] = JsonValue.Create(values[index]);
        }

        return new JsonArray(nodes);
    }

    private static string? ReadString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out string? text) ? text : null;
}
