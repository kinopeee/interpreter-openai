using System;
using System.Threading.Tasks;
using RealtimeTranslator.Core.Audio;
using RealtimeTranslator.Core.OpenAI;
using RealtimeTranslator.Core.Realtime;
using Xunit;

namespace RealtimeTranslator.Core.Tests;

/// <summary>
/// CloseGracefully / ForceClose を経ない Dual.Dispose の解放契約。
/// Drain / Lifecycle / Parity の開いている PR とはファイルを分けて衝突を避ける。
/// </summary>
public sealed class DualRealtimeTranslationClientDisposeTests
{
    // Given: Start 済みの Dual
    // When: Close せず Dispose し、続けて Append / Select する
    // Then: Dispose は投げず、未接続として拒否する。二重 Dispose も投げない
    [Fact]
    public async Task DisposeAfterStartRejectsFurtherAppendAndIsIdempotent()
    {
        var source = new FakeRealtimeServerTransport();
        var english = new FakeRealtimeServerTransport();
        var japanese = new FakeRealtimeServerTransport();
        var dual = CreateDual(source, english, japanese);

        await dual.StartAsync("sk-test", RealtimeSessionTuning.Default);
        dual.Dispose();
        dual.Dispose();

        var appendError = await Assert.ThrowsAsync<RealtimeTranslationException>(
            () => dual.AppendAudioFrameAsync(Frame(0x11)));
        var selectError = await Assert.ThrowsAsync<RealtimeTranslationException>(
            () => dual.SelectTranslationTargetAsync(RealtimeTranslationOutputLanguage.English));

        Assert.Equal(RealtimeTranslationErrorKind.NotConnected, appendError.Kind);
        Assert.Equal(RealtimeTranslationErrorKind.NotConnected, selectError.Kind);
    }

    // Given: 翻訳送信が停滞している Dual
    // When: drain / ForceClose を待たずに Dispose する
    // Then: ポンプ待ちで投げず、プロセス終了経路を塞がない
    [Fact]
    public async Task DisposeDuringStalledTranslationSendDoesNotThrow()
    {
        var source = new FakeRealtimeServerTransport();
        var english = new FakeRealtimeServerTransport();
        var japanese = new FakeRealtimeServerTransport();
        var dual = CreateDual(source, english, japanese);

        await dual.StartAsync("sk-test", RealtimeSessionTuning.Default);
        english.SendDelay = TimeSpan.FromSeconds(30);
        await dual.SelectTranslationTargetAsync(RealtimeTranslationOutputLanguage.English);
        await dual.AppendAudioFrameAsync(Frame(0x22));

        var dispose = Record.Exception(dual.Dispose);

        Assert.Null(dispose);
    }

    private static DualRealtimeTranslationClient CreateDual(
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

    private static byte[] Frame(byte fill)
    {
        var frame = new byte[Pcm16FramePacketizer.BytesPerFrame];
        Array.Fill(frame, fill);
        return frame;
    }
}
