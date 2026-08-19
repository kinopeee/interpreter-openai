using System;
using RealtimeTranslator.Core.Security;
using Xunit;

namespace RealtimeTranslator.Core.Tests;

public sealed class LogSecretRedactorTests
{
    // Given: 小文字 sk- 断片を含むメッセージ
    // When: 伏字化する
    // Then: キー断片は残らない
    [Fact]
    public void RedactReplacesLowercaseApiKeyFragments()
    {
        var redacted = LogSecretRedactor.Redact("invalid key sk-abcdefghi");

        Assert.Equal("invalid key " + LogSecretRedactor.Placeholder, redacted);
    }

    // Given: 大文字 SK- 断片（ケースフォールディング迂回）
    // When: 伏字化する
    // Then: キー断片は残らない
    [Fact]
    public void RedactReplacesUppercaseApiKeyFragments()
    {
        var redacted = LogSecretRedactor.Redact("invalid key SK-ABCDEFGHI");

        Assert.DoesNotContain("SK-ABCDEFGHI", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-abcdefgh", redacted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(LogSecretRedactor.Placeholder, redacted, StringComparison.Ordinal);
    }

    // Given: ZWSP を挟んだ sk- 断片
    // When: 伏字化する
    // Then: 不可視文字を除いたキー断片は残らない
    [Fact]
    public void RedactReplacesZeroWidthObfuscatedApiKeyFragments()
    {
        var redacted = LogSecretRedactor.Redact("invalid key s\u200bk-abcdefghi");

        Assert.DoesNotContain("sk-abcdefghi", redacted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("abcdefghi", redacted, StringComparison.Ordinal);
        Assert.Contains(LogSecretRedactor.Placeholder, redacted, StringComparison.Ordinal);
    }

    // Given: Bearer / Authorization ヘッダ断片
    // When: 伏字化する
    // Then: トークンは残らない
    [Fact]
    public void RedactReplacesBearerAndAuthorization()
    {
        Assert.Equal(LogSecretRedactor.Placeholder, LogSecretRedactor.Redact("Bearer abc.def-ghi"));
        Assert.Equal(LogSecretRedactor.Placeholder, LogSecretRedactor.Redact("Authorization: secret-token"));
    }

    // Given: Base64 文字を含む Bearer と scheme + 資格情報の Authorization
    // When: 伏字化する
    // Then: `+` `/` `=` や Basic の続きも残らない
    [Fact]
    public void RedactReplacesCompleteBearerAndAuthorizationCredentials()
    {
        var bearer = LogSecretRedactor.Redact("token Bearer abc+def/ghi== extra");
        Assert.DoesNotContain("abc+def/ghi==", bearer, StringComparison.Ordinal);
        Assert.Equal("token " + LogSecretRedactor.Placeholder + " extra", bearer);

        var basic = LogSecretRedactor.Redact("Authorization: Basic YWJjZA==");
        Assert.DoesNotContain("YWJjZA==", basic, StringComparison.Ordinal);
        Assert.DoesNotContain("Basic", basic, StringComparison.Ordinal);
        Assert.Equal(LogSecretRedactor.Placeholder, basic);
    }

    // Given: TAB で分断した sk- 断片
    // When: 伏字化する
    // Then: 制御空白を除いたキー断片は残らない
    [Fact]
    public void RedactReplacesTabObfuscatedApiKeyFragments()
    {
        var redacted = LogSecretRedactor.Redact("invalid key s\tk-abcdefghi");

        Assert.DoesNotContain("sk-abcdefghi", redacted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("abcdefghi", redacted, StringComparison.Ordinal);
        Assert.Contains(LogSecretRedactor.Placeholder, redacted, StringComparison.Ordinal);
    }

    // Given: 秘密を含まないメッセージ
    // When: 伏字化する
    // Then: そのまま残る
    [Fact]
    public void RedactPassesThroughPlainMessages()
    {
        const string input = "translation reconnect attempt 2";

        Assert.Equal(input, LogSecretRedactor.Redact(input));
    }
}
