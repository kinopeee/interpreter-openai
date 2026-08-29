using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using RealtimeTranslator.Core.Audio;
using RealtimeTranslator.Core.OpenAI;
using RealtimeTranslator.Core.Realtime;
using Xunit;

namespace RealtimeTranslator.Core.Tests;

/// <summary>
/// CloseGracefully の drain 待ちキャンセルと、ForceClose との競合。
/// drain helper 単体のキャンセル（LifecycleTests）や ForceClose 後の no-op とは交差しない。
/// </summary>
public sealed class DualRealtimeTranslationClientCloseCancelTests
{
    // Given: 翻訳送信が停滞して CloseGracefully が drain 待ち中
    // When: 呼び出し側 token をキャンセルする
    // Then: session.close へ進まず、Dual は稼働のまま。後続 Append は NotConnected にならない
    [Fact]
    public async Task CloseGracefullyCancelDuringDrainDoesNotTearDown()
    {
        var source = new FakeRealtimeServerTransport();
        var english = new FakeRealtimeServerTransport();
        var japanese = new FakeRealtimeServerTransport();
        using var dual = CreateDual(source, english, japanese, TimeSpan.FromSeconds(5));

        await dual.StartAsync("sk-test", RealtimeSessionTuning.Default);
        await dual.SelectTranslationTargetAsync(RealtimeTranslationOutputLanguage.English);
        english.SendDelay = TimeSpan.FromSeconds(30);
        await dual.AppendAudioFrameAsync(Frame(0x21));

        using var caller = new CancellationTokenSource();
        var closeTask = dual.CloseGracefullyAsync(caller.Token);
        await Task.Delay(80);
        await caller.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => closeTask);
        Assert.DoesNotContain("session.close", SentTypes(english));
        Assert.DoesNotContain("session.close", SentTypes(japanese));
        Assert.DoesNotContain("input_audio_buffer.commit", SentTypes(source));

        await dual.AppendAudioFrameAsync(Frame(0x22));
        Assert.Equal(2, source.AppendedFrameTexts().Count);
        await dual.ForceCloseAsync();
    }

    // Given: 翻訳送信が停滞して CloseGracefully が drain 待ち中
    // When: 並行して ForceClose する
    // Then: graceful 側は例外なく終わり、session.close は送らない（ForceClose が所有する）
    [Fact]
    public async Task ForceCloseDuringCloseGracefullyDrainSkipsSessionClose()
    {
        var source = new FakeRealtimeServerTransport { AutoCloseResponses = true };
        var english = new FakeRealtimeServerTransport { AutoCloseResponses = true };
        var japanese = new FakeRealtimeServerTransport { AutoCloseResponses = true };
        using var dual = CreateDual(
            source,
            english,
            japanese,
            TimeSpan.FromSeconds(2),
            translationDrainTimeout: TimeSpan.FromSeconds(2));

        await dual.StartAsync("sk-test", RealtimeSessionTuning.Default);
        await dual.SelectTranslationTargetAsync(RealtimeTranslationOutputLanguage.English);
        english.SendDelay = TimeSpan.FromSeconds(30);
        await dual.AppendAudioFrameAsync(Frame(0x31));

        var closeTask = dual.CloseGracefullyAsync();
        await Task.Delay(80);
        await dual.ForceCloseAsync();
        await closeTask;

        Assert.DoesNotContain("session.close", SentTypes(english));
        Assert.DoesNotContain("session.close", SentTypes(japanese));
        Assert.DoesNotContain("input_audio_buffer.commit", SentTypes(source));
        var appendError = await Assert.ThrowsAsync<RealtimeTranslationException>(
            () => dual.AppendAudioFrameAsync(Frame(0x32)));
        Assert.Equal(RealtimeTranslationErrorKind.NotConnected, appendError.Kind);
    }

    private static DualRealtimeTranslationClient CreateDual(
        FakeRealtimeServerTransport source,
        FakeRealtimeServerTransport english,
        FakeRealtimeServerTransport japanese,
        TimeSpan closeTimeout,
        TimeSpan? translationDrainTimeout = null) =>
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
            translationDrainTimeout: translationDrainTimeout);

    private static byte[] Frame(byte fill)
    {
        var frame = new byte[Pcm16FramePacketizer.BytesPerFrame];
        Array.Fill(frame, fill);
        return frame;
    }

    private static string[] SentTypes(FakeRealtimeServerTransport transport) =>
        transport.Sent
            .Select(payload => JsonNode.Parse(payload)?.AsObject()["type"]?.GetValue<string>() ?? string.Empty)
            .Where(type => type.Length > 0)
            .ToArray();
}
