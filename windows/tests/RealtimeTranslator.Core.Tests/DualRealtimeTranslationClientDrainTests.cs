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
        await dual.SetSpokenLanguageAsync(SpokenLanguage.Japanese);
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
        await dual.SetSpokenLanguageAsync(SpokenLanguage.Japanese);
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
        await dual.SetSpokenLanguageAsync(SpokenLanguage.Japanese);

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
        await dual.SetSpokenLanguageAsync(SpokenLanguage.Japanese);

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
        await dual.SetSpokenLanguageAsync(SpokenLanguage.Japanese);

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

    private static DualRealtimeTranslationClient CreateDual(
        FakeRealtimeServerTransport source,
        FakeRealtimeServerTransport english,
        FakeRealtimeServerTransport japanese,
        TimeSpan closeTimeout) =>
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
