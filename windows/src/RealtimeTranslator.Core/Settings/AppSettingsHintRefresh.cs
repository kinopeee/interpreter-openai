using System;
using RealtimeTranslator.Core.Audio;
using RealtimeTranslator.Core.OpenAI;

namespace RealtimeTranslator.Core.Settings;

/// <summary>
/// 言語ペア変更時に、既定ヒントのままの prompt / keywords を新ペアの既定へ更新する。
/// macOS 版 <c>AppSettings.refreshDefaultTranscriptionHintsIfNeeded</c> と同義。
/// </summary>
public static class AppSettingsHintRefresh
{
    private static readonly LanguagePair[] AllPairs = [LanguagePair.JaEn, LanguagePair.JaEs, LanguagePair.EnEs];

    public static AppSettingsData ApplyLanguagePair(AppSettingsData settings, LanguagePair newPair)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.LanguagePair == newPair)
        {
            return settings;
        }

        var next = settings with { LanguagePair = newPair };
        if (IsKnownDefaultPrompt(settings.TranscriptionPrompt))
        {
            next = next with { TranscriptionPrompt = RealtimeSessionTuning.DefaultPromptForPair(newPair) };
        }

        if (IsKnownDefaultKeywordsText(settings.TranscriptionKeywordsText))
        {
            next = next with
            {
                TranscriptionKeywordsText = RealtimeSessionTuning.KeywordsText(
                    RealtimeSessionTuning.DefaultKeywordsForPair(newPair)
                ),
            };
        }

        return next;
    }

    public static bool IsKnownDefaultPrompt(string prompt) =>
        prompt == RealtimeSessionTuning.DefaultPromptForPair(LanguagePair.JaEn)
        || prompt == RealtimeSessionTuning.DefaultPromptForPair(LanguagePair.JaEs)
        || prompt == RealtimeSessionTuning.DefaultPromptForPair(LanguagePair.EnEs);

    public static bool IsKnownDefaultKeywordsText(string keywordsText)
    {
        foreach (var pair in AllPairs)
        {
            if (keywordsText == RealtimeSessionTuning.KeywordsText(RealtimeSessionTuning.DefaultKeywordsForPair(pair)))
            {
                return true;
            }
        }

        return false;
    }
}
