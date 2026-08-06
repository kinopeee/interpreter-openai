using System;
using RealtimeTranslator.Core.Security;
using Xunit;

namespace RealtimeTranslator.Core.Tests;

public sealed class OpenAISafetyIdentifierTests
{
    // Given: 端末ごとに保存する install ID
    // When: safety identifier を生成する
    // Then: 同じ入力で安定し、install ID そのものは含まない SHA-256 hex になる
    [Fact]
    public void HashedValueIsStableAndDoesNotLeakTheInstallIdentifier()
    {
        const string installIdentifier = "4C3A2F1E-0000-4000-8000-000000000001";

        var hashed = OpenAISafetyIdentifier.HashedValue(installIdentifier);

        Assert.Equal(hashed, OpenAISafetyIdentifier.HashedValue(installIdentifier));
        Assert.Equal(64, hashed.Length);
        Assert.DoesNotContain(installIdentifier, hashed, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(hashed, OpenAISafetyIdentifier.HashedValue(installIdentifier + "2"));
    }

    // Given: 空の install ID
    // When: safety identifier を生成する
    // Then: 例外で弾き、空ハッシュを送らない
    [Fact]
    public void HashedValueRejectsBlankInstallIdentifier() =>
        Assert.Throws<ArgumentException>(() => OpenAISafetyIdentifier.HashedValue("  "));
}
