using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RealtimeTranslator.Core.Audio;
using RealtimeTranslator.Core.OpenAI;
using RealtimeTranslator.Core.Realtime;
using Xunit;

namespace RealtimeTranslator.Core.Tests;

/// <summary>
/// Dual の開始前 NotConnected、停止後 CloseGracefully の no-op、
/// drain 待ちのキャンセル、停止時 in-flight frame を含む drain 予算。
/// Halt / Drain / Parity の開いている PR とは交差しない。
/// </summary>
public sealed class DualRealtimeTranslationClientLifecycleTests
{
    // Given: Start していない Dual
    // When: Append / Select / UpdateTuning する
    // Then: いずれも NotConnected になり、未接続への誤送信を許さない
    [Fact]
    public async Task AppendSelectUpdateBeforeStartAreNotConnected()
    {
        var source = new FakeRealtimeServerTransport();
        var english = new FakeRealtimeServerTransport();
        var japanese = new FakeRealtimeServerTransport();
        using var dual = CreateDual(source, english, japanese);

        var appendError = await Assert.ThrowsAsync<RealtimeTranslationException>(
            () => dual.AppendAudioFrameAsync(Frame(0x11)));
        var selectError = await Assert.ThrowsAsync<RealtimeTranslationException>(
            () => dual.SelectTranslationTargetAsync(RealtimeTranslationOutputLanguage.English));
        var tuningError = await Assert.ThrowsAsync<RealtimeTranslationException>(
            () => dual.UpdateTranscriptionTuningAsync(RealtimeSessionTuning.Default));

        Assert.Equal(RealtimeTranslationErrorKind.NotConnected, appendError.Kind);
        Assert.Equal(RealtimeTranslationErrorKind.NotConnected, selectError.Kind);
        Assert.Equal(RealtimeTranslationErrorKind.NotConnected, tuningError.Kind);
        Assert.Equal(0, source.ConnectCount);
        Assert.Equal(0, english.ConnectCount);
        Assert.Equal(0, japanese.ConnectCount);
    }

    // Given: 一度も Start していない Dual
    // When: CloseGracefullyAsync する
    // Then: 接続せず例外も出さず、Events は完了する（stop drain の WaitToReadAsync が固まらない）
    [Fact]
    public async Task CloseGracefullyWhenNeverStartedIsNoOp()
    {
        var source = new FakeRealtimeServerTransport { AutoCloseResponses = true };
        var english = new FakeRealtimeServerTransport { AutoCloseResponses = true };
        var japanese = new FakeRealtimeServerTransport { AutoCloseResponses = true };
        using var dual = CreateDual(source, english, japanese);

        await dual.CloseGracefullyAsync();

        Assert.Equal(0, source.ConnectCount);
        Assert.Equal(0, source.CloseCount);
        Assert.Equal(0, english.CloseCount);
        Assert.Equal(0, japanese.CloseCount);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        Assert.False(await dual.Events.WaitToReadAsync(timeout.Token));
    }

    // Given: 一度も Start していない Dual
    // When: ForceCloseAsync する
    // Then: 例外なく Events を完了し、接続は tearDown する（drain 待ちが固まらない）
    [Fact]
    public async Task ForceCloseWhenNeverStartedCompletesEvents()
    {
        var source = new FakeRealtimeServerTransport();
        var english = new FakeRealtimeServerTransport();
        var japanese = new FakeRealtimeServerTransport();
        using var dual = CreateDual(source, english, japanese);

        await dual.ForceCloseAsync();

        Assert.Equal(0, source.ConnectCount);
        Assert.True(source.CloseCount >= 1);
        Assert.True(english.CloseCount >= 1);
        Assert.True(japanese.CloseCount >= 1);
        while (dual.Events.TryRead(out _))
        {
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.False(await dual.Events.WaitToReadAsync(timeout.Token));
    }

    // Given: ForceClose 済みの Dual
    // When: CloseGracefullyAsync する
    // Then: session.close を再送せず、Events は完了したまま
    [Fact]
    public async Task CloseGracefullyAfterForceCloseIsNoOp()
    {
        var source = new FakeRealtimeServerTransport { AutoCloseResponses = true };
        var english = new FakeRealtimeServerTransport { AutoCloseResponses = true };
        var japanese = new FakeRealtimeServerTransport { AutoCloseResponses = true };
        using var dual = CreateDual(source, english, japanese);

        await dual.StartAsync("sk-test", RealtimeSessionTuning.Default);
        await dual.ForceCloseAsync();
        var sourceCloseAfterForce = source.CloseCount;
        var englishCloseAfterForce = english.CloseCount;
        var englishSentAfterForce = english.Sent.Count;

        await dual.CloseGracefullyAsync();

        Assert.Equal(sourceCloseAfterForce, source.CloseCount);
        Assert.Equal(englishCloseAfterForce, english.CloseCount);
        Assert.Equal(englishSentAfterForce, english.Sent.Count);
        while (dual.Events.TryRead(out _))
        {
        }

        Assert.False(await dual.Events.WaitToReadAsync());
    }

    // Given: CloseGracefully 済みの Dual（通常の録音停止）
    // When: Select / UpdateTuning / Append する
    // Then: いずれも NotConnected になり、閉じかけ socket への誤送信を許さない
    [Fact]
    public async Task SelectAndUpdateAfterCloseGracefullyAreNotConnected()
    {
        var source = new FakeRealtimeServerTransport { AutoCloseResponses = true };
        var english = new FakeRealtimeServerTransport { AutoCloseResponses = true };
        var japanese = new FakeRealtimeServerTransport { AutoCloseResponses = true };
        using var dual = CreateDual(source, english, japanese);

        await dual.StartAsync("sk-test", RealtimeSessionTuning.Default);
        await dual.CloseGracefullyAsync();

        var selectError = await Assert.ThrowsAsync<RealtimeTranslationException>(
            () => dual.SelectTranslationTargetAsync(RealtimeTranslationOutputLanguage.English));
        var tuningError = await Assert.ThrowsAsync<RealtimeTranslationException>(
            () => dual.UpdateTranscriptionTuningAsync(RealtimeSessionTuning.Default));
        var appendError = await Assert.ThrowsAsync<RealtimeTranslationException>(
            () => dual.AppendAudioFrameAsync(Frame(0x41)));

        Assert.Equal(RealtimeTranslationErrorKind.NotConnected, selectError.Kind);
        Assert.Equal(RealtimeTranslationErrorKind.NotConnected, tuningError.Kind);
        Assert.Equal(RealtimeTranslationErrorKind.NotConnected, appendError.Kind);
    }

    // Given: 英語 target と preroll を残したまま正常停止した Dual
    // When: CloseGracefully のあと Start し直し、判定前に frame を送ってから English を選ぶ
    // Then: 前回の target/preroll は引き継がず、新録音の rolling preroll だけが flush される
    [Fact]
    public async Task StartAfterSuccessfulCloseGracefullyDoesNotReusePreviousTargetOrPreroll()
    {
        var source = new FakeRealtimeServerTransport { AutoCloseResponses = true };
        var english = new FakeRealtimeServerTransport { AutoCloseResponses = true };
        var japanese = new FakeRealtimeServerTransport { AutoCloseResponses = true };
        using var dual = CreateDual(source, english, japanese);

        await dual.StartAsync("sk-test", RealtimeSessionTuning.Default);
        await dual.AppendAudioFrameAsync(Encoding.UTF8.GetBytes("old-preroll"));
        await dual.SelectTranslationTargetAsync(RealtimeTranslationOutputLanguage.English);
        await dual.WaitForTranslationDrainAsync();
        Assert.Equal(["old-preroll"], english.AppendedFrameTexts());

        await dual.CloseGracefullyAsync();

        await dual.StartAsync("sk-test", RealtimeSessionTuning.Default);
        await dual.AppendAudioFrameAsync(Encoding.UTF8.GetBytes("new-preroll"));
        await dual.WaitForTranslationDrainAsync();
        var afterRestart = english.AppendedFrameTexts();
        Assert.DoesNotContain("new-preroll", afterRestart);

        await dual.SelectTranslationTargetAsync(RealtimeTranslationOutputLanguage.English);
        await dual.WaitForTranslationDrainAsync();
        var afterSelect = english.AppendedFrameTexts();
        Assert.Equal("new-preroll", afterSelect[^1]);
        Assert.Equal(1, afterSelect.Count(frame => frame == "old-preroll"));
        await dual.ForceCloseAsync();
    }

    // Given: 翻訳送信が停滞して drain 待ち中の Dual
    // When: キャンセル済み token で WaitForTranslationDrainAsync する
    // Then: timeout まで待たず OperationCanceledException になる
    [Fact]
    public async Task WaitForTranslationDrainHonorsCancellation()
    {
        var source = new FakeRealtimeServerTransport();
        var english = new FakeRealtimeServerTransport();
        var japanese = new FakeRealtimeServerTransport();
        using var dual = CreateDual(source, english, japanese);

        await dual.StartAsync("sk-test", RealtimeSessionTuning.Default);
        await dual.SelectTranslationTargetAsync(RealtimeTranslationOutputLanguage.English);
        english.SendDelay = TimeSpan.FromSeconds(30);
        await dual.AppendAudioFrameAsync(Frame(0x21));

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => dual.WaitForTranslationDrainAsync(TimeSpan.FromSeconds(5), cancelled.Token));
        await dual.ForceCloseAsync();
    }

    // Given: 翻訳送信が停滞し、ポンプが 1 frame を送信中、待ち行列は空
    // When: 停止時 drain 予算を読む
    // Then: pending 0 でも in-flight の +1 が入り、追加 enqueue では pending+in-flight になる
    [Fact]
    public async Task CloseDrainTimeoutIncludesInFlightPumpFrame()
    {
        var source = new FakeRealtimeServerTransport();
        var english = new FakeRealtimeServerTransport();
        var japanese = new FakeRealtimeServerTransport();
        var baseTimeout = TimeSpan.FromMilliseconds(50);
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
            translationDrainTimeout: baseTimeout);

        await dual.StartAsync("sk-test", RealtimeSessionTuning.Default);
        await dual.SelectTranslationTargetAsync(RealtimeTranslationOutputLanguage.English);
        english.SendDelay = TimeSpan.FromSeconds(30);
        await dual.AppendAudioFrameAsync(Frame(0x31));

        var inFlightOnly = DualRealtimeTranslationClient.ResolveTranslationDrainTimeout(
            baseTimeout,
            pendingFrameCount: 1);
        await WaitUntilAsync(() => dual.CloseDrainTimeoutForTests == inFlightOnly);

        await dual.AppendAudioFrameAsync(Frame(0x32));
        await dual.AppendAudioFrameAsync(Frame(0x33));

        Assert.Equal(
            DualRealtimeTranslationClient.ResolveTranslationDrainTimeout(baseTimeout, pendingFrameCount: 3),
            dual.CloseDrainTimeoutForTests);
        await dual.ForceCloseAsync();
    }

    // Given: 言語判定済みで原文送信が停滞している Dual
    // When: 原文 Append の await 中に ForceClose する
    // Then: 復帰後も翻訳 lane へはその frame を enqueue せず、停止後の誤送信を許さない
    [Fact]
    public async Task ForceCloseDuringSourceAppendDoesNotEnqueueTranslationFrame()
    {
        var source = new FakeRealtimeServerTransport();
        var english = new FakeRealtimeServerTransport();
        var japanese = new FakeRealtimeServerTransport();
        using var dual = CreateDual(source, english, japanese);

        await dual.StartAsync("sk-test", RealtimeSessionTuning.Default);
        await dual.SelectTranslationTargetAsync(RealtimeTranslationOutputLanguage.English);
        await dual.AppendAudioFrameAsync(Encoding.UTF8.GetBytes("beforeClose"));
        await dual.WaitForTranslationDrainAsync();
        Assert.Equal(["beforeClose"], english.AppendedFrameTexts());

        source.SendDelay = TimeSpan.FromSeconds(2);
        var appendTask = dual.AppendAudioFrameAsync(Encoding.UTF8.GetBytes("duringClose"));
        await Task.Delay(50);
        await dual.ForceCloseAsync();
        await appendTask;

        Assert.Equal(["beforeClose"], english.AppendedFrameTexts());
        Assert.DoesNotContain("duringClose", english.AppendedFrameTexts());
        var appendError = await Assert.ThrowsAsync<RealtimeTranslationException>(
            () => dual.AppendAudioFrameAsync(Encoding.UTF8.GetBytes("afterClose")));
        Assert.Equal(RealtimeTranslationErrorKind.NotConnected, appendError.Kind);
    }

    // Given: en-es で Spanish target 選択済み、スペイン語送信が停滞して pending が溜まっている Dual
    // When: drain せず English target へ切り替える
    // Then: 旧 target 向け未送信 frame は破棄され、rolling preroll だけが新 target へ届く
    [Fact]
    public async Task SelectTranslationTargetWhileSendStalledClearsPendingOldTargetFrames()
    {
        var source = new FakeRealtimeServerTransport();
        var english = new FakeRealtimeServerTransport();
        var japanese = new FakeRealtimeServerTransport();
        var spanish = new FakeRealtimeServerTransport();
        using var dual = CreateDual(source, english, japanese, spanish);

        await dual.StartAsync("sk-test", RealtimeSessionTuning.Default, LanguagePair.EnEs);
        await dual.SelectTranslationTargetAsync(RealtimeTranslationOutputLanguage.Spanish);
        spanish.SendDelay = TimeSpan.FromMilliseconds(200);
        await dual.AppendAudioFrameAsync(Encoding.UTF8.GetBytes("f1"));
        await Task.Delay(50);
        await dual.AppendAudioFrameAsync(Encoding.UTF8.GetBytes("f2"));
        await dual.AppendAudioFrameAsync(Encoding.UTF8.GetBytes("f3"));

        await dual.SelectTranslationTargetAsync(RealtimeTranslationOutputLanguage.English);
        await dual.WaitForTranslationDrainAsync();

        Assert.Equal(0, japanese.ConnectCount);
        Assert.DoesNotContain("f2", spanish.AppendedFrameTexts());
        Assert.DoesNotContain("f3", spanish.AppendedFrameTexts());
        Assert.Equal(["f1", "f2", "f3"], english.AppendedFrameTexts());
        await dual.ForceCloseAsync();
    }

    // Given: 英語 target 選択済みで preroll を flush した Dual
    // When: 判定前相当で Select(null) したあと frame を送り、再度 English を選ぶ
    // Then: 未選択中は翻訳 lane へ送らず、rolling preroll は維持されて再選択で flush される
    [Fact]
    public async Task SelectNullClearsPendingKeepsPrerollUntilReselect()
    {
        var source = new FakeRealtimeServerTransport();
        var english = new FakeRealtimeServerTransport();
        var japanese = new FakeRealtimeServerTransport();
        using var dual = CreateDual(source, english, japanese);

        await dual.StartAsync("sk-test", RealtimeSessionTuning.Default);
        await dual.AppendAudioFrameAsync(Encoding.UTF8.GetBytes("before-null"));
        await dual.SelectTranslationTargetAsync(RealtimeTranslationOutputLanguage.English);
        await dual.WaitForTranslationDrainAsync();
        Assert.Equal(["before-null"], english.AppendedFrameTexts());

        await dual.SelectTranslationTargetAsync(null);
        await dual.AppendAudioFrameAsync(Encoding.UTF8.GetBytes("while-undetermined"));
        await dual.WaitForTranslationDrainAsync();
        Assert.Equal(["before-null"], english.AppendedFrameTexts());
        Assert.Contains("while-undetermined", source.AppendedFrameTexts());
        Assert.Empty(japanese.AppendedFrameTexts());

        await dual.SelectTranslationTargetAsync(RealtimeTranslationOutputLanguage.English);
        await dual.WaitForTranslationDrainAsync();

        // rolling preroll は Select(null) 前の frame も含むため、再 flush で before-null が重複し得る。
        Assert.Contains("while-undetermined", english.AppendedFrameTexts());
        Assert.Empty(japanese.AppendedFrameTexts());
        await dual.ForceCloseAsync();
    }

    // Given: 英語 target 選択済みで preroll を flush した Dual
    // When: セグメント境界相当の ResetAudioRouting のあと frame を送り、同じ target を選び直す
    // Then: Reset 後は翻訳 lane へ送らず、rolling preroll は次の Select で flush される
    [Fact]
    public async Task ResetAudioRoutingKeepsPrerollForNextSelect()
    {
        var source = new FakeRealtimeServerTransport();
        var english = new FakeRealtimeServerTransport();
        var japanese = new FakeRealtimeServerTransport();
        using var dual = CreateDual(source, english, japanese);

        await dual.StartAsync("sk-test", RealtimeSessionTuning.Default);
        await dual.AppendAudioFrameAsync(Encoding.UTF8.GetBytes("before-reset"));
        await dual.SelectTranslationTargetAsync(RealtimeTranslationOutputLanguage.English);
        await dual.WaitForTranslationDrainAsync();
        Assert.Equal(["before-reset"], english.AppendedFrameTexts());

        await dual.ResetAudioRoutingAsync();
        await dual.AppendAudioFrameAsync(Encoding.UTF8.GetBytes("after-reset"));
        await dual.WaitForTranslationDrainAsync();
        Assert.Equal(["before-reset"], english.AppendedFrameTexts());
        Assert.Contains("after-reset", source.AppendedFrameTexts());

        await dual.SelectTranslationTargetAsync(RealtimeTranslationOutputLanguage.English);
        await dual.WaitForTranslationDrainAsync();

        // rolling preroll は Reset 前の frame も含むため、再 flush で before-reset が重複し得る。
        Assert.Contains("after-reset", english.AppendedFrameTexts());
        Assert.Empty(japanese.AppendedFrameTexts());
        await dual.ForceCloseAsync();
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

    private static byte[] Frame(byte fill)
    {
        var frame = new byte[Pcm16FramePacketizer.BytesPerFrame];
        Array.Fill(frame, fill);
        return frame;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(5);
        }

        Assert.Fail("condition was not met in time");
    }
}
