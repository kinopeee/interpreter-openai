using System.Reflection;
using RealtimeTranslator.Core.Settings;

namespace RealtimeTranslator.App;

/// <summary>起動中プロセスの InformationalVersion を設定画面向けの表示値にする。</summary>
internal static class AppReleaseVersionInfo
{
    internal static string CurrentDisplayValue()
    {
        var informational = typeof(App).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        return AppReleaseVersion.DisplayValue(informational);
    }
}
