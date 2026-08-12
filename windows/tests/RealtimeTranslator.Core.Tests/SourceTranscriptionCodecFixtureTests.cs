using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using RealtimeTranslator.Core.OpenAI;
using RealtimeTranslator.Core.Audio;
using Xunit;

namespace RealtimeTranslator.Core.Tests;

public sealed class SourceTranscriptionCodecFixtureTests
{
    public static TheoryData<string> EncodeCases => SharedFixtures.CaseNames("codec", "transcriptionEncode");

    public static TheoryData<string> DecodeCases => SharedFixtures.CaseNames("codec", "transcriptionDecode");

    // Given: fixture のソース文字起こしクライアントイベント
    // When: 文字起こし専用 codec でエンコードする
    // Then: 翻訳接続とは別形式の期待ペイロードになる
    [Theory]
    [MemberData(nameof(EncodeCases))]
    public void EncodeMatchesFixture(string name)
    {
        // Given: transcription client encode fixture
        var fixture = SharedFixtures.Case("codec", "transcriptionEncode", name);

        // When: JSON へ符号化
        var encoded = RealtimeSourceTranscriptionCodec.Encode(ClientEvent(fixture["event"]!.AsObject()));

        // Then: 期待 JSON と一致する
        var actual = SharedFixtures.ParseUtf8(encoded);
        var expected = fixture["expected"];
        Assert.True(
            SharedFixtures.JsonEquals(actual, expected),
            $"expected {SharedFixtures.Canonical(expected)} but encoded {SharedFixtures.Canonical(actual)}");
    }

    // Given: fixture の文字起こし接続からのサーバーメッセージ
    // When: 文字起こし専用 codec でデコードする
    // Then: 期待イベントになり、翻訳側イベントは無視される
    [Theory]
    [MemberData(nameof(DecodeCases))]
    public void DecodeMatchesFixture(string name)
    {
        // Given: transcription server decode fixture
        var fixture = SharedFixtures.Case("codec", "transcriptionDecode", name);
        var utf8 = Encoding.UTF8.GetBytes(SharedFixtures.Text(fixture["json"]));

        // When: サーバーイベントを復号する
        var actual = RealtimeSourceTranscriptionCodec.DecodeServerEvent(utf8);
        var expected = fixture["expected"]!.AsObject();

        // Then: kind ごとのフィールドが一致する
        switch (SharedFixtures.Text(expected["kind"]))
        {
            case "sessionCreated":
                Assert.IsType<RealtimeSourceTranscriptionServerEvent.SessionCreated>(actual);
                break;

            case "sessionUpdated":
                Assert.IsType<RealtimeSourceTranscriptionServerEvent.SessionUpdated>(actual);
                break;

            case "transcriptionCompleted":
                Assert.IsType<RealtimeSourceTranscriptionServerEvent.TranscriptionCompleted>(actual);
                break;

            case "ignored":
                Assert.IsType<RealtimeSourceTranscriptionServerEvent.Ignored>(actual);
                break;

            case "inputTranscriptDelta":
            {
                var typed = Assert.IsType<RealtimeSourceTranscriptionServerEvent.InputTranscriptDelta>(actual);
                Assert.Equal(SharedFixtures.Text(expected["delta"]), typed.Delta);
                Assert.Equal(SharedFixtures.OptionalText(expected["eventId"]), typed.EventId);

                // 原文 transcription は elapsed_ms を持たない。
                Assert.Null(SharedFixtures.OptionalNumber(expected["elapsedMs"]));
                break;
            }

            case "error":
            {
                var typed = Assert.IsType<RealtimeSourceTranscriptionServerEvent.ServerError>(actual);
                Assert.Equal(SharedFixtures.Text(expected["message"]), typed.Message);
                Assert.Equal(SharedFixtures.OptionalText(expected["code"]), typed.Code);
                break;
            }

            default:
                Assert.Fail("unhandled fixture kind " + SharedFixtures.Text(expected["kind"]));
                break;
        }
    }

    // Given: JSON として不正なペイロード
    // When: 文字起こし専用 codec でデコードする
    // Then: InvalidMessage へ正規化される
    [Fact]
    public void MalformedPayloadIsNormalizedToInvalidMessage()
    {
        // Given: 壊れた JSON
        // When: 復号を試みる
        var error = Assert.Throws<RealtimeTranslationException>(
            () => RealtimeSourceTranscriptionCodec.DecodeServerEvent(Encoding.UTF8.GetBytes("{\"type\":")));

        // Then: InvalidMessage に正規化される
        Assert.Equal(RealtimeTranslationErrorKind.InvalidMessage, error.Kind);
    }

    private static RealtimeSourceTranscriptionClientEvent ClientEvent(JsonObject fixture) =>
        SharedFixtures.Text(fixture["kind"]) switch
        {
        "sessionUpdate" => new RealtimeSourceTranscriptionClientEvent.SessionUpdate(
                new RealtimeSessionTuning(
                    RealtimeTranslationWireValues.ParseNoiseReduction(
                        SharedFixtures.Text(fixture["noiseReduction"])),
                    RealtimeTranslationWireValues.ParseTranscriptionDelay(
                        SharedFixtures.Text(fixture["transcriptionDelay"])),
                    SharedFixtures.Text(fixture["prompt"]),
                    Keywords(fixture["keywords"]!.AsArray())),
                PairFromLanguages(fixture["languages"])),
            "inputAudioBufferAppend" => new RealtimeSourceTranscriptionClientEvent.InputAudioBufferAppend(
                SharedFixtures.Text(fixture["base64Audio"])),
            "commit" => new RealtimeSourceTranscriptionClientEvent.Commit(),
            _ => throw new Xunit.Sdk.XunitException("unhandled client event kind"),
        };

    private static ImmutableArray<string> Keywords(JsonArray values)
    {
        var builder = ImmutableArray.CreateBuilder<string>(values.Count);
        foreach (var value in values)
        {
            builder.Add(SharedFixtures.Text(value));
        }

        return builder.ToImmutable();
    }

    private static LanguagePair PairFromLanguages(JsonNode? node)
    {
        if (node is not JsonArray values || values.Count != 2)
        {
            return LanguagePair.JaEn;
        }

        var wire = string.Join("-", values.Select(SharedFixtures.Text));
        return LanguagePairExtensions.ParseLanguagePair(wire);
    }
}
