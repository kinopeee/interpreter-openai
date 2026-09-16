using System.Text.Json.Nodes;
using RealtimeTranslator.Core.OpenAI;
using RealtimeTranslator.Core.Realtime;
using Xunit;

namespace RealtimeTranslator.Core.Tests;

public sealed class RealtimeSessionExpiryTests
{
    private static JsonObject Session(JsonNode? expiresAt) => new()
    {
        ["id"] = "sess_x",
        ["type"] = "translation",
        ["expires_at"] = expiresAt,
    };

    // Given: 整数値の expires_at を持つ session
    // When: ParseExpiresAt で読む
    // Then: unix 秒の整数として返る
    [Fact]
    public void ParseValidIntegerExpiresAt()
    {
        Assert.Equal(
            1756324625L,
            RealtimeSessionExpiry.ParseExpiresAt(Session(JsonValue.Create(1756324625L))));
    }

    // Given: expires_at が数字文字列の session
    // When: ParseExpiresAt で読む
    // Then: 不明（null）になる
    [Fact]
    public void ParseStringExpiresAtIsUnknown()
    {
        Assert.Null(RealtimeSessionExpiry.ParseExpiresAt(Session(JsonValue.Create("1756324625"))));
    }

    // Given: 負数の expires_at を持つ session
    // When: ParseExpiresAt で読む
    // Then: 不明（null）になる
    [Fact]
    public void ParseNegativeExpiresAtIsUnknown()
    {
        Assert.Null(RealtimeSessionExpiry.ParseExpiresAt(Session(JsonValue.Create(-1L))));
    }

    // Given: 小数の expires_at を持つ session
    // When: ParseExpiresAt で読む
    // Then: 不明（null）になる
    [Fact]
    public void ParseFractionalExpiresAtIsUnknown()
    {
        Assert.Null(RealtimeSessionExpiry.ParseExpiresAt(Session(JsonValue.Create(1756324625.5))));
    }

    // Given: expires_at が null の session
    // When: ParseExpiresAt で読む
    // Then: 不明（null）になる
    [Fact]
    public void ParseNullExpiresAtIsUnknown()
    {
        Assert.Null(RealtimeSessionExpiry.ParseExpiresAt(Session(null)));
    }

    // Given: expires_at が bool の session
    // When: ParseExpiresAt で読む
    // Then: 不明（null）になる
    [Fact]
    public void ParseBoolExpiresAtIsUnknown()
    {
        Assert.Null(RealtimeSessionExpiry.ParseExpiresAt(Session(JsonValue.Create(true))));
        Assert.Null(RealtimeSessionExpiry.ParseExpiresAt(Session(JsonValue.Create(false))));
    }

    // Given: session 自体が欠落または非 object
    // When: ParseExpiresAt で読む
    // Then: 不明（null）になる
    [Fact]
    public void ParseMissingSessionIsUnknown()
    {
        Assert.Null(RealtimeSessionExpiry.ParseExpiresAt(null));
        Assert.Null(RealtimeSessionExpiry.ParseExpiresAt(JsonValue.Create("x")));
    }

    // Given: expires_at を持たない session
    // When: ParseExpiresAt で読む
    // Then: 不明（null）になる
    [Fact]
    public void ParseMissingExpiresAtIsUnknown()
    {
        Assert.Null(RealtimeSessionExpiry.ParseExpiresAt(
            new JsonObject { ["id"] = "sess_x", ["type"] = "translation" }));
    }

    // Given: expires_at が 0 の session
    // When: ParseExpiresAt で読む
    // Then: 0 が有効値として返る
    [Fact]
    public void ParseZeroExpiresAtIsValid()
    {
        Assert.Equal(0L, RealtimeSessionExpiry.ParseExpiresAt(Session(JsonValue.Create(0L))));
    }

    // Given: 正常な expires_at と現在の壁時計
    // When: RemainingSeconds で残り秒へ変換する
    // Then: 130 が返る
    [Fact]
    public void RemainingSecondsReturnsNormalDifference()
    {
        Assert.Equal(
            130L,
            RealtimeSessionExpiry.RemainingSeconds(1_756_324_625L + 130L, 1_756_324_625L));
    }

    // Given: long.MaxValue 相当の巨大な expires_at / 壁時計
    // When: RemainingSeconds で変換する
    // Then: ±10 年の範囲外・オーバーフローは null が返る（例外にならない）
    [Fact]
    public void RemainingSecondsRejectsHugeValue()
    {
        Assert.Null(RealtimeSessionExpiry.RemainingSeconds(long.MaxValue, 0L));
        Assert.Null(RealtimeSessionExpiry.RemainingSeconds(0L, long.MaxValue));
        Assert.Null(RealtimeSessionExpiry.RemainingSeconds(0L, long.MinValue));
    }
}

public sealed class EventDeliveryStateReceiveAndExpiryTests
{
    // Given: 新しい EventDeliveryState
    // When: lane ごとに RecordReceive する
    // Then: lane ごとの受信数が独立して数えられる
    [Fact]
    public void ReceiveCountTracksEachLane()
    {
        var state = new EventDeliveryState(1);
        var enLane = RealtimeTranslationLane.Translation(RealtimeTranslationOutputLanguage.English);

        Assert.Equal(0, state.ReceiveCount(RealtimeTranslationLane.Source));
        Assert.Equal(0, state.ReceiveCount(enLane));

        state.RecordReceive(RealtimeTranslationLane.Source);
        state.RecordReceive(RealtimeTranslationLane.Source);
        state.RecordReceive(enLane);

        Assert.Equal(2, state.ReceiveCount(RealtimeTranslationLane.Source));
        Assert.Equal(1, state.ReceiveCount(enLane));
        Assert.Equal(
            0,
            state.ReceiveCount(RealtimeTranslationLane.Translation(
                RealtimeTranslationOutputLanguage.Japanese)));
    }

    // Given: EventDeliveryState
    // When: RecordSessionExpiry で lane の期限を記録・上書き・不明化する
    // Then: SessionExpiry が記録値を返し、不明化すると null になる
    [Fact]
    public void SessionExpiryRecordOverwriteAndClear()
    {
        var state = new EventDeliveryState(1);
        var enLane = RealtimeTranslationLane.Translation(RealtimeTranslationOutputLanguage.English);

        Assert.Null(state.SessionExpiry(enLane));

        state.RecordSessionExpiry(enLane, 1756324625L);
        Assert.Equal(1756324625L, state.SessionExpiry(enLane));
        Assert.Null(state.SessionExpiry(RealtimeTranslationLane.Source));

        state.RecordSessionExpiry(enLane, 1756325000L);
        Assert.Equal(1756325000L, state.SessionExpiry(enLane));

        state.RecordSessionExpiry(enLane, null);
        Assert.Null(state.SessionExpiry(enLane));
    }
}
