using System;
using System.Linq;
using RealtimeTranslator.Core.Audio;
using RealtimeTranslator.Core.Realtime;
using RealtimeTranslator.Core.OpenAI;
using Xunit;

namespace RealtimeTranslator.Core.Tests;

public sealed class SwitchStabilityFixtureTests
{
    public static TheoryData<string> Cases =>
        SharedFixtures.CaseNames("routing", "switchStability");

    // Given: 同じ原文を異なる delta 分割で表した switchStability fixture
    // When: processor と同じ routing window / tracker / selector の流れを各分割へ適用する
    // Then: 切替回数・最終 target・最初の split offset が分割に依存しない
    [Theory]
    [MemberData(nameof(Cases))]
    public void SwitchStabilityMatchesFixture(string name)
    {
        var fixture = SharedFixtures.Case("routing", "switchStability", name);
        var pair = LanguagePairExtensions.ParseLanguagePair(
            SharedFixtures.Text(fixture["pair"]));
        var initialTarget = RealtimeTranslationWireValues.ParseOutputLanguage(
            SharedFixtures.Text(fixture["initialTarget"]));
        string? joinedText = null;
        int? firstSplitOffset = null;

        foreach (var splitNode in fixture["splits"]!.AsArray())
        {
            var split = splitNode!.AsObject();
            var deltas = split["deltas"]!.AsArray();
            var splitText = string.Concat(deltas.Select(SharedFixtures.Text));
            if (joinedText is null)
            {
                joinedText = splitText;
            }
            else
            {
                Assert.Equal(joinedText, splitText);
            }

            var target = initialTarget;
            var source = string.Empty;
            var routing = string.Empty;
            var tracker = new SourceBoundaryTracker();
            var reverseEvidenceCount = 0;
            var switchCount = 0;
            int? switchDelta = null;
            int? sourceLengthAtSwitch = null;
            int? splitOffset = null;

            for (var index = 0; index < deltas.Count; index += 1)
            {
                var delta = SharedFixtures.Text(deltas[index]);
                var deltaStart = source.Length;
                source += delta;
                routing = RoutingSourceTextWindow.Trim(routing + delta, pair);
                var evidence = SpokenLanguageDetector.RecentEvidence(routing, pair);
                var currentLanguage = pair.Counterpart(target)
                    ?? throw new Xunit.Sdk.XunitException("missing current language");
                tracker.Observe(
                    source,
                    deltaStart,
                    0,
                    pair,
                    currentLanguage,
                    0);
                var selection = TranslationTargetSelector.Select(
                    pair,
                    target,
                    reverseEvidenceCount,
                    evidence,
                    tracker.OppositeScriptRun(source));
                reverseEvidenceCount = selection.ReverseEvidenceCount;

                if (selection.Target is not { } nextTarget || nextTarget == target)
                {
                    continue;
                }

                var offset = tracker.CandidateOffset ?? deltaStart;
                if (switchCount == 0)
                {
                    switchDelta = index;
                    sourceLengthAtSwitch = source.Length;
                    splitOffset = offset;
                }

                switchCount += 1;
                source = source[offset..];
                routing = RoutingSourceTextWindow.Trim(source, pair);
                tracker.Reset();
                target = nextTarget;
                reverseEvidenceCount = 0;
            }

            Assert.Equal(
                SharedFixtures.Number(fixture["expectedSwitchCount"]),
                switchCount);
            Assert.Equal(
                SharedFixtures.Text(fixture["expectedFinalTarget"]),
                target.ToWireValue());
            Assert.Equal(
                SharedFixtures.OptionalNumber(split["expectedSwitchAtDelta"]),
                switchDelta);
            Assert.Equal(
                SharedFixtures.OptionalNumber(split["expectedSourceLengthAtSwitch"]),
                sourceLengthAtSwitch);
            if (firstSplitOffset is null)
            {
                firstSplitOffset = splitOffset;
            }
            else
            {
                Assert.Equal(firstSplitOffset, splitOffset);
            }
        }

        Assert.Equal(
            SharedFixtures.OptionalNumber(fixture["expectedSplitOffset"]),
            firstSplitOffset);
    }
}
