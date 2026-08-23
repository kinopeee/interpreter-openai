using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
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

    // Given: 原文 handshake だけが停滞し、翻訳両 lane は ready になる Dual
    // When: 原文が SessionUpdateTimeout する
    // Then: 例外を伝播し、すでに ready だった翻訳 lane も ForceClose する
    [Fact]
    public async Task SourceHandshakeStallForceClosesReadyTranslationLanes()
    {
        var source = new FakeRealtimeServerTransport { AutoHandshake = false };
        var english = new FakeRealtimeServerTransport();
        var japanese = new FakeRealtimeServerTransport();
        using var dual = new DualRealtimeTranslationClient(
            new RealtimeSourceTranscriptionConnection(
                source,
                "test-safety",
                handshakeTimeout: TimeSpan.FromSeconds(2)),
            new RealtimeTranslationConnection(
                RealtimeTranslationOutputLanguage.English,
                english,
                "test-safety"),
            new RealtimeTranslationConnection(
                RealtimeTranslationOutputLanguage.Japanese,
                japanese,
                "test-safety"));

        var startTask = dual.StartAsync("sk-test", RealtimeSessionTuning.Default);
        await WaitUntilSessionUpdatedAsync(english, japanese);
        // Start 冒頭の ForceClose と各接続 Start の TearDown でも CloseCount は進む。
        // leftover 判定は、handshake 完了後から cleanup 後に増えたことだけを見る。
        var closeCountBeforeCleanup = (
            English: english.CloseCount,
            Japanese: japanese.CloseCount);

        var error = await Assert.ThrowsAsync<RealtimeTranslationException>(() => startTask);

        Assert.Equal(RealtimeTranslationErrorKind.SessionUpdateTimeout, error.Kind);
        Assert.True(english.CloseCount > closeCountBeforeCleanup.English);
        Assert.True(japanese.CloseCount > closeCountBeforeCleanup.Japanese);

        var appendError = await Assert.ThrowsAsync<RealtimeTranslationException>(
            () => dual.AppendAudioFrameAsync(new byte[Pcm16FramePacketizer.BytesPerFrame]));
        Assert.Equal(RealtimeTranslationErrorKind.NotConnected, appendError.Kind);
    }

    // Given: 英語翻訳 handshake だけが停滞し、原文と日本語 lane は ready になる Dual
    // When: 英語が SessionUpdateTimeout する
    // Then: 例外を伝播し、すでに ready だった原文・日本語 lane も ForceClose する
    [Fact]
    public async Task TranslationHandshakeStallForceClosesReadySourceAndOtherLane()
    {
        var source = new FakeRealtimeServerTransport();
        var english = new FakeRealtimeServerTransport { AutoHandshake = false };
        var japanese = new FakeRealtimeServerTransport();
        using var dual = new DualRealtimeTranslationClient(
            new RealtimeSourceTranscriptionConnection(source, "test-safety"),
            new RealtimeTranslationConnection(
                RealtimeTranslationOutputLanguage.English,
                english,
                "test-safety",
                sessionUpdateTimeout: TimeSpan.FromSeconds(2)),
            new RealtimeTranslationConnection(
                RealtimeTranslationOutputLanguage.Japanese,
                japanese,
                "test-safety"));

        var startTask = dual.StartAsync("sk-test", RealtimeSessionTuning.Default);
        await WaitUntilSessionUpdatedAsync(source, japanese);
        // leftover 判定は handshake 完了後から cleanup 後に CloseCount が増えたことだけを見る。
        var closeCountBeforeCleanup = (
            Source: source.CloseCount,
            Japanese: japanese.CloseCount);

        var error = await Assert.ThrowsAsync<RealtimeTranslationException>(() => startTask);

        Assert.Equal(RealtimeTranslationErrorKind.SessionUpdateTimeout, error.Kind);
        Assert.True(source.CloseCount > closeCountBeforeCleanup.Source);
        Assert.True(japanese.CloseCount > closeCountBeforeCleanup.Japanese);

        var appendError = await Assert.ThrowsAsync<RealtimeTranslationException>(
            () => dual.AppendAudioFrameAsync(new byte[Pcm16FramePacketizer.BytesPerFrame]));
        Assert.Equal(RealtimeTranslationErrorKind.NotConnected, appendError.Kind);
    }

    // Given: ja-es でスペイン語 handshake だけが停滞し、原文と日本語 lane は ready
    // When: スペイン語が SessionUpdateTimeout する
    // Then: leftover の原文・日本語を ForceClose し、未使用 English は接続しない
    [Fact]
    public async Task SpanishHandshakeStallForceClosesReadyJaEsLanesWithoutStartingEnglish()
    {
        var source = new FakeRealtimeServerTransport();
        var english = new FakeRealtimeServerTransport();
        var japanese = new FakeRealtimeServerTransport();
        var spanish = new FakeRealtimeServerTransport { AutoHandshake = false };
        using var dual = new DualRealtimeTranslationClient(
            new RealtimeSourceTranscriptionConnection(source, "test-safety"),
            new RealtimeTranslationConnection(
                RealtimeTranslationOutputLanguage.English,
                english,
                "test-safety"),
            new RealtimeTranslationConnection(
                RealtimeTranslationOutputLanguage.Japanese,
                japanese,
                "test-safety"),
            spanishConnection: new RealtimeTranslationConnection(
                RealtimeTranslationOutputLanguage.Spanish,
                spanish,
                "test-safety",
                sessionUpdateTimeout: TimeSpan.FromSeconds(2)));

        var startTask = dual.StartAsync("sk-test", RealtimeSessionTuning.Default, LanguagePair.JaEs);
        await WaitUntilSessionUpdatedAsync(source, japanese);
        // leftover 判定は handshake 完了後から cleanup 後に CloseCount が増えたことだけを見る。
        var closeCountBeforeCleanup = (
            Source: source.CloseCount,
            Japanese: japanese.CloseCount);

        var error = await Assert.ThrowsAsync<RealtimeTranslationException>(() => startTask);

        Assert.Equal(RealtimeTranslationErrorKind.SessionUpdateTimeout, error.Kind);
        Assert.Equal(0, english.ConnectCount);
        Assert.True(source.CloseCount > closeCountBeforeCleanup.Source);
        Assert.True(japanese.CloseCount > closeCountBeforeCleanup.Japanese);

        var appendError = await Assert.ThrowsAsync<RealtimeTranslationException>(
            () => dual.AppendAudioFrameAsync(new byte[Pcm16FramePacketizer.BytesPerFrame]));
        Assert.Equal(RealtimeTranslationErrorKind.NotConnected, appendError.Kind);
    }

    // Given: ja-es で日本語 handshake だけが停滞し、原文とスペイン語 lane は ready
    // When: 日本語が SessionUpdateTimeout する
    // Then: leftover の原文・スペイン語を ForceClose し、未使用 English は接続しない
    [Fact]
    public async Task JapaneseHandshakeStallForceClosesReadyJaEsLanesWithoutStartingEnglish()
    {
        var source = new FakeRealtimeServerTransport();
        var english = new FakeRealtimeServerTransport();
        var japanese = new FakeRealtimeServerTransport { AutoHandshake = false };
        var spanish = new FakeRealtimeServerTransport();
        using var dual = new DualRealtimeTranslationClient(
            new RealtimeSourceTranscriptionConnection(source, "test-safety"),
            new RealtimeTranslationConnection(
                RealtimeTranslationOutputLanguage.English,
                english,
                "test-safety"),
            new RealtimeTranslationConnection(
                RealtimeTranslationOutputLanguage.Japanese,
                japanese,
                "test-safety",
                sessionUpdateTimeout: TimeSpan.FromSeconds(2)),
            spanishConnection: new RealtimeTranslationConnection(
                RealtimeTranslationOutputLanguage.Spanish,
                spanish,
                "test-safety"));

        var startTask = dual.StartAsync("sk-test", RealtimeSessionTuning.Default, LanguagePair.JaEs);
        await WaitUntilSessionUpdatedAsync(source, spanish);
        // leftover 判定は handshake 完了後から cleanup 後に CloseCount が増えたことだけを見る。
        var closeCountBeforeCleanup = (
            Source: source.CloseCount,
            Spanish: spanish.CloseCount);

        var error = await Assert.ThrowsAsync<RealtimeTranslationException>(() => startTask);

        Assert.Equal(RealtimeTranslationErrorKind.SessionUpdateTimeout, error.Kind);
        Assert.Equal(0, english.ConnectCount);
        Assert.True(source.CloseCount > closeCountBeforeCleanup.Source);
        Assert.True(spanish.CloseCount > closeCountBeforeCleanup.Spanish);

        var appendError = await Assert.ThrowsAsync<RealtimeTranslationException>(
            () => dual.AppendAudioFrameAsync(new byte[Pcm16FramePacketizer.BytesPerFrame]));
        Assert.Equal(RealtimeTranslationErrorKind.NotConnected, appendError.Kind);
    }

    // Given: ja-es で原文 handshake だけが停滞し、日本語とスペイン語 lane は ready
    // When: 原文が SessionUpdateTimeout する
    // Then: leftover の翻訳 lane を ForceClose し、未使用 English は接続しない
    [Fact]
    public async Task SourceHandshakeStallForceClosesReadyJaEsLanesWithoutStartingEnglish()
    {
        var source = new FakeRealtimeServerTransport { AutoHandshake = false };
        var english = new FakeRealtimeServerTransport();
        var japanese = new FakeRealtimeServerTransport();
        var spanish = new FakeRealtimeServerTransport();
        using var dual = new DualRealtimeTranslationClient(
            new RealtimeSourceTranscriptionConnection(
                source,
                "test-safety",
                handshakeTimeout: TimeSpan.FromSeconds(2)),
            new RealtimeTranslationConnection(
                RealtimeTranslationOutputLanguage.English,
                english,
                "test-safety"),
            new RealtimeTranslationConnection(
                RealtimeTranslationOutputLanguage.Japanese,
                japanese,
                "test-safety"),
            spanishConnection: new RealtimeTranslationConnection(
                RealtimeTranslationOutputLanguage.Spanish,
                spanish,
                "test-safety"));

        var startTask = dual.StartAsync("sk-test", RealtimeSessionTuning.Default, LanguagePair.JaEs);
        await WaitUntilSessionUpdatedAsync(japanese, spanish);
        // leftover 判定は handshake 完了後から cleanup 後に CloseCount が増えたことだけを見る。
        var closeCountBeforeCleanup = (
            Japanese: japanese.CloseCount,
            Spanish: spanish.CloseCount);

        var error = await Assert.ThrowsAsync<RealtimeTranslationException>(() => startTask);

        Assert.Equal(RealtimeTranslationErrorKind.SessionUpdateTimeout, error.Kind);
        Assert.Equal(0, english.ConnectCount);
        Assert.True(japanese.CloseCount > closeCountBeforeCleanup.Japanese);
        Assert.True(spanish.CloseCount > closeCountBeforeCleanup.Spanish);

        var appendError = await Assert.ThrowsAsync<RealtimeTranslationException>(
            () => dual.AppendAudioFrameAsync(new byte[Pcm16FramePacketizer.BytesPerFrame]));
        Assert.Equal(RealtimeTranslationErrorKind.NotConnected, appendError.Kind);
    }

    // Given: en-es で英語 handshake だけが停滞し、原文とスペイン語 lane は ready
    // When: 英語が SessionUpdateTimeout する
    // Then: leftover の原文・スペイン語を ForceClose し、未使用 Japanese は接続しない
    [Fact]
    public async Task EnglishHandshakeStallForceClosesReadyEnEsLanesWithoutStartingJapanese()
    {
        var source = new FakeRealtimeServerTransport();
        var english = new FakeRealtimeServerTransport { AutoHandshake = false };
        var japanese = new FakeRealtimeServerTransport();
        var spanish = new FakeRealtimeServerTransport();
        using var dual = new DualRealtimeTranslationClient(
            new RealtimeSourceTranscriptionConnection(source, "test-safety"),
            new RealtimeTranslationConnection(
                RealtimeTranslationOutputLanguage.English,
                english,
                "test-safety",
                sessionUpdateTimeout: TimeSpan.FromSeconds(2)),
            new RealtimeTranslationConnection(
                RealtimeTranslationOutputLanguage.Japanese,
                japanese,
                "test-safety"),
            spanishConnection: new RealtimeTranslationConnection(
                RealtimeTranslationOutputLanguage.Spanish,
                spanish,
                "test-safety"));

        var startTask = dual.StartAsync("sk-test", RealtimeSessionTuning.Default, LanguagePair.EnEs);
        await WaitUntilSessionUpdatedAsync(source, spanish);
        // leftover 判定は handshake 完了後から cleanup 後に CloseCount が増えたことだけを見る。
        var closeCountBeforeCleanup = (
            Source: source.CloseCount,
            Spanish: spanish.CloseCount);

        var error = await Assert.ThrowsAsync<RealtimeTranslationException>(() => startTask);

        Assert.Equal(RealtimeTranslationErrorKind.SessionUpdateTimeout, error.Kind);
        Assert.Equal(0, japanese.ConnectCount);
        Assert.True(source.CloseCount > closeCountBeforeCleanup.Source);
        Assert.True(spanish.CloseCount > closeCountBeforeCleanup.Spanish);

        var appendError = await Assert.ThrowsAsync<RealtimeTranslationException>(
            () => dual.AppendAudioFrameAsync(new byte[Pcm16FramePacketizer.BytesPerFrame]));
        Assert.Equal(RealtimeTranslationErrorKind.NotConnected, appendError.Kind);
    }

    // Given: en-es でスペイン語 handshake だけが停滞し、原文と英語 lane は ready
    // When: スペイン語が SessionUpdateTimeout する
    // Then: leftover の原文・英語を ForceClose し、未使用 Japanese は接続しない
    [Fact]
    public async Task SpanishHandshakeStallForceClosesReadyEnEsLanesWithoutStartingJapanese()
    {
        var source = new FakeRealtimeServerTransport();
        var english = new FakeRealtimeServerTransport();
        var japanese = new FakeRealtimeServerTransport();
        var spanish = new FakeRealtimeServerTransport { AutoHandshake = false };
        using var dual = new DualRealtimeTranslationClient(
            new RealtimeSourceTranscriptionConnection(source, "test-safety"),
            new RealtimeTranslationConnection(
                RealtimeTranslationOutputLanguage.English,
                english,
                "test-safety"),
            new RealtimeTranslationConnection(
                RealtimeTranslationOutputLanguage.Japanese,
                japanese,
                "test-safety"),
            spanishConnection: new RealtimeTranslationConnection(
                RealtimeTranslationOutputLanguage.Spanish,
                spanish,
                "test-safety",
                sessionUpdateTimeout: TimeSpan.FromSeconds(2)));

        var startTask = dual.StartAsync("sk-test", RealtimeSessionTuning.Default, LanguagePair.EnEs);
        await WaitUntilSessionUpdatedAsync(source, english);
        var closeCountBeforeCleanup = (
            Source: source.CloseCount,
            English: english.CloseCount);

        var error = await Assert.ThrowsAsync<RealtimeTranslationException>(() => startTask);

        Assert.Equal(RealtimeTranslationErrorKind.SessionUpdateTimeout, error.Kind);
        Assert.Equal(0, japanese.ConnectCount);
        Assert.True(source.CloseCount > closeCountBeforeCleanup.Source);
        Assert.True(english.CloseCount > closeCountBeforeCleanup.English);

        var appendError = await Assert.ThrowsAsync<RealtimeTranslationException>(
            () => dual.AppendAudioFrameAsync(new byte[Pcm16FramePacketizer.BytesPerFrame]));
        Assert.Equal(RealtimeTranslationErrorKind.NotConnected, appendError.Kind);
    }

    // Given: 3 本の翻訳接続を持つ Dual で 1 つ目の pair を開始したあと
    // When: 同じ Dual を別 pair で再 Start する
    // Then: 新 pair に無い lane は ForceClose されて再接続せず、その leftover delta は Events に混線しない
    [Theory]
    [InlineData(LanguagePair.JaEn, LanguagePair.JaEs)]
    [InlineData(LanguagePair.JaEn, LanguagePair.EnEs)]
    [InlineData(LanguagePair.JaEs, LanguagePair.JaEn)]
    [InlineData(LanguagePair.JaEs, LanguagePair.EnEs)]
    [InlineData(LanguagePair.EnEs, LanguagePair.JaEn)]
    [InlineData(LanguagePair.EnEs, LanguagePair.JaEs)]
    public async Task RestartWithDifferentPairForceClosesUnusedLaneAndDropsLeftoverDeltas(
        LanguagePair first,
        LanguagePair second)
    {
        var source = new FakeRealtimeServerTransport();
        var english = new FakeRealtimeServerTransport();
        var japanese = new FakeRealtimeServerTransport();
        var spanish = new FakeRealtimeServerTransport();
        using var dual = CreateDual(source, english, japanese, spanish);

        await dual.StartAsync("sk-test", RealtimeSessionTuning.Default, first);
        var closeCountAfterFirst = (
            English: english.CloseCount,
            Japanese: japanese.CloseCount,
            Spanish: spanish.CloseCount);

        await dual.StartAsync("sk-test", RealtimeSessionTuning.Default, second);

        var firstLanguages = first.Languages().Select(language => language.ToOutputLanguage()).ToHashSet();
        var secondLanguages = second.Languages().Select(language => language.ToOutputLanguage()).ToHashSet();
        Assert.Equal(
            ConnectCountAfterPairSwitch(firstLanguages, secondLanguages, RealtimeTranslationOutputLanguage.English),
            english.ConnectCount);
        Assert.Equal(
            ConnectCountAfterPairSwitch(firstLanguages, secondLanguages, RealtimeTranslationOutputLanguage.Japanese),
            japanese.ConnectCount);
        Assert.Equal(
            ConnectCountAfterPairSwitch(firstLanguages, secondLanguages, RealtimeTranslationOutputLanguage.Spanish),
            spanish.ConnectCount);
        if (!secondLanguages.Contains(RealtimeTranslationOutputLanguage.English))
        {
            Assert.True(english.CloseCount > closeCountAfterFirst.English);
        }

        if (!secondLanguages.Contains(RealtimeTranslationOutputLanguage.Japanese))
        {
            Assert.True(japanese.CloseCount > closeCountAfterFirst.Japanese);
        }

        if (!secondLanguages.Contains(RealtimeTranslationOutputLanguage.Spanish))
        {
            Assert.True(spanish.CloseCount > closeCountAfterFirst.Spanish);
        }

        while (dual.Events.TryRead(out _))
        {
        }

        var unused = UnusedLaneTransport(first, second, english, japanese, spanish);
        unused.EnqueueJson(
            """{"type":"session.output_transcript.delta","delta":"leftover unused lane","event_id":"unused-1"}""");
        source.EnqueueJson(
            """{"type":"conversation.item.input_audio_transcription.delta","item_id":"item-1","delta":"alive"}""");

        // CollectSourceDeltasAsync は非原文イベントを捨てるので、unused leftover が
        // source より先に merge されても緑のままになる。待ち中も leftover を落とす。
        var sourceDeltas = new List<string>(1);
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            while (sourceDeltas.Count < 1)
            {
                var streamEvent = await dual.Events.ReadAsync(timeout.Token);
                Assert.IsNotType<RealtimeTranslationServerEvent.OutputTranscriptDelta>(streamEvent.Event);
                if (streamEvent.Event is RealtimeTranslationServerEvent.InputTranscriptDelta delta)
                {
                    sourceDeltas.Add(delta.Delta);
                }
            }
        }

        Assert.Equal(["alive"], sourceDeltas);
        while (dual.Events.TryRead(out var leftover))
        {
            Assert.IsNotType<RealtimeTranslationServerEvent.OutputTranscriptDelta>(leftover.Event);
        }

        await dual.ForceCloseAsync();
    }

    // Given: 全 lane が session.created を返さない Dual
    // When: Connect 後に呼び出し側 token をキャンセルする
    // Then: SessionUpdateTimeout ではなくキャンセルになり、3 接続とも閉じる
    [Fact]
    public async Task StartCanceledDuringHandshakeForceClosesAllLanes()
    {
        var source = new FakeRealtimeServerTransport { AutoHandshake = false };
        var english = new FakeRealtimeServerTransport { AutoHandshake = false };
        var japanese = new FakeRealtimeServerTransport { AutoHandshake = false };
        using var dual = CreateDual(source, english, japanese);
        using var caller = new CancellationTokenSource();
        var startTask = dual.StartAsync("sk-test", RealtimeSessionTuning.Default, LanguagePair.JaEn, caller.Token);

        await WaitUntilConnectedAsync(source, english, japanese);
        await caller.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => startTask);
        Assert.True(source.CloseCount >= 1);
        Assert.True(english.CloseCount >= 1);
        Assert.True(japanese.CloseCount >= 1);

        var appendError = await Assert.ThrowsAsync<RealtimeTranslationException>(
            () => dual.AppendAudioFrameAsync(new byte[Pcm16FramePacketizer.BytesPerFrame]));
        Assert.Equal(RealtimeTranslationErrorKind.NotConnected, appendError.Kind);
    }

    // Given: 英語 lane は ready、日本語 handshake だけが停滞している Dual
    // When: 呼び出し側 token をキャンセルする
    // Then: leftover の英語・原文も ForceClose し、再 Start できる
    [Fact]
    public async Task CallerCancelDuringPartialReadyHandshakeForceClosesReadyLanes()
    {
        var source = new FakeRealtimeServerTransport();
        var english = new FakeRealtimeServerTransport();
        var japanese = new FakeRealtimeServerTransport { AutoHandshake = false };
        using var dual = CreateDual(source, english, japanese);
        using var caller = new CancellationTokenSource();
        var startTask = dual.StartAsync("sk-test", RealtimeSessionTuning.Default, LanguagePair.JaEn, caller.Token);

        await WaitUntilSessionUpdatedAsync(source, english);
        var closeCountBeforeCleanup = (
            Source: source.CloseCount,
            English: english.CloseCount,
            Japanese: japanese.CloseCount);

        await caller.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => startTask);
        Assert.True(source.CloseCount > closeCountBeforeCleanup.Source);
        Assert.True(english.CloseCount > closeCountBeforeCleanup.English);
        Assert.True(japanese.CloseCount > closeCountBeforeCleanup.Japanese);

        var appendError = await Assert.ThrowsAsync<RealtimeTranslationException>(
            () => dual.AppendAudioFrameAsync(new byte[Pcm16FramePacketizer.BytesPerFrame]));
        Assert.Equal(RealtimeTranslationErrorKind.NotConnected, appendError.Kind);

        japanese.AutoHandshake = true;
        await dual.StartAsync("sk-test", RealtimeSessionTuning.Default, LanguagePair.JaEn);
        Assert.Equal(2, source.ConnectCount);
        Assert.Equal(2, english.ConnectCount);
        await dual.ForceCloseAsync();
    }

    // Given: 英語 Connect が即失敗し、原文は ready、日本語 handshake だけが停滞する Dual
    // When: StartAsync する
    // Then: 日本語の handshake timeout まで leftover 原文を残さず、即 ForceClose して失敗を伝播する
    [Fact]
    public async Task FastLaneFailureCancelsSiblingHandshakeAndForceClosesReadyLanes()
    {
        var source = new FakeRealtimeServerTransport();
        var english = new FakeRealtimeServerTransport
        {
            ConnectError = new RealtimeTranslationException(
                RealtimeTranslationErrorKind.RecoverableTransportFailure,
                "english connect failed"),
        };
        var japanese = new FakeRealtimeServerTransport { AutoHandshake = false };
        using var dual = new DualRealtimeTranslationClient(
            new RealtimeSourceTranscriptionConnection(source, "test-safety"),
            new RealtimeTranslationConnection(
                RealtimeTranslationOutputLanguage.English,
                english,
                "test-safety"),
            new RealtimeTranslationConnection(
                RealtimeTranslationOutputLanguage.Japanese,
                japanese,
                "test-safety",
                sessionUpdateTimeout: TimeSpan.FromSeconds(2)));

        var started = Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<RealtimeTranslationException>(
            () => dual.StartAsync("sk-test", RealtimeSessionTuning.Default, LanguagePair.JaEn));
        started.Stop();

        Assert.Equal(RealtimeTranslationErrorKind.RecoverableTransportFailure, error.Kind);
        Assert.True(
            started.Elapsed < TimeSpan.FromMilliseconds(500),
            $"sibling handshake was not cancelled; elapsed {started.Elapsed.TotalMilliseconds:0}ms");
        // Start 先頭の ForceClose で CloseCount は 1。失敗後 cleanup で 2 以上になる。
        Assert.True(source.CloseCount >= 2);
        Assert.True(english.CloseCount >= 2);
        Assert.True(japanese.CloseCount >= 2);

        var appendError = await Assert.ThrowsAsync<RealtimeTranslationException>(
            () => dual.AppendAudioFrameAsync(new byte[Pcm16FramePacketizer.BytesPerFrame]));
        Assert.Equal(RealtimeTranslationErrorKind.NotConnected, appendError.Kind);
    }

    // Given: 英語 Connect が即失敗し、原文は Connect 後に Close 失敗が仕込まれ、日本語 handshake は停滞する Dual
    // When: StartAsync する
    // Then: leftover ForceClose が失敗しても handshake の RecoverableTransportFailure を返す
    [Fact]
    public async Task FastLaneFailurePreservesHandshakeErrorWhenLeftoverCloseFails()
    {
        var source = new FakeRealtimeServerTransport
        {
            CloseErrorAfterConnect = new InvalidOperationException("source close boom"),
        };
        var english = new FakeRealtimeServerTransport
        {
            ConnectError = new RealtimeTranslationException(
                RealtimeTranslationErrorKind.RecoverableTransportFailure,
                "english connect failed"),
        };
        var japanese = new FakeRealtimeServerTransport { AutoHandshake = false };
        using var dual = new DualRealtimeTranslationClient(
            new RealtimeSourceTranscriptionConnection(source, "test-safety"),
            new RealtimeTranslationConnection(
                RealtimeTranslationOutputLanguage.English,
                english,
                "test-safety"),
            new RealtimeTranslationConnection(
                RealtimeTranslationOutputLanguage.Japanese,
                japanese,
                "test-safety",
                sessionUpdateTimeout: TimeSpan.FromSeconds(2)));

        var started = Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<RealtimeTranslationException>(
            () => dual.StartAsync("sk-test", RealtimeSessionTuning.Default, LanguagePair.JaEn));
        started.Stop();

        Assert.Equal(RealtimeTranslationErrorKind.RecoverableTransportFailure, error.Kind);
        // RecoverableTransportFailure は生サーバー文言を保持しない（#85）。
        // leftover ForceClose の InvalidOperationException に置換されていないことだけ見る。
        Assert.Null(error.ServerMessage);
        Assert.DoesNotContain("source close boom", error.Message, StringComparison.Ordinal);
        Assert.True(
            started.Elapsed < TimeSpan.FromMilliseconds(500),
            $"sibling handshake was not cancelled; elapsed {started.Elapsed.TotalMilliseconds:0}ms");
        // Start 先頭の ForceClose で CloseCount は 1。失敗後 cleanup で 2 以上になる。
        Assert.True(source.CloseCount >= 2);
        Assert.True(english.CloseCount >= 2);
        Assert.True(japanese.CloseCount >= 2);

        var appendError = await Assert.ThrowsAsync<RealtimeTranslationException>(
            () => dual.AppendAudioFrameAsync(new byte[Pcm16FramePacketizer.BytesPerFrame]));
        Assert.Equal(RealtimeTranslationErrorKind.NotConnected, appendError.Kind);
    }

    // Given: 形式不正の API キー
    // When: Dual を開始する
    // Then: どの lane も Connect せず AuthenticationFailed になり、鍵断片を出さない
    [Fact]
    public async Task StartWithMalformedApiKeyDoesNotConnectAnyLane()
    {
        var source = new FakeRealtimeServerTransport();
        var english = new FakeRealtimeServerTransport();
        var japanese = new FakeRealtimeServerTransport();
        using var dual = CreateDual(source, english, japanese);

        var error = await Assert.ThrowsAnyAsync<Exception>(
            () => dual.StartAsync("sk-proj-abc\n3:26", RealtimeSessionTuning.Default));
        var auth = UnwrapAuthenticationFailure(error);

        Assert.Equal(RealtimeTranslationErrorKind.AuthenticationFailed, auth.Kind);
        Assert.DoesNotContain("sk-", auth.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("3:26", auth.Message, StringComparison.Ordinal);
        Assert.Equal(0, source.ConnectCount);
        Assert.Equal(0, english.ConnectCount);
        Assert.Equal(0, japanese.ConnectCount);

        var appendError = await Assert.ThrowsAsync<RealtimeTranslationException>(
            () => dual.AppendAudioFrameAsync(new byte[Pcm16FramePacketizer.BytesPerFrame]));
        Assert.Equal(RealtimeTranslationErrorKind.NotConnected, appendError.Kind);
    }

    // Given: 行折り返しされた allowlist キー
    // When: Dual を開始する
    // Then: 原文と翻訳 lane の Authorization は正規化後のキーだけを載せる
    [Fact]
    public async Task StartStripsEmbeddedWhitespaceFromAllLaneHeaders()
    {
        var source = new FakeRealtimeServerTransport();
        var english = new FakeRealtimeServerTransport();
        var japanese = new FakeRealtimeServerTransport();
        using var dual = CreateDual(source, english, japanese);

        await dual.StartAsync("sk-proj-AAAA\nBBBB", RealtimeSessionTuning.Default);

        Assert.Equal("Bearer sk-proj-AAAABBBB", source.ConnectedHeaders["Authorization"]);
        Assert.Equal("Bearer sk-proj-AAAABBBB", english.ConnectedHeaders["Authorization"]);
        Assert.Equal("Bearer sk-proj-AAAABBBB", japanese.ConnectedHeaders["Authorization"]);
        await dual.ForceCloseAsync();
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

    // Given: ready な Dual
    // When: 原文接続へ鍵断片付きの runtime error が届く
    // Then: merge 後の Events は transcription code と認証失敗文言だけを出し、鍵は出さない
    [Fact]
    public async Task SourceRuntimeAuthErrorIsMergedWithoutKeyMaterial()
    {
        var source = new FakeRealtimeServerTransport();
        var english = new FakeRealtimeServerTransport();
        var japanese = new FakeRealtimeServerTransport();
        using var dual = CreateDual(source, english, japanese);

        await dual.StartAsync("sk-test", RealtimeSessionTuning.Default);

        source.EnqueueJson(
            """{"type":"error","error":{"message":"Incorrect API key sk-dual-xyz","code":"invalid_api_key"}}""");

        RealtimeTranslationServerEvent.ServerError? error = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (error is null)
        {
            var streamEvent = await dual.Events.ReadAsync(timeout.Token);
            error = streamEvent.Event as RealtimeTranslationServerEvent.ServerError;
        }

        Assert.Equal(RealtimeSourceTranscriptionCodec.ErrorCode, error.Code);
        Assert.Equal("OpenAI APIキーが無効です", error.Message);
        Assert.DoesNotContain("sk-dual-xyz", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-", error.Message, StringComparison.Ordinal);
        await dual.ForceCloseAsync();
    }

    // Given: 3 本の翻訳接続を持つ Dual と選択された言語ペア
    // When: その pair で Start する
    // Then: source と pair 内 2 lane だけが接続され、未使用 lane は接続されない
    [Theory]
    [InlineData(LanguagePair.JaEn, true, true, false)]
    [InlineData(LanguagePair.JaEs, false, true, true)]
    [InlineData(LanguagePair.EnEs, true, false, true)]
    public async Task StartConnectsOnlyTranslationLanesInSelectedPair(
        LanguagePair pair,
        bool expectEnglish,
        bool expectJapanese,
        bool expectSpanish)
    {
        var source = new FakeRealtimeServerTransport();
        var english = new FakeRealtimeServerTransport();
        var japanese = new FakeRealtimeServerTransport();
        var spanish = new FakeRealtimeServerTransport();
        using var dual = CreateDual(source, english, japanese, spanish);

        await dual.StartAsync("sk-test", RealtimeSessionTuning.Default, pair);

        Assert.Equal(1, source.ConnectCount);
        Assert.Equal(expectEnglish ? 1 : 0, english.ConnectCount);
        Assert.Equal(expectJapanese ? 1 : 0, japanese.ConnectCount);
        Assert.Equal(expectSpanish ? 1 : 0, spanish.ConnectCount);
        await dual.ForceCloseAsync();
    }

    // Given: スペイン語を含む言語ペア
    // When: その pair で Dual を開始する
    // Then: 原文 session.update の languages が pair の 2 言語になり、ja-en のまま残らない
    [Theory]
    [InlineData(LanguagePair.JaEs, "ja", "es")]
    [InlineData(LanguagePair.EnEs, "en", "es")]
    public async Task StartSendsSourceLanguagesForSelectedPair(
        LanguagePair pair,
        string firstLanguage,
        string secondLanguage)
    {
        var source = new FakeRealtimeServerTransport();
        var english = new FakeRealtimeServerTransport();
        var japanese = new FakeRealtimeServerTransport();
        var spanish = new FakeRealtimeServerTransport();
        using var dual = CreateDual(source, english, japanese, spanish);

        await dual.StartAsync("sk-test", RealtimeSessionTuning.Default, pair);

        Assert.Equal(
            [firstLanguage, secondLanguage],
            TranscriptionLanguages(FirstSessionUpdateInput(source)));
        await dual.ForceCloseAsync();
    }

    // Given: ja-es で開始した Dual
    // When: 録音中に prompt だけを live update する
    // Then: 2 通目の source session.update も ja/es のまま（ja-en へ戻さない）
    [Fact]
    public async Task UpdateTranscriptionTuningKeepsStartedPairLanguages()
    {
        var source = new FakeRealtimeServerTransport();
        var english = new FakeRealtimeServerTransport();
        var japanese = new FakeRealtimeServerTransport();
        var spanish = new FakeRealtimeServerTransport();
        using var dual = CreateDual(source, english, japanese, spanish);

        await dual.StartAsync("sk-test", RealtimeSessionTuning.Default, LanguagePair.JaEs);
        var sentBefore = source.Sent.Count;

        await dual.UpdateTranscriptionTuningAsync(
            new RealtimeSessionTuning(
                RealtimeTranslationNoiseReduction.FarField,
                RealtimeTranscriptionDelay.Medium,
                "Live glossary update",
                ["Acme"]));
        await WaitUntilSentAsync(source, sentBefore + 1);

        var second = SessionUpdates(source)[^1];
        var transcription = second["session"]!["audio"]!["input"]!["transcription"]!.AsObject();
        Assert.Equal(
            ["ja", "es"],
            transcription["languages"]!.AsArray().Select(node => node!.GetValue<string>()).ToArray());
        Assert.Equal("Live glossary update", transcription["prompt"]!.GetValue<string>());
        await dual.ForceCloseAsync();
    }

    // Given: スペイン語接続を含む Dual と ja-es / en-es
    // When: その pair で Start する
    // Then: 原文だけが gpt-live-transcribe を要求し、接続した翻訳 lane は transcription ブロックを持たない
    [Theory]
    [InlineData(LanguagePair.JaEs)]
    [InlineData(LanguagePair.EnEs)]
    public async Task StartOmitsTranscriptionOnSpanishPairTranslationLanes(LanguagePair pair)
    {
        var source = new FakeRealtimeServerTransport();
        var english = new FakeRealtimeServerTransport();
        var japanese = new FakeRealtimeServerTransport();
        var spanish = new FakeRealtimeServerTransport();
        using var dual = CreateDual(source, english, japanese, spanish);

        await dual.StartAsync("sk-test", RealtimeSessionTuning.Default, pair);

        var sourceTranscription = FirstSessionUpdateInput(source)["transcription"]!.AsObject();
        Assert.Equal("gpt-live-transcribe", sourceTranscription["model"]!.GetValue<string>());
        Assert.Null(FirstSessionUpdateInput(spanish)["transcription"]);

        if (pair == LanguagePair.JaEs)
        {
            Assert.Equal(0, english.ConnectCount);
            Assert.Null(FirstSessionUpdateInput(japanese)["transcription"]);
        }
        else
        {
            Assert.Equal(0, japanese.ConnectCount);
            Assert.Null(FirstSessionUpdateInput(english)["transcription"]);
        }

        await dual.ForceCloseAsync();
    }

    // Given: スペイン語翻訳接続だけ Connect が失敗する Dual
    // When: スペイン語を含む pair で Start する
    // Then: 例外を伝播し、原文と全翻訳接続を ForceClose する
    [Theory]
    [InlineData(LanguagePair.JaEs)]
    [InlineData(LanguagePair.EnEs)]
    public async Task StartFailureForceClosesSpanishPairConnections(LanguagePair pair)
    {
        var source = new FakeRealtimeServerTransport();
        var english = new FakeRealtimeServerTransport();
        var japanese = new FakeRealtimeServerTransport();
        var spanish = new FakeRealtimeServerTransport
        {
            ConnectError = new RealtimeTranslationException(
                RealtimeTranslationErrorKind.RecoverableTransportFailure,
                "spanish connect failed"),
        };
        using var dual = CreateDual(source, english, japanese, spanish);

        var error = await Assert.ThrowsAsync<RealtimeTranslationException>(
            () => dual.StartAsync("sk-test", RealtimeSessionTuning.Default, pair));

        Assert.Equal(RealtimeTranslationErrorKind.RecoverableTransportFailure, error.Kind);
        Assert.True(source.CloseCount >= 1);
        Assert.True(english.CloseCount >= 1);
        Assert.True(japanese.CloseCount >= 1);
        Assert.True(spanish.CloseCount >= 1);

        var appendError = await Assert.ThrowsAsync<RealtimeTranslationException>(
            () => dual.AppendAudioFrameAsync(new byte[Pcm16FramePacketizer.BytesPerFrame]));
        Assert.Equal(RealtimeTranslationErrorKind.NotConnected, appendError.Kind);
    }

    // Given: ja-es で ready な Dual の Spanish CloseAsync だけが失敗する
    // When: ForceCloseAsync する
    // Then: 先頭の失敗を伝播しつつ原文・日本語 lane も閉じ、Events は完了する
    [Fact]
    public async Task ForceCloseContinuesWhenSpanishConnectionThrows()
    {
        var source = new FakeRealtimeServerTransport();
        var english = new FakeRealtimeServerTransport();
        var japanese = new FakeRealtimeServerTransport();
        var spanish = new FakeRealtimeServerTransport();
        using var dual = CreateDual(source, english, japanese, spanish);

        await dual.StartAsync("sk-test", RealtimeSessionTuning.Default, LanguagePair.JaEs);
        var epochBefore = dual.ConnectionEpoch;
        var closeCountBefore = (
            Source: source.CloseCount,
            Japanese: japanese.CloseCount,
            Spanish: spanish.CloseCount);
        spanish.CloseError = new InvalidOperationException("spanish close boom");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => dual.ForceCloseAsync());

        Assert.Equal("spanish close boom", error.Message);
        Assert.True(dual.ConnectionEpoch > epochBefore);
        Assert.True(source.CloseCount > closeCountBefore.Source);
        Assert.True(japanese.CloseCount > closeCountBefore.Japanese);
        Assert.True(spanish.CloseCount > closeCountBefore.Spanish);
        while (dual.Events.TryRead(out _))
        {
        }

        Assert.False(await dual.Events.WaitToReadAsync());
    }

    // Given: preroll を英語 lane へ flush 済みの Dual
    // When: 同じ target を再選択してから後続 frame を送る
    // Then: rolling preroll を再 flush せず、翻訳 lane の frame は増えない
    [Fact]
    public async Task SelectSameTargetDoesNotReflushPreroll()
    {
        var source = new FakeRealtimeServerTransport();
        var english = new FakeRealtimeServerTransport();
        var japanese = new FakeRealtimeServerTransport();
        using var dual = CreateDual(source, english, japanese);

        await dual.StartAsync("sk-test", RealtimeSessionTuning.Default);
        await dual.AppendAudioFrameAsync(Encoding.UTF8.GetBytes("frame-a"));
        await dual.SelectTranslationTargetAsync(RealtimeTranslationOutputLanguage.English);
        await dual.WaitForTranslationDrainAsync();
        Assert.Equal(["frame-a"], english.AppendedFrameTexts());

        await dual.AppendAudioFrameAsync(Encoding.UTF8.GetBytes("frame-b"));
        await dual.WaitForTranslationDrainAsync();
        Assert.Equal(["frame-a", "frame-b"], english.AppendedFrameTexts());

        await dual.SelectTranslationTargetAsync(RealtimeTranslationOutputLanguage.English);
        await dual.WaitForTranslationDrainAsync();

        Assert.Equal(["frame-a", "frame-b"], english.AppendedFrameTexts());
        Assert.Empty(japanese.AppendedFrameTexts());
        await dual.ForceCloseAsync();
    }

    // Given: ForceClose 済みの Dual
    // When: Select / UpdateTuning / Append する
    // Then: いずれも NotConnected になり、停止後の誤送信を許さない
    [Fact]
    public async Task SelectAndUpdateAfterForceCloseAreNotConnected()
    {
        var source = new FakeRealtimeServerTransport();
        var english = new FakeRealtimeServerTransport();
        var japanese = new FakeRealtimeServerTransport();
        using var dual = CreateDual(source, english, japanese);

        await dual.StartAsync("sk-test", RealtimeSessionTuning.Default);
        await dual.ForceCloseAsync();

        var selectError = await Assert.ThrowsAsync<RealtimeTranslationException>(
            () => dual.SelectTranslationTargetAsync(RealtimeTranslationOutputLanguage.English));
        var tuningError = await Assert.ThrowsAsync<RealtimeTranslationException>(
            () => dual.UpdateTranscriptionTuningAsync(RealtimeSessionTuning.Default));
        var appendError = await Assert.ThrowsAsync<RealtimeTranslationException>(
            () => dual.AppendAudioFrameAsync(new byte[Pcm16FramePacketizer.BytesPerFrame]));

        Assert.Equal(RealtimeTranslationErrorKind.NotConnected, selectError.Kind);
        Assert.Equal(RealtimeTranslationErrorKind.NotConnected, tuningError.Kind);
        Assert.Equal(RealtimeTranslationErrorKind.NotConnected, appendError.Kind);
    }

    private static RealtimeTranslationException UnwrapAuthenticationFailure(Exception error)
    {
        if (error is RealtimeTranslationException direct)
        {
            return direct;
        }

        if (error is AggregateException aggregate)
        {
            var inner = aggregate.Flatten().InnerExceptions
                .OfType<RealtimeTranslationException>()
                .FirstOrDefault(candidate =>
                    candidate.Kind == RealtimeTranslationErrorKind.AuthenticationFailed);
            if (inner is not null)
            {
                return inner;
            }
        }

        throw new InvalidOperationException(
            $"Expected AuthenticationFailed, but received {error.GetType().Name}: {error.Message}",
            error);
    }

    private static DualRealtimeTranslationClient CreateDual(
        FakeRealtimeServerTransport source,
        FakeRealtimeServerTransport english,
        FakeRealtimeServerTransport japanese,
        FakeRealtimeServerTransport? spanish = null) =>
        new(
            new RealtimeSourceTranscriptionConnection(source, "test-safety"),
            new RealtimeTranslationConnection(
                RealtimeTranslationOutputLanguage.English,
                english,
                "test-safety"),
            new RealtimeTranslationConnection(
                RealtimeTranslationOutputLanguage.Japanese,
                japanese,
                "test-safety"),
            spanishConnection: spanish is null
                ? null
                : new RealtimeTranslationConnection(
                    RealtimeTranslationOutputLanguage.Spanish,
                    spanish,
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

    private static string[] TranscriptionLanguages(JsonObject input) =>
        input["transcription"]!["languages"]!.AsArray()
            .Select(node => node!.GetValue<string>())
            .ToArray();

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

    private static async Task WaitUntilConnectedAsync(
        FakeRealtimeServerTransport source,
        FakeRealtimeServerTransport english,
        FakeRealtimeServerTransport japanese)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (source.ConnectCount < 1 || english.ConnectCount < 1 || japanese.ConnectCount < 1)
        {
            timeout.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, timeout.Token);
        }
    }

    private static int ConnectCountAfterPairSwitch(
        HashSet<RealtimeTranslationOutputLanguage> firstLanguages,
        HashSet<RealtimeTranslationOutputLanguage> secondLanguages,
        RealtimeTranslationOutputLanguage target) =>
        (firstLanguages.Contains(target) ? 1 : 0) + (secondLanguages.Contains(target) ? 1 : 0);

    private static FakeRealtimeServerTransport UnusedLaneTransport(
        LanguagePair first,
        LanguagePair second,
        FakeRealtimeServerTransport english,
        FakeRealtimeServerTransport japanese,
        FakeRealtimeServerTransport spanish)
    {
        var firstLanguages = first.Languages().Select(language => language.ToOutputLanguage()).ToHashSet();
        var secondLanguages = second.Languages().Select(language => language.ToOutputLanguage()).ToHashSet();
        if (firstLanguages.Contains(RealtimeTranslationOutputLanguage.English)
            && !secondLanguages.Contains(RealtimeTranslationOutputLanguage.English))
        {
            return english;
        }

        if (firstLanguages.Contains(RealtimeTranslationOutputLanguage.Japanese)
            && !secondLanguages.Contains(RealtimeTranslationOutputLanguage.Japanese))
        {
            return japanese;
        }

        if (firstLanguages.Contains(RealtimeTranslationOutputLanguage.Spanish)
            && !secondLanguages.Contains(RealtimeTranslationOutputLanguage.Spanish))
        {
            return spanish;
        }

        throw new InvalidOperationException($"No unused leftover lane from {first} to {second}.");
    }

    private static async Task WaitUntilSessionUpdatedAsync(
        params FakeRealtimeServerTransport[] transports)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (transports.Any(transport => SessionUpdates(transport).Count < 1))
        {
            timeout.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, timeout.Token);
        }
    }
}
