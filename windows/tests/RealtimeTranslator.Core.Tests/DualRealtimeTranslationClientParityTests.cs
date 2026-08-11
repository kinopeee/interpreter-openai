using System;
using System.Collections.Generic;
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
/// Swift <c>DualRealtimeTranslationClientTests</c> のうち、Windows CI に未移植だった
/// wire payload / merge / Start 失敗クリーンアップ契約。
/// </summary>
public sealed class DualRealtimeTranslationClientParityTests
{
    // Given: 専用 transcription を開始した Dual
    // When: event_id なし・同一 item_id の連続 delta を受け取る
    // Then: item_id で誤って重複排除せず、両方の delta が Events へ届く
    [Fact]
    public async Task SourceConnectionPublishesEveryDeltaForSameItem()
    {
        var source = new FakeRealtimeServerTransport();
        var english = new FakeRealtimeServerTransport();
        var japanese = new FakeRealtimeServerTransport();
        using var dual = CreateDual(source, english, japanese);

        await dual.StartAsync("sk-test", RealtimeSessionTuning.Default);

        source.EnqueueJson(
            """{"type":"conversation.item.input_audio_transcription.delta","item_id":"item-1","delta":"それ"}""");
        source.EnqueueJson(
            """{"type":"conversation.item.input_audio_transcription.delta","item_id":"item-1","delta":"ぞれ"}""");

        var deltas = await CollectSourceDeltasAsync(dual, count: 2);

        Assert.Equal(["それ", "ぞれ"], deltas);
        await dual.ForceCloseAsync();
    }

    // Given: near_field とカスタム prompt/keywords/delay の tuning
    // When: custom tuning で Dual を開始する
    // Then: 原文 session.update へ反映され、翻訳両 lane は noise_reduction のみ（transcription ブロックなし）
    [Fact]
    public async Task StartAppliesCustomTuningToSourceAndTranslationSessions()
    {
        var source = new FakeRealtimeServerTransport();
        var english = new FakeRealtimeServerTransport();
        var japanese = new FakeRealtimeServerTransport();
        using var dual = CreateDual(source, english, japanese);
        var tuning = new RealtimeSessionTuning(
            RealtimeTranslationNoiseReduction.NearField,
            RealtimeTranscriptionDelay.High,
            "Custom domain glossary hints",
            ["固有名詞", "Acme"]);

        await dual.StartAsync("sk-test", tuning);

        var sourceInput = FirstSessionUpdateInput(source);
        var englishInput = FirstSessionUpdateInput(english);
        var japaneseInput = FirstSessionUpdateInput(japanese);
        var sourceTranscription = sourceInput["transcription"]!.AsObject();

        Assert.Equal("gpt-live-transcribe", sourceTranscription["model"]!.GetValue<string>());
        Assert.Equal(tuning.TranscriptionPrompt, sourceTranscription["prompt"]!.GetValue<string>());
        Assert.Equal(
            tuning.TranscriptionKeywords.ToArray(),
            sourceTranscription["keywords"]!.AsArray().Select(node => node!.GetValue<string>()).ToArray());
        Assert.Equal("high", sourceTranscription["delay"]!.GetValue<string>());
        Assert.Equal("near_field", sourceInput["noise_reduction"]!["type"]!.GetValue<string>());
        Assert.Equal("near_field", englishInput["noise_reduction"]!["type"]!.GetValue<string>());
        Assert.Equal("near_field", japaneseInput["noise_reduction"]!["type"]!.GetValue<string>());
        Assert.Null(englishInput["transcription"]);
        Assert.Null(japaneseInput["transcription"]);
        await dual.ForceCloseAsync();
    }

    // Given: ready な Dual
    // When: 録音中に新しい prompt/keywords/delay で update する
    // Then: 2 通目の source session.update に新値が載る
    [Fact]
    public async Task UpdateTranscriptionTuningSendsSecondSessionUpdate()
    {
        var source = new FakeRealtimeServerTransport();
        var english = new FakeRealtimeServerTransport();
        var japanese = new FakeRealtimeServerTransport();
        using var dual = CreateDual(source, english, japanese);

        await dual.StartAsync("sk-test", RealtimeSessionTuning.Default);
        var sentBefore = source.Sent.Count;

        var updated = new RealtimeSessionTuning(
            RealtimeTranslationNoiseReduction.FarField,
            RealtimeTranscriptionDelay.Medium,
            "Live glossary update",
            ["Acme", "ロードマップ"]);
        await dual.UpdateTranscriptionTuningAsync(updated);
        await WaitUntilSentAsync(source, sentBefore + 1);

        var updates = SessionUpdates(source);
        Assert.True(updates.Count >= 2);
        var transcription = updates[^1]["session"]!["audio"]!["input"]!["transcription"]!.AsObject();
        Assert.Equal("Live glossary update", transcription["prompt"]!.GetValue<string>());
        Assert.Equal(
            ["Acme", "ロードマップ"],
            transcription["keywords"]!.AsArray().Select(node => node!.GetValue<string>()).ToArray());
        Assert.Equal("medium", transcription["delay"]!.GetValue<string>());
        await dual.ForceCloseAsync();
    }

    // Given: far_field で開始した Dual
    // When: 設定側が near_field に変わった tuning で live update する
    // Then: prompt/delay は更新され、noise_reduction は接続時の far_field のまま
    [Fact]
    public async Task UpdateTranscriptionTuningPreservesConnectedNoiseReduction()
    {
        var source = new FakeRealtimeServerTransport();
        var english = new FakeRealtimeServerTransport();
        var japanese = new FakeRealtimeServerTransport();
        using var dual = CreateDual(source, english, japanese);

        await dual.StartAsync(
            "sk-test",
            RealtimeSessionTuning.Default with { NoiseReduction = RealtimeTranslationNoiseReduction.FarField });
        var sentBefore = source.Sent.Count;

        await dual.UpdateTranscriptionTuningAsync(
            new RealtimeSessionTuning(
                RealtimeTranslationNoiseReduction.NearField,
                RealtimeTranscriptionDelay.High,
                "Keep noise reduction pinned",
                ["Acme"]));
        await WaitUntilSentAsync(source, sentBefore + 1);

        var second = SessionUpdates(source)[^1];
        var input = second["session"]!["audio"]!["input"]!.AsObject();
        var transcription = input["transcription"]!.AsObject();
        Assert.Equal("Keep noise reduction pinned", transcription["prompt"]!.GetValue<string>());
        Assert.Equal("high", transcription["delay"]!.GetValue<string>());
        Assert.Equal("far_field", input["noise_reduction"]!["type"]!.GetValue<string>());
        await dual.ForceCloseAsync();
    }

    // Given: 英語翻訳接続だけ Connect が失敗する Dual
    // When: StartAsync する
    // Then: 例外を伝播し、3 接続とも ForceClose され、Append は NotConnected
    [Fact]
    public async Task StartFailureForceClosesAllThreeConnections()
    {
        var source = new FakeRealtimeServerTransport();
        var english = new FakeRealtimeServerTransport
        {
            ConnectError = new RealtimeTranslationException(
                RealtimeTranslationErrorKind.RecoverableTransportFailure,
                "english connect failed"),
        };
        var japanese = new FakeRealtimeServerTransport();
        using var dual = CreateDual(source, english, japanese);

        var error = await Assert.ThrowsAsync<RealtimeTranslationException>(
            () => dual.StartAsync("sk-test", RealtimeSessionTuning.Default));

        Assert.Equal(RealtimeTranslationErrorKind.RecoverableTransportFailure, error.Kind);
        Assert.True(source.CloseCount >= 1);
        Assert.True(english.CloseCount >= 1);
        Assert.True(japanese.CloseCount >= 1);

        var appendError = await Assert.ThrowsAsync<RealtimeTranslationException>(
            () => dual.AppendAudioFrameAsync(new byte[Pcm16FramePacketizer.BytesPerFrame]));
        Assert.Equal(RealtimeTranslationErrorKind.NotConnected, appendError.Kind);
    }

    // Given: ready な Dual
    // When: 原文送信失敗後に ForceClose する
    // Then: epoch が進み、3 接続とも閉じる（Swift OneSidedFailureForceClosesPair 相当）
    [Fact]
    public async Task ForceCloseAfterSourceAppendFailureClosesAllConnections()
    {
        var source = new FakeRealtimeServerTransport();
        var english = new FakeRealtimeServerTransport();
        var japanese = new FakeRealtimeServerTransport();
        using var dual = CreateDual(source, english, japanese);

        await dual.StartAsync("sk-test", RealtimeSessionTuning.Default);
        var epochBefore = dual.ConnectionEpoch;

        source.SendError = new RealtimeTranslationException(
            RealtimeTranslationErrorKind.RecoverableTransportFailure,
            "boom");
        await Assert.ThrowsAsync<RealtimeTranslationException>(
            () => dual.AppendAudioFrameAsync(new byte[Pcm16FramePacketizer.BytesPerFrame]));
        await dual.ForceCloseAsync();

        Assert.True(dual.ConnectionEpoch > epochBefore);
        Assert.True(source.CloseCount >= 1);
        Assert.True(english.CloseCount >= 1);
        Assert.True(japanese.CloseCount >= 1);
    }

    // Given: ready な Dual で先頭の原文 CloseAsync だけが失敗する
    // When: ForceCloseAsync する
    // Then: 先頭の失敗を伝播しつつ翻訳両 lane も閉じ、Events は完了する
    [Fact]
    public async Task ForceCloseContinuesWhenOneConnectionThrows()
    {
        var source = new FakeRealtimeServerTransport();
        var english = new FakeRealtimeServerTransport();
        var japanese = new FakeRealtimeServerTransport();
        using var dual = CreateDual(source, english, japanese);

        await dual.StartAsync("sk-test", RealtimeSessionTuning.Default);
        var epochBefore = dual.ConnectionEpoch;
        var closeCountBefore = (
            Source: source.CloseCount,
            English: english.CloseCount,
            Japanese: japanese.CloseCount);
        // Start 内の初期 ForceClose を避け、ready 後にだけ Close 失敗を注入する。
        source.CloseError = new InvalidOperationException("source close boom");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => dual.ForceCloseAsync());

        Assert.Equal("source close boom", error.Message);
        Assert.True(dual.ConnectionEpoch > epochBefore);
        Assert.True(source.CloseCount > closeCountBefore.Source);
        Assert.True(english.CloseCount > closeCountBefore.English);
        Assert.True(japanese.CloseCount > closeCountBefore.Japanese);
        while (dual.Events.TryRead(out _))
        {
        }

        Assert.False(await dual.Events.WaitToReadAsync());
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

    private static async Task<List<string>> CollectSourceDeltasAsync(
        DualRealtimeTranslationClient dual,
        int count)
    {
        var deltas = new List<string>(count);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (deltas.Count < count)
        {
            var streamEvent = await dual.Events.ReadAsync(timeout.Token);
            if (streamEvent.Event is RealtimeTranslationServerEvent.InputTranscriptDelta delta)
            {
                deltas.Add(delta.Delta);
            }
        }

        return deltas;
    }

    private static JsonObject FirstSessionUpdateInput(FakeRealtimeServerTransport transport)
    {
        var update = SessionUpdates(transport).FirstOrDefault()
            ?? throw new InvalidOperationException("session.update was not sent");
        return update["session"]!["audio"]!["input"]!.AsObject();
    }

    private static List<JsonObject> SessionUpdates(FakeRealtimeServerTransport transport) =>
        transport.Sent
            .Select(payload => JsonNode.Parse(payload)?.AsObject())
            .Where(node => node?["type"]?.GetValue<string>() == "session.update")
            .Select(node => node!)
            .ToList();

    private static async Task WaitUntilSentAsync(FakeRealtimeServerTransport transport, int minimum)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (transport.Sent.Count < minimum)
        {
            timeout.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, timeout.Token);
        }
    }
}
