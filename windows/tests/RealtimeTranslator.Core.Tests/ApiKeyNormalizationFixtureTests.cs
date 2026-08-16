using RealtimeTranslator.Core.Security;
using Xunit;

namespace RealtimeTranslator.Core.Tests;

public sealed class ApiKeyNormalizationFixtureTests
{
    public static TheoryData<string> NormalizeCases => SharedFixtures.CaseNames("api-key", "normalize");

    // Given: shared fixture の API キー正規化ケース
    // When: 実装の正規化を行う
    // Then: empty / malformed / valid と expected が一致する
    [Theory]
    [MemberData(nameof(NormalizeCases))]
    public void NormalizeMatchesFixture(string name)
    {
        var fixture = SharedFixtures.Case("api-key", "normalize", name);
        var input = SharedFixtures.Text(fixture["input"]);
        var status = SharedFixtures.Text(fixture["status"]);
        var result = ApiKeyNormalizer.Normalize(input);

        switch (status)
        {
            case "empty":
                Assert.Equal(ApiKeyNormalizationStatus.Empty, result.Status);
                break;
            case "malformed":
                Assert.Equal(ApiKeyNormalizationStatus.Malformed, result.Status);
                break;
            case "valid":
                Assert.Equal(ApiKeyNormalizationStatus.Valid, result.Status);
                Assert.Equal(SharedFixtures.Text(fixture["expected"]), result.Value);
                break;
            default:
                throw new Xunit.Sdk.XunitException("unknown status " + status);
        }
    }
}
