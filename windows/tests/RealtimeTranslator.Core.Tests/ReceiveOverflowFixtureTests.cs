using System;
using System.Linq;
using System.Threading.Channels;
using RealtimeTranslator.Core.Localization;
using RealtimeTranslator.Core.OpenAI;
using RealtimeTranslator.Core.Realtime;
using Xunit;

namespace RealtimeTranslator.Core.Tests;

public sealed class ReceiveOverflowFixtureTests
{
    public static TheoryData<string, int, bool> BoundaryCases
    {
        get
        {
            var data = new TheoryData<string, int, bool>();
            foreach (var item in SharedFixtures.Section("receive-queue", "boundaries"))
            {
                if (item?["platform"]?.GetValue<string>() != "windows")
                {
                    continue;
                }

                var stage = SharedFixtures.Text(item["stage"]);
                if (stage is "connection" or "merge")
                {
                    data.Add(
                        stage,
                        SharedFixtures.Number(item["eventCount"]),
                        SharedFixtures.Flag(item["expectedLoss"]));
                }
            }

            return data;
        }
    }

    [Fact]
    public void SharedFixtureMatchesWindowsDeliveryContract()
    {
        // Given: receive-queue fixture
        var fixture = SharedFixtures.Load("receive-queue");

        // When: Windowsの容量と終了優先順位を読み取る
        var windows = fixture["capacities"]!["windows"]!;
        var precedence = fixture["terminationPrecedence"]!.AsArray()
            .Select(ParseTermination)
            .ToArray();

        // Then: 接続とマージの容量は名前付き定数と一致する
        Assert.Equal(RealtimeEventChannel.Capacity, SharedFixtures.Number(windows["connection"]));
        Assert.Equal(RealtimeEventChannel.Capacity, SharedFixtures.Number(windows["merge"]));
        Assert.Equal(
            Enum.GetValues<EventDeliveryTermination>()
                .Where(value => value != EventDeliveryTermination.None)
                .OrderByDescending(value => value),
            precedence);
    }

    [Theory]
    [MemberData(nameof(BoundaryCases))]
    public void WindowsBoundaryRecordsLossOnlyAboveCapacity(
        string stageName,
        int count,
        bool expectedLoss)
    {
        // Given: Waitモードの有限チャネルとWindowsの容量
        var channel = RealtimeEventChannel.Create();
        var state = new EventDeliveryState(7);
        var stage = stageName == "connection"
            ? EventDeliveryStage.Source
            : EventDeliveryStage.Merge;
        var writer = new EventDeliveryWriter(
            channel.Writer,
            state,
            stage,
            RealtimeEventChannel.Capacity);

        // When: 本文を含まないイベントを境界数だけ配送する
        for (var index = 0; index < count; index++)
        {
            writer.TryDeliver(new RealtimeTranslationStreamEvent(
                RealtimeTranslationLane.Source,
                new RealtimeTranslationServerEvent.SessionCreated(),
                state.Epoch));
        }

        // Then: 容量超過だけがイベント損失になる
        Assert.Equal(expectedLoss, state.DidLoseEvents);
        if (expectedLoss)
        {
            Assert.Equal(stage, state.LossStage);
            Assert.Equal(RealtimeEventChannel.Capacity, state.LossCapacity);
        }
    }

    [Fact]
    public void OverflowCopyResolvesFixtureLocalizationKey()
    {
        // Given: receive-queue fixtureのエラーキー
        var fixture = SharedFixtures.Load("receive-queue");
        var key = SharedFixtures.Text(fixture["overflow"]!["errorMessageKey"]);
        var japanese = UserCopy.Parse(SharedFixtures.UiCatalogJson, UiLocale.Ja);

        // When: receiveOverflowの例外メッセージを作る
        var error = new RealtimeTranslationException(RealtimeTranslationErrorKind.ReceiveOverflow);

        // Then: 指定されたローカライズ文言になる
        Assert.Equal(japanese.Text(key), error.Message);
        Assert.True(new RealtimeTranslationException(RealtimeTranslationErrorKind.ReceiveOverflow).IsRecoverable);
    }

    [Fact]
    public void TerminationPrecedenceNeverDowngrades()
    {
        // Given: 同じエポックの配送状態
        var state = new EventDeliveryState(3);

        // When: 低い優先順位から終了理由を記録する
        state.TryRecordTermination(EventDeliveryTermination.AuthenticationFailed);
        state.TryRecordTermination(EventDeliveryTermination.TransportFailure);
        state.RecordLoss(EventDeliveryStage.Merge, RealtimeEventChannel.Capacity);

        // Then: 最も高い終了理由を保持する
        Assert.Equal(EventDeliveryTermination.AuthenticationFailed, state.Termination);
        Assert.False(state.TryRecordTermination(EventDeliveryTermination.None));
    }

    private static EventDeliveryTermination ParseTermination(
        System.Text.Json.Nodes.JsonNode? value) =>
        SharedFixtures.Text(value) switch
        {
            "authenticationFailed" => EventDeliveryTermination.AuthenticationFailed,
            "fatalServerError" => EventDeliveryTermination.FatalServerError,
            "receiveOverflow" => EventDeliveryTermination.ReceiveOverflow,
            "transportFailure" => EventDeliveryTermination.TransportFailure,
            _ => throw new Xunit.Sdk.XunitException("unknown termination"),
        };
}
