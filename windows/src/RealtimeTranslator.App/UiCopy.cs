using System;
using System.Collections.Generic;
using System.Globalization;
using RealtimeTranslator.Core.Audio;
using RealtimeTranslator.Core.Localization;
using RealtimeTranslator.Core.OpenAI;

namespace RealtimeTranslator.App;

/// <summary>起動時に固定した UserCopy への薄いアクセス。スレッドカルチャは変えない。</summary>
internal static class UiCopy
{
    internal const string Hotkey = "Ctrl + Alt + Space";

    internal static void Install(UiLanguagePreference preference) =>
        UserCopy.InstallFromPreference(
            preference,
            CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);

    internal static string Text(string key) => UserCopy.Current.Text(key);

    internal static string Format(string key, string name, string value) =>
        UserCopy.Current.Format(key, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [name] = value,
        });

    internal static string Format(string key, IReadOnlyDictionary<string, string> substitutions) =>
        UserCopy.Current.Format(key, substitutions);

    internal static string PairName(LanguagePair pair) => pair switch
    {
        LanguagePair.JaEn => Text("settings.languagePair.jaEn"),
        LanguagePair.JaEs => Text("settings.languagePair.jaEs"),
        LanguagePair.EnEs => Text("settings.languagePair.enEs"),
        _ => Text("settings.languagePair.jaEn"),
    };

    internal static string PresetName(string id) => id switch
    {
        "software_development" => Text("settings.preset.softwareDevelopment"),
        "business_meeting" => Text("settings.preset.businessMeeting"),
        "hackathon" => Text("settings.preset.hackathon"),
        _ => id,
    };

    internal static string DelayName(RealtimeTranscriptionDelay delay) => delay switch
    {
        RealtimeTranscriptionDelay.Minimal => Text("settings.transcriptionDelay.minimal"),
        RealtimeTranscriptionDelay.Low => Text("settings.transcriptionDelay.low"),
        RealtimeTranscriptionDelay.Medium => Text("settings.transcriptionDelay.medium"),
        RealtimeTranscriptionDelay.High => Text("settings.transcriptionDelay.high"),
        RealtimeTranscriptionDelay.XHigh => Text("settings.transcriptionDelay.xhigh"),
        _ => delay.ToWireValue(),
    };

    internal static string NoiseName(RealtimeTranslationNoiseReduction noise) => noise switch
    {
        RealtimeTranslationNoiseReduction.NearField => Text("settings.noiseReduction.nearField"),
        RealtimeTranslationNoiseReduction.FarField => Text("settings.noiseReduction.farField"),
        _ => noise.ToWireValue(),
    };
}
