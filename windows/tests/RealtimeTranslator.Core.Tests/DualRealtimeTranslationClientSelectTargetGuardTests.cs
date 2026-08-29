using System;
using System.Text;
using System.Threading.Tasks;
using RealtimeTranslator.Core.Audio;
using RealtimeTranslator.Core.OpenAI;
using RealtimeTranslator.Core.Realtime;
using Xunit;

namespace RealtimeTranslator.Core.Tests;

/// <summary>
/// Dual の同一 target 再選択（pending / preroll を壊さない）と、
/// 未構成 lane への Select 拒否。halt / pair-switch leftover とは交差しない。
/// </summary>
public sealed class DualRealtimeTranslationClientSelectTargetGuardTests
{
    // Given: 英語 target 選択済みで、送信済み frame と未送信 pending がある
    // When: 同じ English target を再選択する
    // Then: preroll を再 flush せず、各 frame は 1 回だけ英語 lane へ届く
    [Fact]
    public async Task SelectingTheSameTargetDoesNotReflushPreroll()
    {
        var source = new FakeRealtimeServerTransport();
        var english = new FakeRealtimeServerTransport();
        var japanese = new FakeRealtimeServerTransport();
        using var dual = CreateJaEn(source, english, japanese);

        await dual.StartAsync("sk-test", RealtimeSessionTuning.Default);
        await dual.SelectTranslationTargetAsync(RealtimeTranslationOutputLanguage.English);
        await dual.AppendAudioFrameAsync(Encoding.UTF8.GetBytes("already-sent"));
        await dual.WaitForTranslationDrainAsync();
        Assert.Equal(["already-sent"], english.AppendedFrameTexts());

        english.SendDelay = TimeSpan.FromMilliseconds(80);
        await dual.AppendAudioFrameAsync(Encoding.UTF8.GetBytes("pending-1"));
        await dual.AppendAudioFrameAsync(Encoding.UTF8.GetBytes("pending-2"));

        await dual.SelectTranslationTargetAsync(RealtimeTranslationOutputLanguage.English);
        english.SendDelay = TimeSpan.Zero;
        await dual.WaitForTranslationDrainAsync();

        Assert.Equal(["already-sent", "pending-1", "pending-2"], english.AppendedFrameTexts());
        Assert.Empty(japanese.AppendedFrameTexts());
        await dual.ForceCloseAsync();
    }

    // Given: ja-en Dual（Spanish 接続は構成していない）
    // When: Spanish target を選ぶ
    // Then: ArgumentException で拒否し、既選択の英語 lane へは送り続ける
    [Fact]
    public async Task SelectingUnconfiguredSpanishTargetOnJaEnIsRejected()
    {
        var source = new FakeRealtimeServerTransport();
        var english = new FakeRealtimeServerTransport();
        var japanese = new FakeRealtimeServerTransport();
        using var dual = CreateJaEn(source, english, japanese);

        await dual.StartAsync("sk-test", RealtimeSessionTuning.Default, LanguagePair.JaEn);
        await dual.SelectTranslationTargetAsync(RealtimeTranslationOutputLanguage.English);
        await dual.AppendAudioFrameAsync(Encoding.UTF8.GetBytes("before"));
        await dual.WaitForTranslationDrainAsync();

        var error = await Assert.ThrowsAsync<ArgumentException>(
            () => dual.SelectTranslationTargetAsync(RealtimeTranslationOutputLanguage.Spanish));

        Assert.Contains("spanish", error.Message, StringComparison.OrdinalIgnoreCase);
        await dual.AppendAudioFrameAsync(Encoding.UTF8.GetBytes("after-reject"));
        await dual.WaitForTranslationDrainAsync();

        Assert.Equal(["before", "after-reject"], english.AppendedFrameTexts());
        Assert.Empty(japanese.AppendedFrameTexts());
        Assert.True(english.ConnectCount >= 1);
        await dual.ForceCloseAsync();
    }

    private static DualRealtimeTranslationClient CreateJaEn(
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
}
