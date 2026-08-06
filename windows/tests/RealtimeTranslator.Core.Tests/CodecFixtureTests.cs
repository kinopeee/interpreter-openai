using System.Text;
using System.Text.Json.Nodes;
using RealtimeTranslator.Core.OpenAI;
using Xunit;

namespace RealtimeTranslator.Core.Tests;

public sealed class CodecFixtureTests
{
    public static TheoryData<string> EncodeCases => SharedFixtures.CaseNames("codec", "encode");

    public static TheoryData<string> DecodeCases => SharedFixtures.CaseNames("codec", "decode");

    public static TheoryData<string> DecodeFailureCases => SharedFixtures.CaseNames("codec", "decodeFailures");

    [Theory]
    [MemberData(nameof(EncodeCases))]
    public void EncodeMatchesFixture(string name)
    {
        var fixture = SharedFixtures.Case("codec", "encode", name);
        var encoded = RealtimeTranslationMessageCodec.Encode(ClientEvent(fixture["event"]!.AsObject()));

        var actual = SharedFixtures.ParseUtf8(encoded);
        var expected = fixture["expected"];
        Assert.True(
            SharedFixtures.JsonEquals(actual, expected),
            $"expected {SharedFixtures.Canonical(expected)} but encoded {SharedFixtures.Canonical(actual)}");
    }

    [Theory]
    [MemberData(nameof(DecodeCases))]
    public void DecodeMatchesFixture(string name)
    {
        var fixture = SharedFixtures.Case("codec", "decode", name);
        var utf8 = Encoding.UTF8.GetBytes(SharedFixtures.Text(fixture["json"]));

        var actual = RealtimeTranslationMessageCodec.DecodeServerEvent(utf8);
        var expected = fixture["expected"]!.AsObject();

        switch (SharedFixtures.Text(expected["kind"]))
        {
            case "sessionCreated":
                Assert.IsType<RealtimeTranslationServerEvent.SessionCreated>(actual);
                break;

            case "sessionUpdated":
                Assert.IsType<RealtimeTranslationServerEvent.SessionUpdated>(actual);
                break;

            case "sessionClosed":
                Assert.IsType<RealtimeTranslationServerEvent.SessionClosed>(actual);
                break;

            case "outputAudioDelta":
                Assert.IsType<RealtimeTranslationServerEvent.OutputAudioDelta>(actual);
                break;

            case "inputTranscriptDelta":
            {
                var typed = Assert.IsType<RealtimeTranslationServerEvent.InputTranscriptDelta>(actual);
                AssertDelta(expected, typed.Delta, typed.EventId, typed.ElapsedMs);
                break;
            }

            case "outputTranscriptDelta":
            {
                var typed = Assert.IsType<RealtimeTranslationServerEvent.OutputTranscriptDelta>(actual);
                AssertDelta(expected, typed.Delta, typed.EventId, typed.ElapsedMs);
                break;
            }

            case "error":
            {
                var typed = Assert.IsType<RealtimeTranslationServerEvent.ServerError>(actual);
                Assert.Equal(SharedFixtures.Text(expected["message"]), typed.Message);
                Assert.Equal(SharedFixtures.OptionalText(expected["code"]), typed.Code);
                break;
            }

            case "unknown":
            {
                var typed = Assert.IsType<RealtimeTranslationServerEvent.Unknown>(actual);
                Assert.Equal(SharedFixtures.Text(expected["type"]), typed.Type);
                break;
            }

            default:
                Assert.Fail("unhandled fixture kind " + SharedFixtures.Text(expected["kind"]));
                break;
        }
    }

    [Theory]
    [MemberData(nameof(DecodeFailureCases))]
    public void DecodeFailureMatchesFixture(string name)
    {
        var fixture = SharedFixtures.Case("codec", "decodeFailures", name);
        var utf8 = Encoding.UTF8.GetBytes(SharedFixtures.Text(fixture["json"]));

        var error = Assert.Throws<RealtimeTranslationException>(
            () => RealtimeTranslationMessageCodec.DecodeServerEvent(utf8));
        Assert.Equal(RealtimeTranslationErrorKind.InvalidMessage, error.Kind);
    }

    private static void AssertDelta(JsonObject expected, string delta, string? eventId, int? elapsedMs)
    {
        Assert.Equal(SharedFixtures.Text(expected["delta"]), delta);
        Assert.Equal(SharedFixtures.OptionalText(expected["eventId"]), eventId);
        Assert.Equal(SharedFixtures.OptionalNumber(expected["elapsedMs"]), elapsedMs);
    }

    private static RealtimeTranslationClientEvent ClientEvent(JsonObject fixture) =>
        SharedFixtures.Text(fixture["kind"]) switch
        {
            "sessionUpdate" => new RealtimeTranslationClientEvent.SessionUpdate(
                new RealtimeTranslationSessionConfig(
                    RealtimeTranslationWireValues.ParseOutputLanguage(SharedFixtures.Text(fixture["outputLanguage"])),
                    SharedFixtures.OptionalText(fixture["inputTranscriptionModel"]),
                    SharedFixtures.OptionalText(fixture["noiseReduction"]) is { } noiseReduction
                        ? RealtimeTranslationWireValues.ParseNoiseReduction(noiseReduction)
                        : null)),
            "inputAudioBufferAppend" => new RealtimeTranslationClientEvent.InputAudioBufferAppend(
                SharedFixtures.Text(fixture["base64Audio"])),
            "sessionClose" => new RealtimeTranslationClientEvent.SessionClose(),
            _ => throw new Xunit.Sdk.XunitException("unhandled client event kind"),
        };
}
