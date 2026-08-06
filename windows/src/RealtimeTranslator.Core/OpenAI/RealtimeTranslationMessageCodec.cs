using System;
using System.Buffers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RealtimeTranslator.Core.OpenAI;

/// <summary>Realtime セッションの JSON エンコード/デコード。契約は shared/protocol/endpoints.md。</summary>
public static class RealtimeTranslationMessageCodec
{
    public static byte[] Encode(RealtimeTranslationClientEvent clientEvent)
    {
        ArgumentNullException.ThrowIfNull(clientEvent);

        JsonObject payload;
        switch (clientEvent)
        {
            case RealtimeTranslationClientEvent.SessionUpdate sessionUpdate:
            {
                var config = sessionUpdate.Config;
                var input = new JsonObject();
                if (config.InputTranscriptionModel is { } model)
                {
                    input["transcription"] = new JsonObject { ["model"] = model };
                }

                // 無効時はキー省略ではなく明示的な null を送る。
                input["noise_reduction"] = config.NoiseReduction is { } noiseReduction
                    ? new JsonObject { ["type"] = noiseReduction.ToWireValue() }
                    : null;

                payload = new JsonObject
                {
                    ["type"] = "session.update",
                    ["session"] = new JsonObject
                    {
                        ["audio"] = new JsonObject
                        {
                            ["output"] = new JsonObject { ["language"] = config.OutputLanguage.ToWireValue() },
                            ["input"] = input,
                        },
                    },
                };
                break;
            }

            case RealtimeTranslationClientEvent.InputAudioBufferAppend append:
                payload = new JsonObject
                {
                    ["type"] = "session.input_audio_buffer.append",
                    ["audio"] = append.Base64Audio,
                };
                break;

            case RealtimeTranslationClientEvent.SessionClose:
                payload = new JsonObject { ["type"] = "session.close" };
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(clientEvent), clientEvent, null);
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            payload.WriteTo(writer);
        }

        return buffer.WrittenSpan.ToArray();
    }

    public static RealtimeTranslationServerEvent DecodeServerEvent(ReadOnlySpan<byte> utf8Json)
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

        if (node is not JsonObject dictionary
            || dictionary["type"] is not JsonValue typeValue
            || !typeValue.TryGetValue<string>(out var type))
        {
            throw new RealtimeTranslationException(RealtimeTranslationErrorKind.InvalidMessage);
        }

        switch (type)
        {
            case "session.created":
                return new RealtimeTranslationServerEvent.SessionCreated();
            case "session.updated":
                return new RealtimeTranslationServerEvent.SessionUpdated();
            case "session.input_transcript.delta":
                return new RealtimeTranslationServerEvent.InputTranscriptDelta(
                    StringValue(dictionary["delta"]) ?? string.Empty,
                    StringValue(dictionary["event_id"]),
                    IntValue(dictionary["elapsed_ms"]));
            case "session.output_transcript.delta":
                return new RealtimeTranslationServerEvent.OutputTranscriptDelta(
                    StringValue(dictionary["delta"]) ?? string.Empty,
                    StringValue(dictionary["event_id"]),
                    IntValue(dictionary["elapsed_ms"]));
            case "session.output_audio.delta":
                return new RealtimeTranslationServerEvent.OutputAudioDelta();
            case "session.closed":
                return new RealtimeTranslationServerEvent.SessionClosed();
            case "error":
            {
                var errorObject = dictionary["error"] as JsonObject;
                var message = StringValue(errorObject?["message"])
                    ?? StringValue(dictionary["message"])
                    ?? RealtimeTranslationException.GenericServerMessage;
                var code = StringValue(errorObject?["code"]) ?? StringValue(errorObject?["type"]);
                return new RealtimeTranslationServerEvent.ServerError(message, code);
            }

            default:
                return new RealtimeTranslationServerEvent.Unknown(type);
        }
    }

    private static string? StringValue(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    /// <summary>整数・実数どちらで届いても Int32 へ寄せる。実数は 0 方向へ切り捨てる。</summary>
    private static int? IntValue(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<int>(out var intValue))
        {
            return intValue;
        }

        if (value.TryGetValue<double>(out var doubleValue)
            && doubleValue >= int.MinValue
            && doubleValue <= int.MaxValue)
        {
            return (int)doubleValue;
        }

        return null;
    }
}
