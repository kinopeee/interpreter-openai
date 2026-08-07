using System;
using System.Text.Json.Nodes;
using RealtimeTranslator.Core.Subtitles;
using Xunit;

namespace RealtimeTranslator.Core.Tests;

public sealed class SubtitleTranscriptFormatterTests
{
    public static TheoryData<string> FormatCases => SharedFixtures.CaseNames("transcript", "format");

    // Given: shared fixture のファイル上限とバナー文言
    // When: Core 定数と照合する
    // Then: 10 MB 上限とバナー文言が一致する
    [Fact]
    public void LimitsAndMessagesMatchFixture()
    {
        var root = SharedFixtures.Load("transcript");
        var limits = (JsonObject)root["limits"]!;
        var messages = (JsonObject)root["messages"]!;

        Assert.Equal(
            SharedFixtures.Number(limits["maxFileBytes"]),
            SubtitleTranscriptLimits.MaxFileBytes);
        Assert.Equal(
            SharedFixtures.Text(messages["sizeLimitBanner"]),
            SubtitleTranscriptLimits.SizeLimitBanner);
        Assert.Equal(
            SharedFixtures.Text(messages["writeFailureBanner"]),
            SubtitleTranscriptLimits.WriteFailureBanner);
    }

    // Given: fixture の entry / sessionStart ケース
    // When: フォーマッタで整形する
    // Then: 期待するプレーンテキストになる
    [Theory]
    [MemberData(nameof(FormatCases))]
    public void FormatMatchesFixture(string name)
    {
        var fixture = SharedFixtures.Case("transcript", "format", name);
        var kind = SharedFixtures.Text(fixture["kind"]);
        var timestamp = SharedFixtures.Text(fixture["timestamp"]);
        var expected = SharedFixtures.Text(fixture["expected"]);

        var actual = kind switch
        {
            "entry" => SubtitleTranscriptFormatter.FormatEntry(
                timestamp,
                SharedFixtures.Text(fixture["sourceText"]),
                SharedFixtures.Text(fixture["translatedText"])),
            "sessionStart" => SubtitleTranscriptFormatter.FormatSessionStart(timestamp),
            _ => throw new InvalidOperationException("unhandled kind " + kind),
        };

        Assert.Equal(expected, actual);
    }

    // Given: 固定の日時とタイムゾーン
    // When: タイムスタンプを整形する
    // Then: オフセット付き ISO8601 になる
    [Fact]
    public void FormatTimestampIncludesOffset()
    {
        var timestamp = new DateTimeOffset(2026, 8, 7, 15, 40, 12, TimeSpan.FromHours(9));

        Assert.Equal(
            "2026-08-07T15:40:12+09:00",
            SubtitleTranscriptFormatter.FormatTimestamp(timestamp));
    }
}
