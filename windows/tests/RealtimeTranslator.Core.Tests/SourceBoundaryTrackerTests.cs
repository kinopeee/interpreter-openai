using System;
using System.Collections.Generic;
using RealtimeTranslator.Core.Audio;
using RealtimeTranslator.Core.Realtime;
using Xunit;

namespace RealtimeTranslator.Core.Tests;

public sealed class SourceBoundaryTrackerTests
{
    [Fact]
    public void GenerationMismatchResetsCandidate()
    {
        // Given: tracker が一つの generation を観測した
        var tracker = new SourceBoundaryTracker();
        tracker.Observe("あいうえお To", 0, 1, LanguagePair.JaEn, SpokenLanguage.Japanese, 0);
        Assert.Equal(5, tracker.CandidateOffset);

        // When: generation が変わった source を観測する
        tracker.Observe("日本語", 0, 2, LanguagePair.JaEn, SpokenLanguage.Japanese, 0);

        // Then: 古い候補を捨てる
        Assert.Null(tracker.CandidateOffset);
    }

    [Theory]
    [InlineData("It is over.", " Pero la reunión es", 11)]
    [InlineData("It is over and", " la reunión está", 14)]
    public void EnEsCandidateUsesSentenceOrCueStart(
        string first,
        string second,
        int expected)
    {
        // Given: current language が英語の en-es tracker
        var tracker = new SourceBoundaryTracker();
        var source = first + second;

        // When: reverse evidence が一回観測される
        tracker.Observe(source, first.Length, 1, LanguagePair.EnEs, SpokenLanguage.English, 1);

        // Then: UTF-16 boundary は規則どおり
        Assert.Equal(expected, tracker.CandidateOffset);
    }
}
