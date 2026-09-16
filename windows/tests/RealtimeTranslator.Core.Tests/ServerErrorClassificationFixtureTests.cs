using System;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using RealtimeTranslator.Core.Localization;
using RealtimeTranslator.Core.OpenAI;
using RealtimeTranslator.Core.Realtime;
using Xunit;

namespace RealtimeTranslator.Core.Tests;

/// <summary>
/// shared/fixtures/v1/server-error.json を正本として、サーバー error の分類が
/// type / code を別々に見た許可リストだけで決まることを確認する。
/// </summary>
public sealed class ServerErrorClassificationFixtureTests
{
    public static TheoryData<string> CaseNames
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var item in SharedFixtures.Section("server-error", "cases"))
            {
                data.Add(SharedFixtures.Text(item!["name"]));
            }

            return data;
        }
    }

    // Given: fixture の 1 ケース（type / code / message）
    // When: 共有分類器へ渡す
    // Then: disposition と termination、致命時の正規化文言が fixture と一致する
    [Theory]
    [MemberData(nameof(CaseNames))]
    public void ClassificationMatchesFixture(string name)
    {
        var item = SharedFixtures.Section("server-error", "cases")
            .Single(entry => SharedFixtures.Text(entry!["name"]) == name)!;
        var expected = item["expected"]!;

        var actual = RealtimeServerErrorClassification.Classify(
            SharedFixtures.OptionalText(item["errorType"]),
            SharedFixtures.OptionalText(item["code"]),
            SharedFixtures.Text(item["message"]));

        Assert.Equal(ParseDisposition(expected["disposition"]), actual.Disposition);
        Assert.Equal(ParseTermination(expected["termination"]), actual.Termination);
        var sanitized = SharedFixtures.OptionalText(expected["sanitizedMessage"]);
        Assert.Equal(sanitized, actual.SanitizedMessage);
        if (actual.Termination != EventDeliveryTermination.FatalServerError)
        {
            Assert.Null(actual.SanitizedMessage);
        }
    }

    // Given: fixture の許可リストと優先順位
    // When: Windows の定数と比較する
    // Then: transport code と終了理由の優先順位が一致する
    [Fact]
    public void AllowlistAndPrecedenceMatchFixture()
    {
        var fixture = SharedFixtures.Load("server-error");

        Assert.Equal(
            SharedFixtures.Text(fixture["allowlists"]!["transportCode"]),
            RealtimeServerErrorClassification.TransportCode);
        Assert.Equal(
            DualRealtimeTranslationClient.TransportErrorCode,
            RealtimeServerErrorClassification.TransportCode);
        Assert.Equal(
            Enum.GetValues<EventDeliveryTermination>()
                .Where(value => value != EventDeliveryTermination.None)
                .OrderByDescending(value => value),
            fixture["terminationPrecedence"]!.AsArray().Select(ParseTermination));

        var recoverable = fixture["recoverableServerError"]!;
        var exception = new RealtimeTranslationException(RealtimeTranslationErrorKind.RecoverableServerError);
        Assert.True(exception.IsRecoverable);
        Assert.Equal(
            UserCopy.Current.Text(SharedFixtures.Text(recoverable["errorMessageKey"])),
            exception.Message);
    }

    // Given: 回復候補・接続維持・致命の各 error を EventDeliveryState へ記録する
    // When: 優先順位どおりに記録する
    // Then: 接続維持は記録されず、致命が回復候補を上書きし、逆は起きない
    [Fact]
    public void DeliveryStateRecordsClassificationWithPrecedence()
    {
        var state = new EventDeliveryState(epoch: 1);

        Assert.False(state.TryRecordTermination(
            RealtimeServerErrorClassification.Classify(null, "input_audio_buffer_commit_empty", "empty")));
        Assert.Equal(EventDeliveryTermination.None, state.Termination);

        Assert.True(state.TryRecordTermination(
            RealtimeServerErrorClassification.Classify("server_error", null, "boom")));
        Assert.Equal(RealtimeTranslationErrorKind.RecoverableServerError, state.ToException().Kind);
        Assert.True(state.ToException().IsRecoverable);

        Assert.True(state.TryRecordTermination(
            RealtimeServerErrorClassification.Classify(null, "unknown_code", "bearer sk-x")));
        Assert.False(state.TryRecordTermination(
            RealtimeServerErrorClassification.Classify("server_error", null, "boom")));
        Assert.Equal(EventDeliveryTermination.FatalServerError, state.Termination);
        Assert.Equal(RealtimeTranslationException.GenericServerMessage, state.ToException().Message);
    }

    // Given: 翻訳接続と原文接続の codec
    // When: type と code を両方持つ error を復号する
    // Then: 両接続とも type / code を別々に保持し、分類結果が一致する
    [Fact]
    public void TranslationAndSourceCodecsPreserveTypeAndCodeIdentically()
    {
        var utf8 = Encoding.UTF8.GetBytes(
            """{"type":"error","error":{"message":"Rate limit reached","type":"rate_limit_error","code":"rate_limit_exceeded"}}""");

        var translation = Assert.IsType<RealtimeTranslationServerEvent.ServerError>(
            RealtimeTranslationMessageCodec.DecodeServerEvent(utf8));
        var source = Assert.IsType<RealtimeSourceTranscriptionServerEvent.ServerError>(
            RealtimeSourceTranscriptionCodec.DecodeServerEvent(utf8));

        Assert.Equal("rate_limit_error", translation.ErrorType);
        Assert.Equal("rate_limit_exceeded", translation.Code);
        Assert.Equal(translation.ErrorType, source.ErrorType);
        Assert.Equal(translation.Code, source.Code);
        Assert.Equal(
            EventDeliveryState.Classify(translation),
            EventDeliveryState.Classify(source.ToStreamError()));
        Assert.Equal(
            RealtimeServerErrorDisposition.Recover,
            EventDeliveryState.Classify(translation).Disposition);
    }

    // Given: 認証失敗の文言を持つが code が transport / invalid_request_error の error
    // When: 分類する / 原文 codec で復号してから再分類する
    // Then: どちらも AuthenticationFailed（transport 扱いで再接続に回らず、復号後も分類が変わらない）
    [Fact]
    public void AuthenticationEvidenceWinsOverTransportCodeAndSurvivesSourceDecoding()
    {
        var direct = RealtimeServerErrorClassification.Classify(
            null,
            RealtimeServerErrorClassification.TransportCode,
            "Incorrect API key provided: sk-secret");
        Assert.Equal(RealtimeServerErrorDisposition.Halt, direct.Disposition);
        Assert.Equal(EventDeliveryTermination.AuthenticationFailed, direct.Termination);

        var utf8 = Encoding.UTF8.GetBytes(
            """{"type":"error","error":{"message":"Incorrect API key provided: sk-secret","type":"invalid_request_error","code":"invalid_request_error"}}""");
        var source = Assert.IsType<RealtimeSourceTranscriptionServerEvent.ServerError>(
            RealtimeSourceTranscriptionCodec.DecodeServerEvent(utf8));
        Assert.DoesNotContain("sk-secret", source.Message, StringComparison.Ordinal);
        Assert.Equal(EventDeliveryTermination.AuthenticationFailed, source.Classification.Termination);
        // 表示文言（ローカライズ済み）から再分類すると認証の根拠が失われる。接続側は Classification を使う。
        Assert.NotEqual(
            EventDeliveryTermination.AuthenticationFailed,
            EventDeliveryState.Classify(source.ToStreamError()).Termination);
    }

    // Given: handshake 中の翻訳接続と原文接続
    // When: session.created の前に接続維持 error が届く
    // Then: どちらも読み飛ばして handshake を完了する
    [Fact]
    public async Task HandshakeSkipsKeepAliveErrorsOnBothConnections()
    {
        var translationTransport = new FakeRealtimeServerTransport();
        translationTransport.EnqueueJson(
            """{"type":"error","error":{"message":"buffer empty","code":"input_audio_buffer_commit_empty"}}""");
        var translation = new RealtimeTranslationConnection(
            RealtimeTranslationOutputLanguage.English,
            translationTransport,
            "test-safety");
        await translation.StartAsync(
            "sk-test",
            SessionConfigs.EnglishTargetWithoutSourceTranscription());
        Assert.False(translation.Events.Completion.IsCompleted);
        await translation.ForceCloseAsync();

        var sourceTransport = new FakeRealtimeServerTransport();
        sourceTransport.EnqueueJson(
            """{"type":"error","error":{"message":"buffer empty","code":"input_audio_buffer_commit_empty"}}""");
        var source = new RealtimeSourceTranscriptionConnection(sourceTransport, "test-safety");
        await source.StartAsync("sk-test", RealtimeSessionTuning.Default);
        Assert.False(source.Events.Completion.IsCompleted);
        await source.ForceCloseAsync();
    }

    // Given: handshake timeout 300ms、keep-alive error が 50ms 間隔で届き続ける両接続
    // When: session.created が永久に届かない
    // Then: keep-alive で期限は延びず、1 回の handshake 期限で SessionUpdateTimeout になる
    [Fact]
    public async Task HandshakeKeepAliveErrorsDoNotExtendTimeoutOnBothConnections()
    {
        const string keepAlive =
            """{"type":"error","error":{"message":"buffer empty","code":"input_audio_buffer_commit_empty"}}""";
        var timeout = TimeSpan.FromMilliseconds(300);

        var translationTransport = new FakeRealtimeServerTransport { AutoHandshake = false };
        translationTransport.AfterInboundRead = () =>
        {
            Thread.Sleep(50);
            translationTransport.EnqueueJson(keepAlive);
        };
        translationTransport.EnqueueJson(keepAlive);
        var translation = new RealtimeTranslationConnection(
            RealtimeTranslationOutputLanguage.English,
            translationTransport,
            "test-safety",
            sessionUpdateTimeout: timeout);
        var started = Stopwatch.GetTimestamp();
        var translationError = await Assert.ThrowsAsync<RealtimeTranslationException>(
            () => translation.StartAsync(
                "sk-test",
                SessionConfigs.EnglishTargetWithoutSourceTranscription()));
        Assert.Equal(RealtimeTranslationErrorKind.SessionUpdateTimeout, translationError.Kind);
        Assert.InRange(Stopwatch.GetElapsedTime(started), timeout, TimeSpan.FromSeconds(10));

        var sourceTransport = new FakeRealtimeServerTransport { AutoHandshake = false };
        sourceTransport.AfterInboundRead = () =>
        {
            Thread.Sleep(50);
            sourceTransport.EnqueueJson(keepAlive);
        };
        sourceTransport.EnqueueJson(keepAlive);
        var source = new RealtimeSourceTranscriptionConnection(
            sourceTransport,
            "test-safety",
            handshakeTimeout: timeout);
        started = Stopwatch.GetTimestamp();
        var sourceError = await Assert.ThrowsAsync<RealtimeTranslationException>(
            () => source.StartAsync("sk-test", RealtimeSessionTuning.Default));
        Assert.Equal(RealtimeTranslationErrorKind.SessionUpdateTimeout, sourceError.Kind);
        Assert.InRange(Stopwatch.GetElapsedTime(started), timeout, TimeSpan.FromSeconds(10));
    }

    // Given: handshake 中の翻訳接続
    // When: session.created の代わりに回復候補 error（server_error）が届く
    // Then: RecoverableServerError で失敗し、再接続の対象になる
    [Fact]
    public async Task HandshakeRecoverableServerErrorIsRecoverable()
    {
        var transport = new FakeRealtimeServerTransport();
        transport.EnqueueJson(
            """{"type":"error","error":{"message":"upstream echo sk-should-not-appear","type":"server_error"}}""");
        var connection = new RealtimeTranslationConnection(
            RealtimeTranslationOutputLanguage.English,
            transport,
            "test-safety");

        var error = await Assert.ThrowsAsync<RealtimeTranslationException>(
            () => connection.StartAsync(
                "sk-test",
                SessionConfigs.EnglishTargetWithoutSourceTranscription()));

        Assert.Equal(RealtimeTranslationErrorKind.RecoverableServerError, error.Kind);
        Assert.True(error.IsRecoverable);
        Assert.DoesNotContain("sk-", error.Message, StringComparison.Ordinal);
    }

    // Given: ready な原文接続
    // When: 接続維持 error のあとに delta が届く
    // Then: error イベントは下流へ流れず、delta だけが読める
    [Fact]
    public async Task SourceRuntimeKeepAliveErrorIsNotDelivered()
    {
        var transport = new FakeRealtimeServerTransport();
        var connection = new RealtimeSourceTranscriptionConnection(transport, "test-safety");
        await connection.StartAsync("sk-test", RealtimeSessionTuning.Default);

        transport.EnqueueJson(
            """{"type":"error","error":{"message":"buffer empty","code":"input_audio_buffer_commit_empty"}}""");
        transport.EnqueueJson(
            """{"type":"conversation.item.input_audio_transcription.delta","delta":"still-here","event_id":"e1"}""");

        var streamEvent = await connection.Events.ReadAsync(
            new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        var delta = Assert.IsType<RealtimeTranslationServerEvent.InputTranscriptDelta>(streamEvent.Event);
        Assert.Equal("still-here", delta.Delta);
        Assert.False(connection.Events.Completion.IsCompleted);
        await connection.ForceCloseAsync();
    }

    private static RealtimeServerErrorDisposition ParseDisposition(JsonNode? value) =>
        SharedFixtures.Text(value) switch
        {
            "keepAlive" => RealtimeServerErrorDisposition.KeepAlive,
            "recover" => RealtimeServerErrorDisposition.Recover,
            "halt" => RealtimeServerErrorDisposition.Halt,
            _ => throw new Xunit.Sdk.XunitException("unknown disposition"),
        };

    private static EventDeliveryTermination ParseTermination(JsonNode? value) =>
        SharedFixtures.Text(value) switch
        {
            "none" => EventDeliveryTermination.None,
            "authenticationFailed" => EventDeliveryTermination.AuthenticationFailed,
            "fatalServerError" => EventDeliveryTermination.FatalServerError,
            "receiveOverflow" => EventDeliveryTermination.ReceiveOverflow,
            "recoverableServerError" => EventDeliveryTermination.RecoverableServerError,
            "transportFailure" => EventDeliveryTermination.TransportFailure,
            _ => throw new Xunit.Sdk.XunitException("unknown termination"),
        };
}
