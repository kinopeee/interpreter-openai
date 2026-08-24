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

    // Given: TAB が k とハイフン、またはキー本体を分断している
    // When: 伏字化する
    // Then: 空白を挟んでもキー断片は残らない
    [Fact]
    public void RedactReplacesTabSplitApiKeyHyphenAndBody()
    {
        foreach (var input in new[]
        {
            "invalid key sk\t-abcdefghi",
            "invalid key sk-\tabcdefghi",
            "invalid key sk-abcd\tefghi",
        })
        {
            var redacted = LogSecretRedactor.Redact(input);

            Assert.DoesNotContain("abcdefghi", redacted, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("abcd", redacted, StringComparison.Ordinal);
            Assert.Contains(LogSecretRedactor.Placeholder, redacted, StringComparison.Ordinal);
        }
    }

    // Given: TAB で語を分けた Bearer 資格情報
    // When: 伏字化する
    // Then: 制御空白を消して連結せず、トークンは残らない
    [Fact]
    public void RedactReplacesTabSeparatedBearerCredentials()
    {
        var redacted = LogSecretRedactor.Redact("token Bearer\tabc+def/ghi== extra");

        Assert.DoesNotContain("abc+def/ghi==", redacted, StringComparison.Ordinal);
        Assert.Equal("token " + LogSecretRedactor.Placeholder + " extra", redacted);
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

    // Given: OpenAI-Safety-Identifier ヘッダ断片
    // When: 伏字化する
    // Then: 識別子は残らず、前後の文言だけが残る
    [Fact]
    public void RedactReplacesOpenAISafetyIdentifierHeader()
    {
        const string identifier = "deadbeefcafebabe0123456789abcdef";
        var redacted = LogSecretRedactor.Redact(
            "hdr OpenAI-Safety-Identifier: " + identifier + " extra");

        Assert.DoesNotContain(identifier, redacted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Safety-Identifier", redacted, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("hdr " + LogSecretRedactor.Placeholder + " extra", redacted);
    }

    // Given: ZWSP で分断した Safety-Identifier ヘッダ
    // When: 伏字化する
    // Then: 不可視文字を除いた識別子は残らない
    [Fact]
    public void RedactReplacesZeroWidthObfuscatedSafetyIdentifierHeader()
    {
        const string identifier = "deadbeefcafebabe0123456789abcdef";
        var redacted = LogSecretRedactor.Redact(
            "hdr OpenAI-Safety-\u200bIdentifier: " + identifier + " extra");

        Assert.DoesNotContain(identifier, redacted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(LogSecretRedactor.Placeholder, redacted, StringComparison.Ordinal);
    }

    // Given: ログメッセージに混入した install UUID
    // When: 伏字化する
    // Then: UUID は残らず、周辺テキストは残る
    [Fact]
    public void RedactReplacesRawInstallUuid()
    {
        const string installId = "550e8400-e29b-41d4-a716-446655440000";
        var redacted = LogSecretRedactor.Redact("install id " + installId + " stored");

        Assert.DoesNotContain(installId, redacted, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("install id " + LogSecretRedactor.Placeholder + " stored", redacted);
    }

    // Given: TAB で語を分けた api key フレーズ
    // When: 照合用に正規化する
    // Then: 制御空白は ASCII 空白へ寄り、連結して 401 を誤検出しない
    [Fact]
    public void SecretTextNormalizesControlWhitespaceWithoutJoiningStatusCodes()
    {
        Assert.Equal(
            "invalid api key provided",
            SecretText.NormalizeForMatch("invalid api\tkey provided"));
        Assert.Equal("code 4 01", SecretText.NormalizeForMatch("code 4\t01"));
        Assert.DoesNotContain("401", SecretText.NormalizeForMatch("code 4\t01"), StringComparison.Ordinal);
    }
}
