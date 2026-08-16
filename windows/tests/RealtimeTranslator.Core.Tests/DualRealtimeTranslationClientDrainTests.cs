using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using RealtimeTranslator.Core.Audio;
using RealtimeTranslator.Core.OpenAI;
using RealtimeTranslator.Core.Realtime;
using Xunit;

namespace RealtimeTranslator.Core.Tests;

public sealed class DualRealtimeTranslationClientDrainTests
{
    private static readonly TimeSpan ShortCloseTimeout = TimeSpan.FromMilliseconds(300);

    [Fact]
    public async Task WaitForTranslationDrainTimesOutWhenSendStalls()
    {
        // Given: 翻訳送信が完了しない dual（言語判定済みで翻訳 lane へ流れる）
        var source = new FakeRealtimeServerTransport();
        var english = new FakeRealtimeServerTransport();
        var japanese = new FakeRealtimeServerTransport();
        using var dual = new DualRealtimeTranslationClient(
            new RealtimeSourceTranscriptionConnection(source, "test-safety"),
            new RealtimeTranslationConnection(RealtimeTranslationOutputLanguage.English, english, "test-safety"),
            new RealtimeTranslationConnection(RealtimeTranslationOutputLanguage.Japanese, japanese, "test-safety"));

        await dual.StartAsync("sk-test", RealtimeSessionTuning.Default);
        // handshake 後に遅延を入れ、接続確立自体はブロックしない。
        english.SendDelay = TimeSpan.FromSeconds(30);
        await dual.SelectTranslationTargetAsync(RealtimeTranslationOutputLanguage.English);
        var frame = new byte[Pcm16FramePacketizer.BytesPerFrame];
        Array.Fill(frame, (byte)0x11);
        await dual.AppendAudioFrameAsync(frame);

        // When: 短い timeout で drain を待つ
        // Then: ポンプ待ちでハングせず、TimeoutException になる
        var error = await Assert.ThrowsAsync<TimeoutException>(
            () => dual.WaitForTranslationDrainAsync(TimeSpan.FromMilliseconds(50)));
        Assert.Equal("translation pump did not drain", error.Message);
        await dual.ForceCloseAsync();
    }

    // Given: 英語 target の送信が長時間停滞する dual
    // When: 翻訳停滞中に frame を連続 Append する
    // Then: 原文側は翻訳完了を待たず 3 frame を受け取る
    [Fact]
    public async Task SourceAppendContinuesWhenTranslationSendHangs()
    {
        var source = new FakeRealtimeServerTransport();
        var english = new FakeRealtimeServerTransport();
        var japanese = new FakeRealtimeServerTransport();
        using var dual = new DualRealtimeTranslationClient(
            new RealtimeSourceTranscriptionConnection(source, "test-safety"),
            new RealtimeTranslationConnection(RealtimeTranslationOutputLanguage.English, english, "test-safety"),
            new RealtimeTranslationConnection(RealtimeTranslationOutputLanguage.Japanese, japanese, "test-safety"));

        await dual.StartAsync("sk-test", RealtimeSessionTuning.Default);
        var frameA = Frame(0x55);
        var frameB = Frame(0x66);
        var frameC = Frame(0x77);
        await dual.AppendAudioFrameAsync(frameA);
        await dual.SelectTranslationTargetAsync(RealtimeTranslationOutputLanguage.English);
        english.SendDelay = TimeSpan.FromSeconds(30);

        // When: 翻訳停滞中でも原文へ連続送信できる
        var appendWhileTranslationHangs = Task.WhenAll(
            dual.AppendAudioFrameAsync(frameB),
            dual.AppendAudioFrameAsync(frameC));
        var winner = await Task.WhenAny(appendWhileTranslationHangs, Task.Delay(TimeSpan.FromSeconds(1)));
        Assert.Same(appendWhileTranslationHangs, winner);
        await appendWhileTranslationHangs;

        // Then: 原文側は翻訳停滞を待たず 3 frame を受け取る
        Assert.Equal(3, source.AppendedFrameTexts().Count);
        await dual.ForceCloseAsync();
    }

    // Given: 翻訳 lane へ未送信 frame が溜まっている dual（#42 の drain-before-clear）
    // When: CloseGracefullyAsync する
    // Then: session.close より前に溜まった frame が英語 target へ送られる
    [Fact]
    public async Task CloseGracefullyDrainsPendingTranslationFramesBeforeSessionClose()
    {
        var source = new FakeRealtimeServerTransport { AutoCloseResponses = true };
        var english = new FakeRealtimeServerTransport { AutoCloseResponses = true };
        var japanese = new FakeRealtimeServerTransport { AutoCloseResponses = true };
        using var dual = CreateDual(source, english, japanese, ShortCloseTimeout);

        await dual.StartAsync("sk-test", RealtimeSessionTuning.Default);
        await dual.SelectTranslationTargetAsync(RealtimeTranslationOutputLanguage.English);

        // 送信を少し遅らせて pending を作り、CloseGracefully の drain 待ちに載せる。
        english.SendDelay = TimeSpan.FromMilliseconds(120);
        await dual.AppendAudioFrameAsync(Frame(0x21));
        await dual.AppendAudioFrameAsync(Frame(0x22));
        await dual.AppendAudioFrameAsync(Frame(0x23));
        english.SendDelay = TimeSpan.Zero;

        await dual.CloseGracefullyAsync();

        var englishTypes = SentTypes(english);
        var lastAppendIndex = englishTypes.FindLastIndex(type => type == "session.input_audio_buffer.append");
        var closeIndex = englishTypes.FindLastIndex(type => type == "session.close");
        Assert.Equal(3, english.AppendedFrameTexts().Count);
        Assert.True(lastAppendIndex >= 0);
        Assert.True(closeIndex > lastAppendIndex);
        // close 応答で残ったイベントを吸い切ったあと、停止側の WaitToReadAsync が終了できること。
        while (dual.Events.TryRead(out _))
        {
        }

        Assert.False(await dual.Events.WaitToReadAsync());
    }

    // Given: 購読停止中に output_audio.delta が大量到着したあと訳文 delta が届く（#42 stop-drain 窓）
    // When: CloseGracefullyAsync して Events を吸い取る
    // Then: 音声 delta は dual channel に残らず、訳文 delta が DropOldest で消えない
    [Fact]
    public async Task CloseDrainPreservesTranscriptDespiteOutputAudioFlood()
    {
        var source = new FakeRealtimeServerTransport { AutoCloseResponses = true };
        var english = new FakeRealtimeServerTransport { AutoCloseResponses = true };
        var japanese = new FakeRealtimeServerTransport { AutoCloseResponses = true };
        using var dual = CreateDual(source, english, japanese, ShortCloseTimeout);

        await dual.StartAsync("sk-test", RealtimeSessionTuning.Default);
        await dual.SelectTranslationTargetAsync(RealtimeTranslationOutputLanguage.English);

        for (var index = 0; index < 600; index += 1)
        {
            english.EnqueueJson("""{"type":"session.output_audio.delta","delta":"AAAA"}""");
        }

        english.EnqueueJson(
            """{"type":"session.output_transcript.delta","delta":"drain survivor","event_id":"drain-1"}""");

        // merge が洪水を処理する猶予。購読側は意図的に読まない（Stop 後の世代フェンス相当）。
        await Task.Delay(200);

        await dual.CloseGracefullyAsync();

        var transcripts = new List<string>();
        while (await dual.Events.WaitToReadAsync())
        {
            while (dual.Events.TryRead(out var streamEvent))
            {
                Assert.IsNotType<RealtimeTranslationServerEvent.OutputAudioDelta>(streamEvent.Event);
                if (streamEvent.Event is RealtimeTranslationServerEvent.OutputTranscriptDelta transcript)
                {
                    transcripts.Add(transcript.Delta);
                }
            }
        }

        Assert.Contains("drain survivor", transcripts);
    }

    // Given: session.closed / transcription completed を返さない dual
    // When: CloseGracefullyAsync が CloseTimeout になる
    // Then: 例外を返しても Events channel は完了し、停止側の drain 待ちが固まらない
    [Fact]
    public async Task CloseGracefullyCompletesEventsChannelAfterCloseTimeout()
    {
        var source = new FakeRealtimeServerTransport();
        var english = new FakeRealtimeServerTransport();
        var japanese = new FakeRealtimeServerTransport();
        using var dual = CreateDual(source, english, japanese, ShortCloseTimeout);

        await dual.StartAsync("sk-test", RealtimeSessionTuning.Default);
        await dual.SelectTranslationTargetAsync(RealtimeTranslationOutputLanguage.English);

        var error = await Assert.ThrowsAsync<RealtimeTranslationException>(
            () => dual.CloseGracefullyAsync());

        Assert.Equal(RealtimeTranslationErrorKind.CloseTimeout, error.Kind);
        Assert.Contains("session.close", SentTypes(english));
        Assert.Contains("session.close", SentTypes(japanese));
        Assert.Contains("input_audio_buffer.commit", SentTypes(source));
        while (dual.Events.TryRead(out _))
        {
        }

        Assert.False(await dual.Events.WaitToReadAsync());
    }

    // Given: 英語 lane の CloseAsync だけが失敗する ready Dual
    // When: CloseGracefullyAsync する
    // Then: 例外を伝播しても原文・日本語 lane は閉じ、Events は完了する
    [Fact]
    public async Task CloseGracefullyContinuesWhenOneConnectionThrows()
    {
        var source = new FakeRealtimeServerTransport { AutoCloseResponses = true };
        var english = new FakeRealtimeServerTransport { AutoCloseResponses = true };
        var japanese = new FakeRealtimeServerTransport { AutoCloseResponses = true };
        using var dual = CreateDual(source, english, japanese, ShortCloseTimeout);

        await dual.StartAsync("sk-test", RealtimeSessionTuning.Default);
        await dual.SelectTranslationTargetAsync(RealtimeTranslationOutputLanguage.English);
        var closeCountBefore = (
            Source: source.CloseCount,
            English: english.CloseCount,
            Japanese: japanese.CloseCount);
        // Start 内の初期 ForceClose を避け、ready 後にだけ Close 失敗を注入する。
        english.CloseError = new InvalidOperationException("english close boom");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => dual.CloseGracefullyAsync());

        Assert.Equal("english close boom", error.Message);
        Assert.Contains("session.close", SentTypes(english));
        Assert.Contains("session.close", SentTypes(japanese));
        Assert.Contains("input_audio_buffer.commit", SentTypes(source));
        Assert.True(source.CloseCount > closeCountBefore.Source);
        Assert.True(english.CloseCount > closeCountBefore.English);
        Assert.True(japanese.CloseCount > closeCountBefore.Japanese);
        while (dual.Events.TryRead(out _))
        {
        }

        Assert.False(await dual.Events.WaitToReadAsync());
    }

    // Given: base drain timeout だけでは送り切れない程度の pending と緩い送信遅延
    // When: CloseGracefullyAsync する（pending 比例で予算が伸びる）
    // Then: session.close より前に全 frame が翻訳 lane へ届く（短い固定 timeout だと欠落する）
    [Fact]
    public async Task CloseGracefullyScalesDrainTimeoutWithPendingFrames()
    {
        var source = new FakeRealtimeServerTransport { AutoCloseResponses = true };
        var english = new FakeRealtimeServerTransport { AutoCloseResponses = true };
        var japanese = new FakeRealtimeServerTransport { AutoCloseResponses = true };
        using var dual = CreateDual(
            source,
            english,
            japanese,
            ShortCloseTimeout,
            translationDrainTimeout: TimeSpan.FromMilliseconds(200));

        await dual.StartAsync("sk-test", RealtimeSessionTuning.Default);
        await dual.SelectTranslationTargetAsync(RealtimeTranslationOutputLanguage.English);
        english.SendDelay = TimeSpan.FromMilliseconds(80);
        for (var index = 0; index < 6; index += 1)
        {
            await dual.AppendAudioFrameAsync(Frame((byte)(0x40 + index)));
        }

        await dual.CloseGracefullyAsync();

        Assert.Equal(6, english.AppendedFrameTexts().Count);
        var englishTypes = SentTypes(english);
        var lastAppendIndex = englishTypes.FindLastIndex(type => type == "session.input_audio_buffer.append");
        var closeIndex = englishTypes.FindLastIndex(type => type == "session.close");
        Assert.True(lastAppendIndex >= 0);
        Assert.True(closeIndex > lastAppendIndex);
    }

    // Given: ResolveTranslationDrainTimeout の base / pending / cap
    // When: 各境界値で計算する
    // Then: base を下限、cap を上限、pending 比例の加算になる
    [Fact]
    public void ResolveTranslationDrainTimeoutScalesAndCaps()
    {
        var baseTimeout = TimeSpan.FromSeconds(5);
        Assert.Equal(
            baseTimeout,
            DualRealtimeTranslationClient.ResolveTranslationDrainTimeout(baseTimeout, pendingFrameCount: 0));
        Assert.Equal(
            TimeSpan.FromMilliseconds(5_000 + (40 * 250)),
            DualRealtimeTranslationClient.ResolveTranslationDrainTimeout(baseTimeout, pendingFrameCount: 40));
        Assert.Equal(
            DualRealtimeTranslationClient.TranslationDrainTimeoutCap,
            DualRealtimeTranslationClient.ResolveTranslationDrainTimeout(baseTimeout, pendingFrameCount: 200));
        Assert.Equal(
            TimeSpan.FromMilliseconds(50),
            DualRealtimeTranslationClient.ResolveTranslationDrainTimeout(
                TimeSpan.FromMilliseconds(50),
                pendingFrameCount: 0));
    }

    // Given: 翻訳送信が停滞して pending frame が drain できない dual
    // When: CloseGracefullyAsync する（短い translationDrainTimeout）
    // Then: TimeoutException を外へ出さず session.close / commit へ進む
    [Fact]
    public async Task CloseGracefullyProceedsWhenTranslationDrainTimesOut()
    {
        var source = new FakeRealtimeServerTransport { AutoCloseResponses = true };
        var english = new FakeRealtimeServerTransport { AutoCloseResponses = true };
        var japanese = new FakeRealtimeServerTransport { AutoCloseResponses = true };
        using var dual = CreateDual(
            source,
            english,
            japanese,
            ShortCloseTimeout,
            translationDrainTimeout: TimeSpan.FromMilliseconds(50));

        await dual.StartAsync("sk-test", RealtimeSessionTuning.Default);
        await dual.SelectTranslationTargetAsync(RealtimeTranslationOutputLanguage.English);
        english.SendDelay = TimeSpan.FromSeconds(30);
        await dual.AppendAudioFrameAsync(Frame(0x31));
        await dual.AppendAudioFrameAsync(Frame(0x32));

        var closeTask = dual.CloseGracefullyAsync();
        var winner = await Task.WhenAny(closeTask, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(closeTask, winner);
        await closeTask;

        Assert.Contains("session.close", SentTypes(english));
        Assert.Contains("session.close", SentTypes(japanese));
        Assert.Contains("input_audio_buffer.commit", SentTypes(source));
        while (dual.Events.TryRead(out _))
        {
        }

        Assert.False(await dual.Events.WaitToReadAsync());
    }

    // Given: スペイン語接続を渡していない dual
    // When: スペイン語を含む pair で Start する
    // Then: KeyNotFoundException ではなく、必要な接続が無い旨の ArgumentException になる
    [Theory]
    [InlineData(LanguagePair.JaEs)]
    [InlineData(LanguagePair.EnEs)]
    public async Task StartWithSpanishPairWithoutSpanishConnectionFailsClearly(LanguagePair pair)
    {
        var source = new FakeRealtimeServerTransport();
        var english = new FakeRealtimeServerTransport();
        var japanese = new FakeRealtimeServerTransport();
        using var dual = new DualRealtimeTranslationClient(
            new RealtimeSourceTranscriptionConnection(source, "test-safety"),
            new RealtimeTranslationConnection(RealtimeTranslationOutputLanguage.English, english, "test-safety"),
            new RealtimeTranslationConnection(RealtimeTranslationOutputLanguage.Japanese, japanese, "test-safety"));

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            dual.StartAsync("sk-test", RealtimeSessionTuning.Default, pair));

        Assert.Equal("pair", error.ParamName);
        Assert.Contains("es", error.Message, StringComparison.Ordinal);
    }

    // Given: ja-en で開始した dual（Spanish 接続なし）
    // When: Spanish target を選ぶ
    // Then: KeyNotFoundException ではなく、未構成接続である旨の ArgumentException になる
    [Fact]
    public async Task SelectSpanishTargetWithoutSpanishConnectionFailsClearly()
    {
        var source = new FakeRealtimeServerTransport();
        var english = new FakeRealtimeServerTransport();
        var japanese = new FakeRealtimeServerTransport();
        using var dual = new DualRealtimeTranslationClient(
            new RealtimeSourceTranscriptionConnection(source, "test-safety"),
            new RealtimeTranslationConnection(RealtimeTranslationOutputLanguage.English, english, "test-safety"),
            new RealtimeTranslationConnection(RealtimeTranslationOutputLanguage.Japanese, japanese, "test-safety"));

        await dual.StartAsync("sk-test", RealtimeSessionTuning.Default, LanguagePair.JaEn);

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            dual.SelectTranslationTargetAsync(RealtimeTranslationOutputLanguage.Spanish));

        Assert.Equal("target", error.ParamName);
        Assert.Contains("es", error.Message, StringComparison.Ordinal);
        await dual.ForceCloseAsync();
    }

    // Given: ja-es で Spanish target に pending frame がある dual（未使用 English lane は未接続）
    // When: CloseGracefullyAsync する
    // Then: session.close より前に Spanish lane へ全 frame が届き、未使用 lane で止まらず Events が完了する
    [Fact]
    public async Task CloseGracefullyDrainsSpanishPendingFramesForJaEsPair()
    {
        var source = new FakeRealtimeServerTransport { AutoCloseResponses = true };
        var english = new FakeRealtimeServerTransport { AutoCloseResponses = true };
        var japanese = new FakeRealtimeServerTransport { AutoCloseResponses = true };
        var spanish = new FakeRealtimeServerTransport { AutoCloseResponses = true };
        using var dual = CreateDual(source, english, japanese, ShortCloseTimeout, spanish: spanish);

        await dual.StartAsync("sk-test", RealtimeSessionTuning.Default, LanguagePair.JaEs);
        await dual.SelectTranslationTargetAsync(RealtimeTranslationOutputLanguage.Spanish);

        spanish.SendDelay = TimeSpan.FromMilliseconds(120);
        await dual.AppendAudioFrameAsync(Frame(0xA1));
        await dual.AppendAudioFrameAsync(Frame(0xA2));
        await dual.AppendAudioFrameAsync(Frame(0xA3));
        spanish.SendDelay = TimeSpan.Zero;

        var closeTask = dual.CloseGracefullyAsync();
        var winner = await Task.WhenAny(closeTask, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(closeTask, winner);
        await closeTask;

        Assert.Equal(0, english.ConnectCount);
        Assert.Equal(3, spanish.AppendedFrameTexts().Count);
        var spanishTypes = SentTypes(spanish);
        var lastAppendIndex = spanishTypes.FindLastIndex(type => type == "session.input_audio_buffer.append");
        var closeIndex = spanishTypes.FindLastIndex(type => type == "session.close");
        Assert.True(lastAppendIndex >= 0);
        Assert.True(closeIndex > lastAppendIndex);
        Assert.Contains("session.close", SentTypes(japanese));
        Assert.DoesNotContain("session.close", SentTypes(english));
        while (dual.Events.TryRead(out _))
        {
        }

        Assert.False(await dual.Events.WaitToReadAsync());
    }

    // Given: en-es で English target に pending frame がある dual（未使用 Japanese lane は未接続）
    // When: CloseGracefullyAsync する
    // Then: English lane へ drain したあと close し、未使用 Japanese へ session.close を送らない
    [Fact]
    public async Task CloseGracefullyDrainsEnglishPendingFramesForEnEsPair()
    {
        var source = new FakeRealtimeServerTransport { AutoCloseResponses = true };
        var english = new FakeRealtimeServerTransport { AutoCloseResponses = true };
        var japanese = new FakeRealtimeServerTransport { AutoCloseResponses = true };
        var spanish = new FakeRealtimeServerTransport { AutoCloseResponses = true };
        using var dual = CreateDual(source, english, japanese, ShortCloseTimeout, spanish: spanish);

        await dual.StartAsync("sk-test", RealtimeSessionTuning.Default, LanguagePair.EnEs);
        await dual.SelectTranslationTargetAsync(RealtimeTranslationOutputLanguage.English);

        english.SendDelay = TimeSpan.FromMilliseconds(120);
        await dual.AppendAudioFrameAsync(Frame(0xB1));
        await dual.AppendAudioFrameAsync(Frame(0xB2));
        english.SendDelay = TimeSpan.Zero;

        await dual.CloseGracefullyAsync();

        Assert.Equal(0, japanese.ConnectCount);
        Assert.Equal(2, english.AppendedFrameTexts().Count);
        var englishTypes = SentTypes(english);
        var lastAppendIndex = englishTypes.FindLastIndex(type => type == "session.input_audio_buffer.append");
        var closeIndex = englishTypes.FindLastIndex(type => type == "session.close");
        Assert.True(lastAppendIndex >= 0);
        Assert.True(closeIndex > lastAppendIndex);
        Assert.Contains("session.close", SentTypes(spanish));
        Assert.DoesNotContain("session.close", SentTypes(japanese));
        while (dual.Events.TryRead(out _))
        {
        }

        Assert.False(await dual.Events.WaitToReadAsync());
    }

    // Given: ja-es で Japanese target に pending frame がある dual（未使用 English lane は未接続）
    // When: CloseGracefullyAsync する
    // Then: Japanese lane へ drain したあと close し、未使用 English へ session.close を送らない
    [Fact]
    public async Task CloseGracefullyDrainsJapanesePendingFramesForJaEsPair()
    {
        var source = new FakeRealtimeServerTransport { AutoCloseResponses = true };
        var english = new FakeRealtimeServerTransport { AutoCloseResponses = true };
        var japanese = new FakeRealtimeServerTransport { AutoCloseResponses = true };
        var spanish = new FakeRealtimeServerTransport { AutoCloseResponses = true };
        using var dual = CreateDual(source, english, japanese, ShortCloseTimeout, spanish: spanish);

        await dual.StartAsync("sk-test", RealtimeSessionTuning.Default, LanguagePair.JaEs);
        await dual.SelectTranslationTargetAsync(RealtimeTranslationOutputLanguage.Japanese);

        japanese.SendDelay = TimeSpan.FromMilliseconds(120);
        await dual.AppendAudioFrameAsync(Frame(0xC1));
        await dual.AppendAudioFrameAsync(Frame(0xC2));
        japanese.SendDelay = TimeSpan.Zero;

        await dual.CloseGracefullyAsync();

        Assert.Equal(0, english.ConnectCount);
        Assert.Equal(2, japanese.AppendedFrameTexts().Count);
        var japaneseTypes = SentTypes(japanese);
        var lastAppendIndex = japaneseTypes.FindLastIndex(type => type == "session.input_audio_buffer.append");
        var closeIndex = japaneseTypes.FindLastIndex(type => type == "session.close");
        Assert.True(lastAppendIndex >= 0);
        Assert.True(closeIndex > lastAppendIndex);
        Assert.Contains("session.close", SentTypes(spanish));
        Assert.DoesNotContain("session.close", SentTypes(english));
        while (dual.Events.TryRead(out _))
        {
        }

        Assert.False(await dual.Events.WaitToReadAsync());
    }

    // Given: en-es で Spanish target に pending frame がある dual（未使用 Japanese lane は未接続）
    // When: CloseGracefullyAsync する
    // Then: Spanish lane へ drain したあと close し、未使用 Japanese へ session.close を送らない
    [Fact]
    public async Task CloseGracefullyDrainsSpanishPendingFramesForEnEsPair()
    {
        var source = new FakeRealtimeServerTransport { AutoCloseResponses = true };
        var english = new FakeRealtimeServerTransport { AutoCloseResponses = true };
        var japanese = new FakeRealtimeServerTransport { AutoCloseResponses = true };
        var spanish = new FakeRealtimeServerTransport { AutoCloseResponses = true };
        using var dual = CreateDual(source, english, japanese, ShortCloseTimeout, spanish: spanish);

        await dual.StartAsync("sk-test", RealtimeSessionTuning.Default, LanguagePair.EnEs);
        await dual.SelectTranslationTargetAsync(RealtimeTranslationOutputLanguage.Spanish);

        spanish.SendDelay = TimeSpan.FromMilliseconds(120);
        await dual.AppendAudioFrameAsync(Frame(0xD1));
        await dual.AppendAudioFrameAsync(Frame(0xD2));
        await dual.AppendAudioFrameAsync(Frame(0xD3));
        spanish.SendDelay = TimeSpan.Zero;

        await dual.CloseGracefullyAsync();

        Assert.Equal(0, japanese.ConnectCount);
        Assert.Equal(3, spanish.AppendedFrameTexts().Count);
        var spanishTypes = SentTypes(spanish);
        var lastAppendIndex = spanishTypes.FindLastIndex(type => type == "session.input_audio_buffer.append");
        var closeIndex = spanishTypes.FindLastIndex(type => type == "session.close");
        Assert.True(lastAppendIndex >= 0);
        Assert.True(closeIndex > lastAppendIndex);
        Assert.Contains("session.close", SentTypes(english));
        Assert.DoesNotContain("session.close", SentTypes(japanese));
        while (dual.Events.TryRead(out _))
        {
        }

        Assert.False(await dual.Events.WaitToReadAsync());
    }

    // Given: ja-es で Spanish lane の CloseAsync だけが失敗する ready Dual
    // When: CloseGracefullyAsync する
    // Then: 例外を伝播しても原文・日本語 lane は閉じ、未使用 English で止まらず Events は完了する
    [Fact]
    public async Task CloseGracefullyContinuesWhenSpanishConnectionThrows()
    {
        var source = new FakeRealtimeServerTransport { AutoCloseResponses = true };
        var english = new FakeRealtimeServerTransport { AutoCloseResponses = true };
        var japanese = new FakeRealtimeServerTransport { AutoCloseResponses = true };
        var spanish = new FakeRealtimeServerTransport { AutoCloseResponses = true };
        using var dual = CreateDual(source, english, japanese, ShortCloseTimeout, spanish: spanish);

        await dual.StartAsync("sk-test", RealtimeSessionTuning.Default, LanguagePair.JaEs);
        await dual.SelectTranslationTargetAsync(RealtimeTranslationOutputLanguage.Spanish);
        var closeCountBefore = (
            Source: source.CloseCount,
            Japanese: japanese.CloseCount,
            Spanish: spanish.CloseCount);
        spanish.CloseError = new InvalidOperationException("spanish close boom");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => dual.CloseGracefullyAsync());

        Assert.Equal("spanish close boom", error.Message);
        Assert.Equal(0, english.ConnectCount);
        Assert.Contains("session.close", SentTypes(spanish));
        Assert.Contains("session.close", SentTypes(japanese));
        Assert.Contains("input_audio_buffer.commit", SentTypes(source));
        Assert.DoesNotContain("session.close", SentTypes(english));
        Assert.True(source.CloseCount > closeCountBefore.Source);
        Assert.True(japanese.CloseCount > closeCountBefore.Japanese);
        Assert.True(spanish.CloseCount > closeCountBefore.Spanish);
        while (dual.Events.TryRead(out _))
        {
        }

        Assert.False(await dual.Events.WaitToReadAsync());
    }

    private static DualRealtimeTranslationClient CreateDual(
        FakeRealtimeServerTransport source,
        FakeRealtimeServerTransport english,
        FakeRealtimeServerTransport japanese,
        TimeSpan closeTimeout,
        TimeSpan? translationDrainTimeout = null,
        FakeRealtimeServerTransport? spanish = null) =>
        new(
            new RealtimeSourceTranscriptionConnection(source, "test-safety", closeTimeout: closeTimeout),
            new RealtimeTranslationConnection(
                RealtimeTranslationOutputLanguage.English,
                english,
                "test-safety",
                closeTimeout: closeTimeout),
            new RealtimeTranslationConnection(
                RealtimeTranslationOutputLanguage.Japanese,
                japanese,
                "test-safety",
                closeTimeout: closeTimeout),
            translationDrainTimeout: translationDrainTimeout,
            spanishConnection: spanish is null
                ? null
                : new RealtimeTranslationConnection(
                    RealtimeTranslationOutputLanguage.Spanish,
                    spanish,
                    "test-safety",
                    closeTimeout: closeTimeout));

    private static byte[] Frame(byte fill)
    {
        var frame = new byte[Pcm16FramePacketizer.BytesPerFrame];
        Array.Fill(frame, fill);
        return frame;
    }

    private static List<string> SentTypes(FakeRealtimeServerTransport transport) =>
        transport.Sent
            .Select(payload => JsonNode.Parse(payload)?.AsObject()["type"]?.GetValue<string>() ?? string.Empty)
            .Where(type => type.Length > 0)
            .ToList();
}
