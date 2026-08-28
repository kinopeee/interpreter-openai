using System;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using RealtimeTranslator.Core.Audio;
using RealtimeTranslator.Core.OpenAI;
using RealtimeTranslator.Core.Realtime;
using Xunit;

namespace RealtimeTranslator.Core.Tests;

/// <summary>
/// 言語未選択のまま停止した Dual の lane close 契約。
/// 選択 target だけ閉じると未使用 socket が漏れ、次セッションが切れる。
/// </summary>
public sealed class DualRealtimeTranslationClientUnselectedCloseTests
{
    // Given: JaEn で Start 済みだが target を一度も選んでいない Dual
    // When: CloseGracefully する
    // Then: 原文は commit、翻訳両 lane は session.close、Events は完了し、再 Append は NotConnected
    [Fact]
    public async Task CloseGracefullyWithoutSelectedTargetClosesSourceAndBothTranslationLanes()
    {
        var source = new FakeRealtimeServerTransport { AutoCloseResponses = true };
        var english = new FakeRealtimeServerTransport { AutoCloseResponses = true };
        var japanese = new FakeRealtimeServerTransport { AutoCloseResponses = true };
        using var dual = new DualRealtimeTranslationClient(
            new RealtimeSourceTranscriptionConnection(source, "test-safety"),
            new RealtimeTranslationConnection(
                RealtimeTranslationOutputLanguage.English,
                english,
                "test-safety"),
            new RealtimeTranslationConnection(
                RealtimeTranslationOutputLanguage.Japanese,
                japanese,
                "test-safety"));

        await dual.StartAsync("sk-test", RealtimeSessionTuning.Default);
        await dual.AppendAudioFrameAsync(Frame(0x21));

        await dual.CloseGracefullyAsync();

        Assert.Contains(source.Sent, payload => TypeOf(payload) == "input_audio_buffer.commit");
        Assert.Contains(english.Sent, payload => TypeOf(payload) == "session.close");
        Assert.Contains(japanese.Sent, payload => TypeOf(payload) == "session.close");
        Assert.Empty(english.AppendedFrameTexts());
        Assert.Empty(japanese.AppendedFrameTexts());
        Assert.True(source.CloseCount >= 1);
        Assert.True(english.CloseCount >= 1);
        Assert.True(japanese.CloseCount >= 1);

        while (dual.Events.TryRead(out _))
        {
        }

        Assert.False(await dual.Events.WaitToReadAsync());

        var appendError = await Assert.ThrowsAsync<RealtimeTranslationException>(
            () => dual.AppendAudioFrameAsync(Frame(0x22)));
        Assert.Equal(RealtimeTranslationErrorKind.NotConnected, appendError.Kind);
    }

    private static byte[] Frame(byte fill)
    {
        var frame = new byte[Pcm16FramePacketizer.BytesPerFrame];
        Array.Fill(frame, fill);
        return frame;
    }

    private static string? TypeOf(byte[] payload) =>
        JsonNode.Parse(payload)?.AsObject()["type"]?.GetValue<string>();
}
