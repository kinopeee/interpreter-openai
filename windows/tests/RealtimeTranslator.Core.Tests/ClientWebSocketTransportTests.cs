using RealtimeTranslator.Core.OpenAI;
using RealtimeTranslator.Core.Realtime;
using Xunit;

namespace RealtimeTranslator.Core.Tests;

/// <summary>受信メッセージの累積上限。巨大応答で managed memory を食い潰させない。</summary>
public sealed class ClientWebSocketTransportTests
{
    // Given: 上限直下まで累積した受信状態
    // When: 残り容量ぴったりの fragment が届く
    // Then: 例外なく受け入れる
    [Fact]
    public void AcceptsFragmentThatExactlyFillsMessageLimit()
    {
        ClientWebSocketTransport.EnsureWithinMessageLimit(
            ClientWebSocketTransport.MaxMessageBytes - 1024,
            1024);
    }

    // Given: 上限直下まで累積した受信状態
    // When: 残り容量を 1 byte 超える fragment が届く
    // Then: 回復可能な transport error として弾く
    [Fact]
    public void RejectsFragmentThatExceedsMessageLimit()
    {
        var exception = Assert.Throws<RealtimeTranslationException>(() =>
            ClientWebSocketTransport.EnsureWithinMessageLimit(
                ClientWebSocketTransport.MaxMessageBytes - 1024,
                1025));

        Assert.Equal(RealtimeTranslationErrorKind.RecoverableTransportFailure, exception.Kind);
    }

    // Given: 未受信の状態
    // When: 上限を超える単一の巨大 frame が届く
    // Then: 確保・parse する前に弾く
    [Fact]
    public void RejectsSingleOversizedFrame()
    {
        Assert.Throws<RealtimeTranslationException>(() =>
            ClientWebSocketTransport.EnsureWithinMessageLimit(
                0,
                ClientWebSocketTransport.MaxMessageBytes + 1));
    }
}
