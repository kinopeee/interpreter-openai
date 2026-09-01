using System.Text;
using RealtimeTranslator.Core.Localization;
using RealtimeTranslator.Core.OpenAI;
using Xunit;

namespace RealtimeTranslator.Core.Tests;

/// <summary>
/// 原文 codec の壊れた payload 正規化と、throw せず落とすべき runtime 形。
/// 翻訳側 <c>decodeFailures</c> と同じ不正 JSON は InvalidMessage へ寄せ、
/// 空/欠落 delta や error 本文なしはストリーム切断にしない。
/// <c>SourceTranscriptionCodecFixtureTests</c> を触る開いている coverage PR とは交差しない。
/// </summary>
public sealed class SourceTranscriptionCodecRobustnessTests
{
    public static TheoryData<string> DecodeFailureCases =>
        SharedFixtures.CaseNames("codec", "decodeFailures");

    // Given: 翻訳 codec と同じ decodeFailures fixture
    // When: 原文専用 codec でデコードする
    // Then: parser 例外を外へ出さず InvalidMessage へ正規化する
    [Theory]
    [MemberData(nameof(DecodeFailureCases))]
    public void DecodeFailureMatchesFixture(string name)
    {
        var fixture = SharedFixtures.Case("codec", "decodeFailures", name);
        var utf8 = Encoding.UTF8.GetBytes(SharedFixtures.Text(fixture["json"]));

        var error = Assert.Throws<RealtimeTranslationException>(
            () => RealtimeSourceTranscriptionCodec.DecodeServerEvent(utf8));

        Assert.Equal(RealtimeTranslationErrorKind.InvalidMessage, error.Kind);
    }

    // Given: delta キーが無い原文 transcription delta
    // When: 原文 codec でデコードする
    // Then: InvalidMessage にせず Ignored にする（空発話で接続を落とさない）
    [Fact]
    public void MissingDeltaIsIgnored()
    {
        var utf8 = Encoding.UTF8.GetBytes(
            """{"type":"conversation.item.input_audio_transcription.delta","event_id":"evt_missing"}""");

        var actual = RealtimeSourceTranscriptionCodec.DecodeServerEvent(utf8);

        Assert.IsType<RealtimeSourceTranscriptionServerEvent.Ignored>(actual);
    }

    // Given: delta が文字列ではない原文 transcription delta
    // When: 原文 codec でデコードする
    // Then: InvalidMessage にせず Ignored にする
    [Theory]
    [InlineData("""{"type":"conversation.item.input_audio_transcription.delta","delta":42}""")]
    [InlineData("""{"type":"conversation.item.input_audio_transcription.delta","delta":null}""")]
    [InlineData("""{"type":"conversation.item.input_audio_transcription.delta","delta":["x"]}""")]
    public void NonStringDeltaIsIgnored(string json)
    {
        var actual = RealtimeSourceTranscriptionCodec.DecodeServerEvent(Encoding.UTF8.GetBytes(json));

        Assert.IsType<RealtimeSourceTranscriptionServerEvent.Ignored>(actual);
    }

    // Given: error 本文が無い runtime error
    // When: 原文 codec でデコードする
    // Then: throw せず ServerError になり、code は transcription、文言は原文汎用メッセージ
    [Fact]
    public void ErrorWithoutBodyIsSourceGenericServerError()
    {
        var actual = RealtimeSourceTranscriptionCodec.DecodeServerEvent(
            Encoding.UTF8.GetBytes("""{"type":"error"}"""));
        var error = Assert.IsType<RealtimeSourceTranscriptionServerEvent.ServerError>(actual);

        Assert.Equal(RealtimeSourceTranscriptionCodec.ErrorCode, error.Code);
        Assert.Equal(UserCopy.Current.Text("error.sourceSessionGeneric"), error.Message);
    }

    // Given: error フィールドがオブジェクトではない runtime error
    // When: 原文 codec でデコードする
    // Then: throw せず原文汎用 ServerError になる
    [Fact]
    public void ErrorWithNonObjectBodyIsSourceGenericServerError()
    {
        var actual = RealtimeSourceTranscriptionCodec.DecodeServerEvent(
            Encoding.UTF8.GetBytes("""{"type":"error","error":"boom"}"""));
        var error = Assert.IsType<RealtimeSourceTranscriptionServerEvent.ServerError>(actual);

        Assert.Equal(RealtimeSourceTranscriptionCodec.ErrorCode, error.Code);
        Assert.Equal(UserCopy.Current.Text("error.sourceSessionGeneric"), error.Message);
    }

    // Given: message が空文字の非認証 runtime error
    // When: 原文 codec でデコードする
    // Then: 空は Sanitize で汎用サーバー文言になり、鍵断片も出さない
    [Fact]
    public void EmptyErrorMessageBecomesGenericServerMessage()
    {
        var actual = RealtimeSourceTranscriptionCodec.DecodeServerEvent(
            Encoding.UTF8.GetBytes("""{"type":"error","error":{"message":"","code":"server_error"}}"""));
        var error = Assert.IsType<RealtimeSourceTranscriptionServerEvent.ServerError>(actual);

        Assert.Equal(RealtimeSourceTranscriptionCodec.ErrorCode, error.Code);
        Assert.Equal(RealtimeTranslationException.GenericServerMessage, error.Message);
        Assert.DoesNotContain("sk-", error.Message, System.StringComparison.Ordinal);
    }
}
