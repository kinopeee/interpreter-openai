using Xunit;

namespace RealtimeTranslator.Platform.Tests;

/// <summary>
/// 静的な <c>AppLogger</c> の sink を差し替えるテストの collection。
/// 並列実行中の他テストのログが記録用 sink へ混ざらないよう、単独で実行する。
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AppLoggerSinkCollectionDefinition
{
    public const string Name = "AppLogger sink";
}
