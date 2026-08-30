using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using RealtimeTranslator.Core.Audio;
using RealtimeTranslator.Core.OpenAI;
using RealtimeTranslator.Core.Realtime;
using Xunit;

namespace RealtimeTranslator.Core.Tests;

/// <summary>
/// スペイン語接続が無い Dual で JaEs / EnEs を Start したあとの leftover。
/// DrainTests の例外メッセージ契約とは交差せず、running 状態と復旧だけを見る。
/// </summary>
public sealed class DualRealtimeTranslationClientPairGuardTests
{
    // Given: スペイン語接続を渡していない Dual
    // When: スペイン語を含む pair で Start する
    // Then: ArgumentException のあと Dual は running に入らず、Select/Append は NotConnected、
    //       handshake は始まらず、Events は完了したまま（購読がハングしない）
    [Theory]
    [InlineData(LanguagePair.JaEs)]
    [InlineData(LanguagePair.EnEs)]
    public async Task FailedSpanishPairStartLeavesDualNotRunning(LanguagePair pair)
    {
        var source = new FakeRealtimeServerTransport { AutoCloseResponses = true };
        var english = new FakeRealtimeServerTransport { AutoCloseResponses = true };
        var japanese = new FakeRealtimeServerTransport { AutoCloseResponses = true };
        using var dual = CreateJaEnOnlyDual(source, english, japanese);

        var error = await Assert.ThrowsAsync<ArgumentException>(
            () => dual.StartAsync("sk-test", RealtimeSessionTuning.Default, pair));

        Assert.Equal("pair", error.ParamName);
        Assert.Equal(0, source.ConnectCount);
        Assert.Equal(0, english.ConnectCount);
        Assert.Equal(0, japanese.ConnectCount);

        var selectError = await Assert.ThrowsAsync<RealtimeTranslationException>(
            () => dual.SelectTranslationTargetAsync(RealtimeTranslationOutputLanguage.English));
        var appendError = await Assert.ThrowsAsync<RealtimeTranslationException>(
            () => dual.AppendAudioFrameAsync(Encoding.UTF8.GetBytes("after-failed-start")));
        Assert.Equal(RealtimeTranslationErrorKind.NotConnected, selectError.Kind);
        Assert.Equal(RealtimeTranslationErrorKind.NotConnected, appendError.Kind);

        await dual.CloseGracefullyAsync();
        Assert.Empty(SentTypes(english));
        Assert.Empty(SentTypes(japanese));
        Assert.Empty(SentTypes(source));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (dual.Events.TryRead(out _))
        {
        }

        Assert.False(await dual.Events.WaitToReadAsync(timeout.Token));
    }

    // Given: ja-en で開始済みの Dual（スペイン語接続なし）
    // When: JaEs で Start し直して失敗する
    // Then: 旧セッションは ForceClose され、失敗後は running に戻らず Select できない
    [Fact]
    public async Task FailedSpanishPairStartAfterJaEnTearsDownPreviousSession()
    {
        var source = new FakeRealtimeServerTransport { AutoCloseResponses = true };
        var english = new FakeRealtimeServerTransport { AutoCloseResponses = true };
        var japanese = new FakeRealtimeServerTransport { AutoCloseResponses = true };
        using var dual = CreateJaEnOnlyDual(source, english, japanese);

        await dual.StartAsync("sk-test", RealtimeSessionTuning.Default, LanguagePair.JaEn);
        var sourceConnectAfterJaEn = source.ConnectCount;
        var sourceCloseAfterJaEn = source.CloseCount;
        Assert.True(sourceConnectAfterJaEn >= 1);

        var error = await Assert.ThrowsAsync<ArgumentException>(
            () => dual.StartAsync("sk-test", RealtimeSessionTuning.Default, LanguagePair.JaEs));

        Assert.Equal("pair", error.ParamName);
        Assert.Equal(sourceConnectAfterJaEn, source.ConnectCount);
        Assert.True(source.CloseCount > sourceCloseAfterJaEn);
        Assert.True(english.CloseCount >= 1);
        Assert.True(japanese.CloseCount >= 1);

        var selectError = await Assert.ThrowsAsync<RealtimeTranslationException>(
            () => dual.SelectTranslationTargetAsync(RealtimeTranslationOutputLanguage.Japanese));
        Assert.Equal(RealtimeTranslationErrorKind.NotConnected, selectError.Kind);
    }

    // Given: スペイン語 pair の Start に失敗した Dual
    // When: ja-en で Start し直す
    // Then: leftover に巻き込まれず handshake でき、英語 lane へ frame が届く
    [Fact]
    public async Task JaEnStartSucceedsAfterFailedSpanishPairStart()
    {
        var source = new FakeRealtimeServerTransport();
        var english = new FakeRealtimeServerTransport();
        var japanese = new FakeRealtimeServerTransport();
        using var dual = CreateJaEnOnlyDual(source, english, japanese);

        await Assert.ThrowsAsync<ArgumentException>(
            () => dual.StartAsync("sk-test", RealtimeSessionTuning.Default, LanguagePair.EnEs));

        await dual.StartAsync("sk-test", RealtimeSessionTuning.Default, LanguagePair.JaEn);
        await dual.SelectTranslationTargetAsync(RealtimeTranslationOutputLanguage.English);
        await dual.AppendAudioFrameAsync(Encoding.UTF8.GetBytes("recovered"));
        await dual.WaitForTranslationDrainAsync();

        Assert.Equal(1, source.ConnectCount);
        Assert.Equal(1, english.ConnectCount);
        Assert.Equal(1, japanese.ConnectCount);
        Assert.Equal(["recovered"], english.AppendedFrameTexts());
        Assert.Contains("recovered", source.AppendedFrameTexts());
        await dual.ForceCloseAsync();
    }

    private static DualRealtimeTranslationClient CreateJaEnOnlyDual(
        FakeRealtimeServerTransport source,
        FakeRealtimeServerTransport english,
        FakeRealtimeServerTransport japanese) =>
        new(
            new RealtimeSourceTranscriptionConnection(source, "test-safety"),
            new RealtimeTranslationConnection(
                RealtimeTranslationOutputLanguage.English,
                english,
                "test-safety"),
            new RealtimeTranslationConnection(
                RealtimeTranslationOutputLanguage.Japanese,
                japanese,
                "test-safety"));

    private static List<string> SentTypes(FakeRealtimeServerTransport transport)
    {
        var types = new List<string>();
        foreach (var payload in transport.Sent)
        {
            var type = JsonNode.Parse(payload)?.AsObject()["type"]?.GetValue<string>();
            if (type is { } value)
            {
                types.Add(value);
            }
        }

        return types;
    }
}
