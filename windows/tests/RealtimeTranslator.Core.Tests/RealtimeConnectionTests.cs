using System;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using RealtimeTranslator.Core.OpenAI;
using RealtimeTranslator.Core.Realtime;
using Xunit;

namespace RealtimeTranslator.Core.Tests;

/// <summary>3 本の Realtime 接続の handshake / 送受信 / close 契約。</summary>
public sealed class RealtimeConnectionTests
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(300);

    // Given: handshake を自動応答する fake サーバー
    // When: 翻訳接続を開始して音声 frame を送る
    // Then: session.update の後に base64 音声が append される
    [Fact]
    public async Task TranslationConnectionHandshakesThenAppendsAudio()
    {
        var transport = new FakeRealtimeServerTransport();
        var connection = new RealtimeTranslationConnection(
            RealtimeTranslationOutputLanguage.English,
            transport,
            "test-safety");

        await connection.StartAsync(
            "sk-test",
            RealtimeTranslationSessionConfig.EnglishTargetWithoutSourceTranscription());
        await connection.AppendAudioFrameAsync(Encoding.UTF8.GetBytes("frame"));

        Assert.Equal("session.update", TypeOf(transport.Sent[0]));
        Assert.Equal(["frame"], transport.AppendedFrameTexts());
        await connection.ForceCloseAsync();
    }

    // Given: 翻訳接続と原文接続
    // When: それぞれ接続する
    // Then: 規定の endpoint と Authorization / Safety-Identifier のみを送り、OpenAI-Beta は送らない
    [Fact]
    public async Task ConnectionsUseTheContractedEndpointsAndHeaders()
    {
        var translationTransport = new FakeRealtimeServerTransport();
        var translation = new RealtimeTranslationConnection(
            RealtimeTranslationOutputLanguage.Japanese,
            translationTransport,
            "safety-id");
        var sourceTransport = new FakeRealtimeServerTransport();
        var source = new RealtimeSourceTranscriptionConnection(sourceTransport, "safety-id");

        await translation.StartAsync(
            "sk-test",
            RealtimeTranslationSessionConfig.JapaneseTargetWithoutSourceTranscription());
        await source.StartAsync("sk-test", RealtimeSessionTuning.Default);

        Assert.Equal(
            new Uri("wss://api.openai.com/v1/realtime/translations?model=gpt-realtime-translate"),
            translationTransport.ConnectedUrl);
        Assert.Equal(
            new Uri("wss://api.openai.com/v1/realtime?intent=transcription"),
            sourceTransport.ConnectedUrl);

        foreach (var headers in new[] { translationTransport.ConnectedHeaders, sourceTransport.ConnectedHeaders })
        {
            Assert.Equal("Bearer sk-test", headers["Authorization"]);
            Assert.Equal("safety-id", headers["OpenAI-Safety-Identifier"]);
            Assert.DoesNotContain("OpenAI-Beta", headers.Keys, StringComparer.OrdinalIgnoreCase);
        }

        await translation.ForceCloseAsync();
        await source.ForceCloseAsync();
    }

    // Given: session.created を返さない fake サーバー
    // When: handshake timeout まで待つ
    // Then: SessionUpdateTimeout で失敗し transport は解放される
    [Fact]
    public async Task TranslationConnectionTimesOutWhenHandshakeStalls()
    {
        var transport = new FakeRealtimeServerTransport { AutoHandshake = false };
        var connection = new RealtimeTranslationConnection(
            RealtimeTranslationOutputLanguage.English,
            transport,
            "test-safety",
            sessionUpdateTimeout: ShortTimeout);

        var error = await Assert.ThrowsAsync<RealtimeTranslationException>(() => connection.StartAsync(
            "sk-test",
            RealtimeTranslationSessionConfig.EnglishTargetWithoutSourceTranscription()));

        Assert.Equal(RealtimeTranslationErrorKind.SessionUpdateTimeout, error.Kind);
        Assert.True(transport.CloseCount >= 1);
    }

    // Given: handshake 中に認証エラーを返す fake サーバー
    // When: 接続を開始する
    // Then: AuthenticationFailed へ分類され、鍵断片を含む文言は使わない
    [Fact]
    public async Task TranslationConnectionClassifiesAuthenticationFailure()
    {
        var transport = new FakeRealtimeServerTransport { AutoHandshake = false };
        transport.EnqueueJson("""{"type":"error","error":{"message":"Incorrect API key sk-live-xyz","code":"invalid_api_key"}}""");
        var connection = new RealtimeTranslationConnection(
            RealtimeTranslationOutputLanguage.English,
            transport,
            "test-safety",
            sessionUpdateTimeout: ShortTimeout);

        var error = await Assert.ThrowsAsync<RealtimeTranslationException>(() => connection.StartAsync(
            "sk-test",
            RealtimeTranslationSessionConfig.EnglishTargetWithoutSourceTranscription()));

        Assert.Equal(RealtimeTranslationErrorKind.AuthenticationFailed, error.Kind);
        Assert.DoesNotContain("sk-live-xyz", error.Message, StringComparison.Ordinal);
    }

    // Given: handshake 中に Authorization / Bearer を含む非 auth code の error
    // When: 翻訳接続を開始する
    // Then: AuthenticationFailed へ分類し、鍵断片を Message に出さない
    [Fact]
    public async Task TranslationConnectionClassifiesAuthorizationThemedHandshakeAsAuthenticationFailure()
    {
        var transport = new FakeRealtimeServerTransport { AutoHandshake = false };
        transport.EnqueueJson(
            """{"type":"error","error":{"message":"Invalid Authorization header: Bearer sk-leak-example","code":"invalid_request_error"}}""");
        var connection = new RealtimeTranslationConnection(
            RealtimeTranslationOutputLanguage.English,
            transport,
            "test-safety",
            sessionUpdateTimeout: ShortTimeout);

        var error = await Assert.ThrowsAsync<RealtimeTranslationException>(() => connection.StartAsync(
            "sk-test",
            RealtimeTranslationSessionConfig.EnglishTargetWithoutSourceTranscription()));

        Assert.Equal(RealtimeTranslationErrorKind.AuthenticationFailed, error.Kind);
        Assert.DoesNotContain("sk-leak-example", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-", error.Message, StringComparison.Ordinal);
        Assert.True(transport.CloseCount >= 1);
    }

    // Given: handshake 中にキー断片を含む非認証 server_error
    // When: 翻訳接続を開始する
    // Then: FatalServerError へ分類し、表示文言から秘密情報を除去する
    [Fact]
    public async Task TranslationConnectionHandshakeFatalServerErrorRedactsKeyMaterial()
    {
        var transport = new FakeRealtimeServerTransport { AutoHandshake = false };
        transport.EnqueueJson(
            """{"type":"error","error":{"message":"upstream echo sk-should-not-appear","code":"server_error"}}""");
        var connection = new RealtimeTranslationConnection(
            RealtimeTranslationOutputLanguage.English,
            transport,
            "test-safety",
            sessionUpdateTimeout: ShortTimeout);

        var error = await Assert.ThrowsAsync<RealtimeTranslationException>(() => connection.StartAsync(
            "sk-test",
            RealtimeTranslationSessionConfig.EnglishTargetWithoutSourceTranscription()));

        Assert.Equal(RealtimeTranslationErrorKind.FatalServerError, error.Kind);
        Assert.Equal(RealtimeTranslationException.GenericServerMessage, error.Message);
        Assert.DoesNotContain("sk-should-not-appear", error.Message, StringComparison.Ordinal);
        Assert.True(transport.CloseCount >= 1);
    }

    // Given: handshake で session.created の代わりに session.updated が来る
    // When: 翻訳接続を開始する
    // Then: InvalidMessage で失敗し transport は解放される
    [Fact]
    public async Task TranslationConnectionRejectsUnexpectedHandshakeEvent()
    {
        var transport = new FakeRealtimeServerTransport { AutoHandshake = false };
        transport.EnqueueJson("""{"type":"session.updated"}""");
        var connection = new RealtimeTranslationConnection(
            RealtimeTranslationOutputLanguage.English,
            transport,
            "test-safety",
            sessionUpdateTimeout: ShortTimeout);

        var error = await Assert.ThrowsAsync<RealtimeTranslationException>(() => connection.StartAsync(
            "sk-test",
            RealtimeTranslationSessionConfig.EnglishTargetWithoutSourceTranscription()));

        Assert.Equal(RealtimeTranslationErrorKind.InvalidMessage, error.Kind);
        Assert.True(transport.CloseCount >= 1);
    }

    // Given: 空の API キー
    // When: 接続を開始する
    // Then: 送信前に MissingApiKey で失敗する
    [Fact]
    public async Task TranslationConnectionRejectsBlankApiKey()
    {
        var transport = new FakeRealtimeServerTransport();
        var connection = new RealtimeTranslationConnection(
            RealtimeTranslationOutputLanguage.English,
            transport,
            "test-safety");

        var error = await Assert.ThrowsAsync<RealtimeTranslationException>(() => connection.StartAsync(
            "   ",
            RealtimeTranslationSessionConfig.EnglishTargetWithoutSourceTranscription()));

        Assert.Equal(RealtimeTranslationErrorKind.MissingApiKey, error.Kind);
        Assert.Equal(0, transport.ConnectCount);
    }

    // Given: handshake 中に Missing bearer 文言（code は非 auth）
    // When: 翻訳接続を開始する
    // Then: AuthenticationFailed へ分類し、bearer を Message に出さない
    [Fact]
    public async Task TranslationConnectionClassifiesMissingBearerHandshakeAsAuthenticationFailure()
    {
        var transport = new FakeRealtimeServerTransport { AutoHandshake = false };
        transport.EnqueueJson(
            """{"type":"error","error":{"message":"Missing bearer or basic authentication in header","code":"invalid_request_error"}}""");
        var connection = new RealtimeTranslationConnection(
            RealtimeTranslationOutputLanguage.English,
            transport,
            "test-safety",
            sessionUpdateTimeout: ShortTimeout);

        var error = await Assert.ThrowsAsync<RealtimeTranslationException>(() => connection.StartAsync(
            "sk-test",
            RealtimeTranslationSessionConfig.EnglishTargetWithoutSourceTranscription()));

        Assert.Equal(RealtimeTranslationErrorKind.AuthenticationFailed, error.Kind);
        Assert.DoesNotContain("bearer", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sk-", error.Message, StringComparison.Ordinal);
        Assert.True(transport.CloseCount >= 1);
    }

    // Given: 埋め込み改行と時刻が混ざったキー
    // When: 接続を開始する
    // Then: 送信前に AuthenticationFailed で失敗する
    [Fact]
    public async Task TranslationConnectionRejectsMalformedApiKeyBeforeConnect()
    {
        var transport = new FakeRealtimeServerTransport();
        var connection = new RealtimeTranslationConnection(
            RealtimeTranslationOutputLanguage.English,
            transport,
            "test-safety");

        var error = await Assert.ThrowsAsync<RealtimeTranslationException>(() => connection.StartAsync(
            "sk-proj-abc\n3:26",
            RealtimeTranslationSessionConfig.EnglishTargetWithoutSourceTranscription()));

        Assert.Equal(RealtimeTranslationErrorKind.AuthenticationFailed, error.Kind);
        Assert.Equal(0, transport.ConnectCount);
    }

    // Given: 行折り返しされた allowlist キー
    // When: 翻訳接続を開始する
    // Then: Authorization は正規化後のキーだけを載せる
    [Fact]
    public async Task TranslationConnectionStripsEmbeddedWhitespaceFromApiKeyHeader()
    {
        var transport = new FakeRealtimeServerTransport();
        var connection = new RealtimeTranslationConnection(
            RealtimeTranslationOutputLanguage.English,
            transport,
            "test-safety");

        await connection.StartAsync(
            "sk-proj-AAAA\nBBBB",
            RealtimeTranslationSessionConfig.EnglishTargetWithoutSourceTranscription());

        Assert.Equal("Bearer sk-proj-AAAABBBB", transport.ConnectedHeaders["Authorization"]);
        await connection.ForceCloseAsync();
    }

    // Given: ready 状態の翻訳接続
    // When: 訳文 delta が届く
    // Then: target と epoch を付けて下流へ流す
    [Fact]
    public async Task TranslationConnectionPublishesDecodedEvents()
    {
        var transport = new FakeRealtimeServerTransport();
        var connection = new RealtimeTranslationConnection(
            RealtimeTranslationOutputLanguage.Japanese,
            transport,
            "test-safety");
        await connection.StartAsync(
            "sk-test",
            RealtimeTranslationSessionConfig.JapaneseTargetWithoutSourceTranscription());

        transport.EnqueueJson("""{"type":"session.output_transcript.delta","delta":"こんにちは","event_id":"e1"}""");
        var streamEvent = await ReadOneAsync(connection.Events);

        Assert.Equal(RealtimeTranslationOutputLanguage.Japanese, streamEvent.Target);
        var delta = Assert.IsType<RealtimeTranslationServerEvent.OutputTranscriptDelta>(streamEvent.Event);
        Assert.Equal("こんにちは", delta.Delta);
        Assert.Equal(connection.Epoch, streamEvent.Epoch);
        await connection.ForceCloseAsync();
    }

    // Given: ready な翻訳接続（原文 transcription は別接続）
    // When: session.input_transcript.delta のあとに output_transcript.delta が届く
    // Then: 翻訳側 input_transcript は Events に出ず、訳文だけが届く（原文 authority 汚染を防ぐ）
    [Fact]
    public async Task TranslationConnectionDoesNotEnqueueInputTranscriptDeltas()
    {
        var transport = new FakeRealtimeServerTransport();
        var connection = new RealtimeTranslationConnection(
            RealtimeTranslationOutputLanguage.English,
            transport,
            "test-safety");
        await connection.StartAsync(
            "sk-test",
            RealtimeTranslationSessionConfig.EnglishTargetWithoutSourceTranscription());

        transport.EnqueueJson(
            """{"type":"session.input_transcript.delta","delta":"polluting source","event_id":"in-1","elapsed_ms":10}""");
        transport.EnqueueJson(
            """{"type":"session.output_transcript.delta","delta":"kept translation","event_id":"out-1"}""");

        RealtimeTranslationServerEvent.OutputTranscriptDelta? kept = null;
        var deadline = Environment.TickCount64 + 5_000;
        while (kept is null && Environment.TickCount64 < deadline)
        {
            while (connection.Events.TryRead(out var streamEvent))
            {
                Assert.IsNotType<RealtimeTranslationServerEvent.InputTranscriptDelta>(streamEvent.Event);
                if (streamEvent.Event is RealtimeTranslationServerEvent.OutputTranscriptDelta transcript)
                {
                    kept = transcript;
                }
            }

            if (kept is null)
            {
                await Task.Delay(10);
            }
        }

        Assert.NotNull(kept);
        Assert.Equal("kept translation", kept.Delta);
        await connection.ForceCloseAsync();
    }

    // Given: Stop 相当で Events を読まないまま output_audio.delta が channel 容量を超えて届く
    // When: その後に output_transcript.delta が届く
    // Then: 音声 delta は channel に載せず、訳文 delta が DropOldest で消えない
    [Fact]
    public async Task TranslationConnectionDoesNotEnqueueOutputAudioDeltas()
    {
        var transport = new FakeRealtimeServerTransport();
        var connection = new RealtimeTranslationConnection(
            RealtimeTranslationOutputLanguage.English,
            transport,
            "test-safety");
        await connection.StartAsync(
            "sk-test",
            RealtimeTranslationSessionConfig.EnglishTargetWithoutSourceTranscription());

        // bounded(512) + DropOldest を超える量。フィルタが無いと後続の訳文が落ちる。
        for (var index = 0; index < 600; index += 1)
        {
            transport.EnqueueJson("""{"type":"session.output_audio.delta","delta":"AAAA"}""");
        }

        transport.EnqueueJson(
            """{"type":"session.output_transcript.delta","delta":"kept after audio flood","event_id":"keep-1"}""");

        RealtimeTranslationServerEvent.OutputTranscriptDelta? kept = null;
        var deadline = Environment.TickCount64 + 5_000;
        while (kept is null && Environment.TickCount64 < deadline)
        {
            while (connection.Events.TryRead(out var streamEvent))
            {
                Assert.IsNotType<RealtimeTranslationServerEvent.OutputAudioDelta>(streamEvent.Event);
                if (streamEvent.Event is RealtimeTranslationServerEvent.OutputTranscriptDelta transcript)
                {
                    kept = transcript;
                }
            }

            if (kept is null)
            {
                await Task.Delay(10);
            }
        }

        Assert.NotNull(kept);
        Assert.Equal("kept after audio flood", kept.Delta);
        await connection.ForceCloseAsync();
    }

    // Given: ready 状態の翻訳接続
    // When: 復号できないメッセージが届く
    // Then: transport error として 1 度だけ通知しイベント流を閉じる
    [Fact]
    public async Task TranslationConnectionReportsTransportErrorOnBrokenMessage()
    {
        var transport = new FakeRealtimeServerTransport();
        var connection = new RealtimeTranslationConnection(
            RealtimeTranslationOutputLanguage.English,
            transport,
            "test-safety");
        await connection.StartAsync(
            "sk-test",
            RealtimeTranslationSessionConfig.EnglishTargetWithoutSourceTranscription());

        transport.EnqueueRaw(Encoding.UTF8.GetBytes("{not json"));
        var streamEvent = await ReadOneAsync(connection.Events);

        var error = Assert.IsType<RealtimeTranslationServerEvent.ServerError>(streamEvent.Event);
        Assert.Equal("transport", error.Code);
        await connection.ForceCloseAsync();
    }

    // Given: session.closed を返さない fake サーバー
    // When: graceful close を試みる
    // Then: session.close を送った上で CloseTimeout になる
    [Fact]
    public async Task TranslationConnectionCloseTimesOutWithoutSessionClosed()
    {
        var transport = new FakeRealtimeServerTransport();
        var connection = new RealtimeTranslationConnection(
            RealtimeTranslationOutputLanguage.English,
            transport,
            "test-safety",
            closeTimeout: ShortTimeout);
        await connection.StartAsync(
            "sk-test",
            RealtimeTranslationSessionConfig.EnglishTargetWithoutSourceTranscription());

        var error = await Assert.ThrowsAsync<RealtimeTranslationException>(
            () => connection.CloseGracefullyAsync());

        Assert.Equal(RealtimeTranslationErrorKind.CloseTimeout, error.Kind);
        Assert.Equal("session.close", TypeOf(transport.Sent[^1]));
    }

    // Given: handshake 前（未 ready）の翻訳接続。closeTimeout は長く、誤って待つとテストが固まる
    // When: ready 前に graceful close する
    // Then: session.closed 待ちへ入らず即完了し、session.close も送らない
    [Fact]
    public async Task TranslationConnectionCloseBeforeReadyForceClosesWithoutWaiting()
    {
        var transport = new FakeRealtimeServerTransport { AutoHandshake = false };
        var connection = new RealtimeTranslationConnection(
            RealtimeTranslationOutputLanguage.Japanese,
            transport,
            "test-safety",
            closeTimeout: TimeSpan.FromSeconds(2));

        var started = Stopwatch.StartNew();
        await connection.CloseGracefullyAsync();
        started.Stop();

        Assert.True(started.Elapsed < TimeSpan.FromMilliseconds(500));
        Assert.Equal(1, transport.CloseCount);
        Assert.Empty(transport.Sent);
    }

    // Given: 原文 transcription 接続
    // When: delta と completed が届く
    // Then: delta だけを英語 lane 相当のイベントとして流し、commit 応答で graceful close できる
    [Fact]
    public async Task SourceConnectionPublishesDeltasAndClosesOnCompleted()
    {
        var transport = new FakeRealtimeServerTransport();
        var connection = new RealtimeSourceTranscriptionConnection(
            transport,
            "test-safety",
            closeTimeout: TimeSpan.FromSeconds(2));
        await connection.StartAsync("sk-test", RealtimeSessionTuning.Default);

        transport.EnqueueJson(
            """{"type":"conversation.item.input_audio_transcription.delta","item_id":"i1","delta":"hello","event_id":"e1"}""");
        var streamEvent = await ReadOneAsync(connection.Events);
        var delta = Assert.IsType<RealtimeTranslationServerEvent.InputTranscriptDelta>(streamEvent.Event);
        Assert.Equal("hello", delta.Delta);
        Assert.Null(delta.ElapsedMs);

        transport.EnqueueJson("""{"type":"conversation.item.input_audio_transcription.completed"}""");
        await connection.CloseGracefullyAsync();

        Assert.Equal("input_audio_buffer.commit", TypeOf(transport.Sent[^1]));
    }

    // Given: 接続していない原文 transcription 接続
    // When: tuning 更新や frame 送信を試みる
    // Then: NotConnected で拒否する
    [Fact]
    public async Task SourceConnectionRejectsUseBeforeStart()
    {
        var transport = new FakeRealtimeServerTransport();
        var connection = new RealtimeSourceTranscriptionConnection(transport, "test-safety");

        var appendError = await Assert.ThrowsAsync<RealtimeTranslationException>(
            () => connection.AppendAudioFrameAsync(Encoding.UTF8.GetBytes("frame")));
        var tuningError = await Assert.ThrowsAsync<RealtimeTranslationException>(
            () => connection.UpdateTuningAsync(RealtimeSessionTuning.Default));

        Assert.Equal(RealtimeTranslationErrorKind.NotConnected, appendError.Kind);
        Assert.Equal(RealtimeTranslationErrorKind.NotConnected, tuningError.Kind);
    }

    // Given: session.created を返さない原文接続
    // When: handshake timeout まで待つ
    // Then: SessionUpdateTimeout で失敗し transport は解放される
    [Fact]
    public async Task SourceConnectionTimesOutWhenHandshakeStalls()
    {
        var transport = new FakeRealtimeServerTransport { AutoHandshake = false };
        var connection = new RealtimeSourceTranscriptionConnection(
            transport,
            "test-safety",
            handshakeTimeout: ShortTimeout);

        var error = await Assert.ThrowsAsync<RealtimeTranslationException>(
            () => connection.StartAsync("sk-test", RealtimeSessionTuning.Default));

        Assert.Equal(RealtimeTranslationErrorKind.SessionUpdateTimeout, error.Kind);
        Assert.True(transport.CloseCount >= 1);
    }

    // Given: session.created を返さない翻訳接続。handshake timeout は長い
    // When: 呼び出し側 token を Connect 後にキャンセルする
    // Then: SessionUpdateTimeout（再接続対象）ではなく OperationCanceledException になる
    [Fact]
    public async Task TranslationConnectionStartCanceledByCallerIsNotHandshakeTimeout()
    {
        var transport = new FakeRealtimeServerTransport { AutoHandshake = false };
        var connection = new RealtimeTranslationConnection(
            RealtimeTranslationOutputLanguage.English,
            transport,
            "test-safety",
            sessionUpdateTimeout: TimeSpan.FromSeconds(15));
        using var caller = new CancellationTokenSource();
        var startTask = connection.StartAsync(
            "sk-test",
            RealtimeTranslationSessionConfig.EnglishTargetWithoutSourceTranscription(),
            caller.Token);

        await WaitUntilAsync(() => transport.ConnectCount >= 1);
        await caller.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => startTask);
        Assert.True(transport.CloseCount >= 1);
    }

    // Given: session.created を返さない原文接続。handshake timeout は長い
    // When: 呼び出し側 token を Connect 後にキャンセルする
    // Then: SessionUpdateTimeout（再接続対象）ではなく OperationCanceledException になる
    [Fact]
    public async Task SourceConnectionStartCanceledByCallerIsNotHandshakeTimeout()
    {
        var transport = new FakeRealtimeServerTransport { AutoHandshake = false };
        var connection = new RealtimeSourceTranscriptionConnection(
            transport,
            "test-safety",
            handshakeTimeout: TimeSpan.FromSeconds(15));
        using var caller = new CancellationTokenSource();
        var startTask = connection.StartAsync("sk-test", RealtimeSessionTuning.Default, caller.Token);

        await WaitUntilAsync(() => transport.ConnectCount >= 1);
        await caller.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => startTask);
        Assert.True(transport.CloseCount >= 1);
    }

    // Given: handshake 前（未 ready）の原文接続。closeTimeout は長く、誤って待つとテストが固まる
    // When: ready 前に graceful close する
    // Then: completed 待ちへ入らず即完了し、commit も送らない
    [Fact]
    public async Task SourceConnectionCloseBeforeReadyForceClosesWithoutWaiting()
    {
        var transport = new FakeRealtimeServerTransport { AutoHandshake = false };
        var connection = new RealtimeSourceTranscriptionConnection(
            transport,
            "test-safety",
            closeTimeout: TimeSpan.FromSeconds(2));

        var started = Stopwatch.StartNew();
        await connection.CloseGracefullyAsync();
        started.Stop();

        Assert.True(started.Elapsed < TimeSpan.FromMilliseconds(500));
        Assert.Equal(1, transport.CloseCount);
        Assert.Empty(transport.Sent);
    }

    // Given: session completed を返さない ready な原文接続
    // When: graceful close を試みる
    // Then: commit を送った上で CloseTimeout になる
    [Fact]
    public async Task SourceConnectionCloseTimesOutWithoutCompleted()
    {
        var transport = new FakeRealtimeServerTransport();
        var connection = new RealtimeSourceTranscriptionConnection(
            transport,
            "test-safety",
            closeTimeout: ShortTimeout);
        await connection.StartAsync("sk-test", RealtimeSessionTuning.Default);

        var error = await Assert.ThrowsAsync<RealtimeTranslationException>(
            () => connection.CloseGracefullyAsync());

        Assert.Equal(RealtimeTranslationErrorKind.CloseTimeout, error.Kind);
        Assert.Equal("input_audio_buffer.commit", TypeOf(transport.Sent[^1]));
    }

    // Given: handshake 中に認証エラーを返す原文接続
    // When: 接続を開始する
    // Then: 再接続しない致命失敗になり、鍵断片は Message に出ず transport は解放される
    [Fact]
    public async Task SourceConnectionClassifiesAuthenticationFailure()
    {
        var transport = new FakeRealtimeServerTransport { AutoHandshake = false };
        transport.EnqueueJson(
            """{"type":"error","error":{"message":"Incorrect API key sk-live-xyz","code":"invalid_api_key"}}""");
        var connection = new RealtimeSourceTranscriptionConnection(
            transport,
            "test-safety",
            handshakeTimeout: ShortTimeout);

        var error = await Assert.ThrowsAsync<RealtimeTranslationException>(
            () => connection.StartAsync("sk-test", RealtimeSessionTuning.Default));

        Assert.False(error.IsRecoverable);
        Assert.Equal("OpenAI APIキーが無効です", error.Message);
        Assert.DoesNotContain("sk-live-xyz", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-", error.Message, StringComparison.Ordinal);
        Assert.True(transport.CloseCount >= 1);
    }

    // Given: handshake 中に Authorization / Bearer を含む非 auth code の error
    // When: 原文接続を開始する
    // Then: 認証失敗文言へ正規化し、鍵断片を Message に出さない
    [Fact]
    public async Task SourceConnectionClassifiesAuthorizationThemedHandshakeAsAuthenticationFailure()
    {
        var transport = new FakeRealtimeServerTransport { AutoHandshake = false };
        transport.EnqueueJson(
            """{"type":"error","error":{"message":"Invalid Authorization header: Bearer sk-leak-example","code":"invalid_request_error"}}""");
        var connection = new RealtimeSourceTranscriptionConnection(
            transport,
            "test-safety",
            handshakeTimeout: ShortTimeout);

        var error = await Assert.ThrowsAsync<RealtimeTranslationException>(
            () => connection.StartAsync("sk-test", RealtimeSessionTuning.Default));

        Assert.False(error.IsRecoverable);
        Assert.Equal("OpenAI APIキーが無効です", error.Message);
        Assert.DoesNotContain("sk-leak-example", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-", error.Message, StringComparison.Ordinal);
        Assert.True(transport.CloseCount >= 1);
    }

    // Given: handshake 中にキー断片を含む非認証 server_error
    // When: 原文接続を開始する
    // Then: FatalServerError へ分類し、表示文言から秘密情報を除去する
    [Fact]
    public async Task SourceConnectionHandshakeFatalServerErrorRedactsKeyMaterial()
    {
        var transport = new FakeRealtimeServerTransport { AutoHandshake = false };
        transport.EnqueueJson(
            """{"type":"error","error":{"message":"upstream echo sk-should-not-appear","code":"server_error"}}""");
        var connection = new RealtimeSourceTranscriptionConnection(
            transport,
            "test-safety",
            handshakeTimeout: ShortTimeout);

        var error = await Assert.ThrowsAsync<RealtimeTranslationException>(
            () => connection.StartAsync("sk-test", RealtimeSessionTuning.Default));

        Assert.Equal(RealtimeTranslationErrorKind.FatalServerError, error.Kind);
        Assert.Equal(RealtimeTranslationException.GenericServerMessage, error.Message);
        Assert.DoesNotContain("sk-should-not-appear", error.Message, StringComparison.Ordinal);
        Assert.True(transport.CloseCount >= 1);
    }

    // Given: handshake で session.created の代わりに session.updated が来る
    // When: 原文接続を開始する
    // Then: InvalidMessage で失敗し transport は解放される
    [Fact]
    public async Task SourceConnectionRejectsUnexpectedHandshakeEvent()
    {
        var transport = new FakeRealtimeServerTransport { AutoHandshake = false };
        transport.EnqueueJson("""{"type":"session.updated"}""");
        var connection = new RealtimeSourceTranscriptionConnection(
            transport,
            "test-safety",
            handshakeTimeout: ShortTimeout);

        var error = await Assert.ThrowsAsync<RealtimeTranslationException>(
            () => connection.StartAsync("sk-test", RealtimeSessionTuning.Default));

        Assert.Equal(RealtimeTranslationErrorKind.InvalidMessage, error.Kind);
        Assert.True(transport.CloseCount >= 1);
    }

    // Given: 空の API キー
    // When: 原文接続を開始する
    // Then: 送信前に MissingApiKey で失敗する
    [Fact]
    public async Task SourceConnectionRejectsBlankApiKey()
    {
        var transport = new FakeRealtimeServerTransport();
        var connection = new RealtimeSourceTranscriptionConnection(transport, "test-safety");

        var error = await Assert.ThrowsAsync<RealtimeTranslationException>(
            () => connection.StartAsync("   ", RealtimeSessionTuning.Default));

        Assert.Equal(RealtimeTranslationErrorKind.MissingApiKey, error.Kind);
        Assert.Equal(0, transport.ConnectCount);
    }

    // Given: 埋め込み改行と時刻が混ざったキー
    // When: 原文接続を開始する
    // Then: 送信前に AuthenticationFailed で失敗する
    [Fact]
    public async Task SourceConnectionRejectsMalformedApiKeyBeforeConnect()
    {
        var transport = new FakeRealtimeServerTransport();
        var connection = new RealtimeSourceTranscriptionConnection(transport, "test-safety");

        var error = await Assert.ThrowsAsync<RealtimeTranslationException>(
            () => connection.StartAsync("sk-proj-abc\n3:26", RealtimeSessionTuning.Default));

        Assert.Equal(RealtimeTranslationErrorKind.AuthenticationFailed, error.Kind);
        Assert.Equal(0, transport.ConnectCount);
        Assert.DoesNotContain("sk-", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("3:26", error.Message, StringComparison.Ordinal);
    }

    // Given: 行折り返しされた allowlist キー
    // When: 原文接続を開始する
    // Then: Authorization は正規化後のキーだけを載せる
    [Fact]
    public async Task SourceConnectionStripsEmbeddedWhitespaceFromApiKeyHeader()
    {
        var transport = new FakeRealtimeServerTransport();
        var connection = new RealtimeSourceTranscriptionConnection(transport, "test-safety");

        await connection.StartAsync("sk-proj-AAAA\nBBBB", RealtimeSessionTuning.Default);

        Assert.Equal("Bearer sk-proj-AAAABBBB", transport.ConnectedHeaders["Authorization"]);
        await connection.ForceCloseAsync();
    }

    // Given: ready 状態の原文接続
    // When: 復号できないメッセージが届く
    // Then: transport error として 1 度だけ通知しイベント流を閉じる
    [Fact]
    public async Task SourceConnectionReportsTransportErrorOnBrokenMessage()
    {
        var transport = new FakeRealtimeServerTransport();
        var connection = new RealtimeSourceTranscriptionConnection(transport, "test-safety");
        await connection.StartAsync("sk-test", RealtimeSessionTuning.Default);

        transport.EnqueueRaw(Encoding.UTF8.GetBytes("{not json"));
        var streamEvent = await ReadOneAsync(connection.Events);

        var error = Assert.IsType<RealtimeTranslationServerEvent.ServerError>(streamEvent.Event);
        Assert.Equal("transport", error.Code);
        Assert.Equal("原文字幕サーバーとの接続が切れました", error.Message);
        await connection.ForceCloseAsync();
    }

    // Given: ready な原文接続
    // When: 鍵断片を含む runtime error が届く
    // Then: 原文 codec は code を transcription に畳み、表示文言から秘密情報を除去する
    [Fact]
    public async Task SourceConnectionRuntimeAuthErrorRedactsKeyMaterial()
    {
        var transport = new FakeRealtimeServerTransport();
        var connection = new RealtimeSourceTranscriptionConnection(transport, "test-safety");
        await connection.StartAsync("sk-test", RealtimeSessionTuning.Default);

        transport.EnqueueJson(
            """{"type":"error","error":{"message":"Incorrect API key sk-runtime-xyz","code":"invalid_api_key"}}""");
        var streamEvent = await ReadOneAsync(connection.Events);

        var error = Assert.IsType<RealtimeTranslationServerEvent.ServerError>(streamEvent.Event);
        Assert.Equal(RealtimeSourceTranscriptionCodec.ErrorCode, error.Code);
        Assert.Equal("OpenAI APIキーが無効です", error.Message);
        Assert.DoesNotContain("sk-runtime-xyz", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-", error.Message, StringComparison.Ordinal);
        await connection.ForceCloseAsync();
    }

    // Given: far_field で接続した原文 transcription 接続
    // When: near_field を含む tuning を録音中に反映する
    // Then: noise_reduction は接続時の値を維持する
    [Fact]
    public async Task SourceConnectionKeepsConnectedNoiseReductionOnLiveUpdate()
    {
        var transport = new FakeRealtimeServerTransport();
        var connection = new RealtimeSourceTranscriptionConnection(transport, "test-safety");
        await connection.StartAsync(
            "sk-test",
            RealtimeSessionTuning.Default with { NoiseReduction = RealtimeTranslationNoiseReduction.FarField });

        await connection.UpdateTuningAsync(
            RealtimeSessionTuning.Default with { NoiseReduction = RealtimeTranslationNoiseReduction.NearField });

        var payload = JsonNode.Parse(transport.Sent[^1])!.AsObject();
        var noiseReduction = payload["session"]!["audio"]!["input"]!["noise_reduction"]!["type"]!.GetValue<string>();
        Assert.Equal("far_field", noiseReduction);
        await connection.ForceCloseAsync();
    }

    private static async Task<RealtimeTranslationStreamEvent> ReadOneAsync(
        System.Threading.Channels.ChannelReader<RealtimeTranslationStreamEvent> reader)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        return await reader.ReadAsync(timeout.Token);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail("condition was not met in time");
    }

    private static string? TypeOf(byte[] payload) =>
        JsonNode.Parse(payload)!.AsObject()["type"]?.GetValue<string>();
}
