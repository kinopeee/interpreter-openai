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

    // Given: fixture の翻訳クライアントイベント
    // When: 翻訳 codec でエンコードする
    // Then: 期待する JSON ペイロードと一致する
    [Theory]
    [MemberData(nameof(EncodeCases))]
    public void EncodeMatchesFixture(string name)
    {
        // Given: client event encode fixture
        var fixture = SharedFixtures.Case("codec", "encode", name);

        // When: JSON へ符号化
        var encoded = RealtimeTranslationMessageCodec.Encode(ClientEvent(fixture["event"]!.AsObject()));

        // Then: 期待 JSON と一致する
        var actual = SharedFixtures.ParseUtf8(encoded);
        var expected = fixture["expected"];
        Assert.True(
            SharedFixtures.JsonEquals(actual, expected),
            $"expected {SharedFixtures.Canonical(expected)} but encoded {SharedFixtures.Canonical(actual)}");
    }

    // Given: fixture の翻訳サーバーメッセージ
    // When: 翻訳 codec でデコードする
    // Then: 期待するサーバーイベント種別と値になる
    [Theory]
    [MemberData(nameof(DecodeCases))]
    public void DecodeMatchesFixture(string name)
    {
        // Given: server event decode fixture
        var fixture = SharedFixtures.Case("codec", "decode", name);
        var utf8 = Encoding.UTF8.GetBytes(SharedFixtures.Text(fixture["json"]));

        // When: サーバーイベントを復号する
        var actual = RealtimeTranslationMessageCodec.DecodeServerEvent(utf8);
        var expected = fixture["expected"]!.AsObject();

        // Then: kind ごとのフィールドが一致する
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

    // Given: 不正または欠損したサーバーメッセージ
    // When: 翻訳 codec でデコードする
    // Then: fixture が指定するエラー種別へ正規化される
    [Theory]
    [MemberData(nameof(DecodeFailureCases))]
    public void DecodeFailureMatchesFixture(string name)
    {
        // Given: 不正 JSON fixture
        var fixture = SharedFixtures.Case("codec", "decodeFailures", name);
        var utf8 = Encoding.UTF8.GetBytes(SharedFixtures.Text(fixture["json"]));

        // When: 復号を試みる
        var error = Assert.Throws<RealtimeTranslationException>(
            () => RealtimeTranslationMessageCodec.DecodeServerEvent(utf8));

        // Then: InvalidMessage に正規化される
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
