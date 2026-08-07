using System;
using System.Threading.Tasks;
using RealtimeTranslator.Core.Audio;
using RealtimeTranslator.Core.OpenAI;
using RealtimeTranslator.Core.Realtime;
using Xunit;

namespace RealtimeTranslator.Core.Tests;

public sealed class DualRealtimeTranslationClientDrainTests
{
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
}
