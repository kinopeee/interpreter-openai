using System.Runtime.CompilerServices;

namespace RealtimeTranslator.App;

/// <summary>
/// WPF の自動生成 Main より前に WinForms bootstrap を走らせ、
/// csproj の ApplicationHighDpiMode (PerMonitorV2) をプロセスへ適用する。
/// マニフェスト DPI は UseWindowsForms 下で WFO0003 になるため使わない。
/// </summary>
internal static class DpiBootstrap
{
    [ModuleInitializer]
    internal static void Initialize() => ApplicationConfiguration.Initialize();
}
