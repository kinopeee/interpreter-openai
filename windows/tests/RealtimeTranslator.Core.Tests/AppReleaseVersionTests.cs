using System.Reflection;
using RealtimeTranslator.Core.Settings;
using Xunit;

namespace RealtimeTranslator.Core.Tests;

public sealed class AppReleaseVersionTests
{
    // Given: 空や空白だけの入力
    // When: 表示値へ正規化する
    // Then: 未リリースの 0.0.0 を返す
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("+build")]
    public void BlankRawBecomesUnpublished(string? raw) =>
        Assert.Equal(AppReleaseVersion.Unpublished, AppReleaseVersion.DisplayValue(raw));

    // Given: リリースタグやアセンブリ InformationalVersion
    // When: 表示値へ正規化する
    // Then: 先頭の v と + 以降のビルドメタデータを落とし、本文だけ残す
    [Theory]
    [InlineData("0.1.0", "0.1.0")]
    [InlineData("v0.1.0", "0.1.0")]
    [InlineData("V0.1.0", "0.1.0")]
    [InlineData("  v0.1.0  ", "0.1.0")]
    [InlineData("0.1.0+abc123", "0.1.0")]
    [InlineData("v0.1.0-rc.1+sha", "0.1.0-rc.1")]
    [InlineData("pr12", "pr12")]
    [InlineData("very", "very")]
    public void DisplayValueStripsTagPrefixAndBuildMetadata(string raw, string expected) =>
        Assert.Equal(expected, AppReleaseVersion.DisplayValue(raw));

    // Given: 未リリースの既定 Version (Directory.Build.props)
    // When: Core アセンブリの InformationalVersion を読む
    // Then: 設定画面と同じ 0.0.0 になる
    [Fact]
    public void UnpublishedAssemblyVersionIsZero()
    {
        var informational = typeof(AppReleaseVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        Assert.Equal(AppReleaseVersion.Unpublished, AppReleaseVersion.DisplayValue(informational));
    }
}
