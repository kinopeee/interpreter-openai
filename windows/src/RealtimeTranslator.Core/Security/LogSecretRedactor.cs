using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace RealtimeTranslator.Core.Security;

/// <summary>
/// ログへ出す前の伏字化。API キー・Bearer・Authorization・install UUID を残さない。
/// </summary>
public static partial class LogSecretRedactor
{
    public const string Placeholder = "[redacted]";

    public static string Redact(string message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var redacted = SecretText.StripFormatAndControl(message);
        foreach (var pattern in SecretPatterns)
        {
            redacted = pattern.Replace(redacted, Placeholder);
        }

        return redacted;
    }

    private static IEnumerable<Regex> SecretPatterns
    {
        get
        {
            yield return ApiKeyPattern();
            yield return BearerPattern();
            yield return AuthorizationHeaderPattern();
            yield return SafetyIdentifierPattern();
            yield return UuidPattern();
        }
    }

    [GeneratedRegex(@"(?i)sk-[A-Za-z0-9_\-]{4,}", RegexOptions.None, matchTimeoutMilliseconds: 200)]
    private static partial Regex ApiKeyPattern();

    [GeneratedRegex(@"(?i)bearer\s+[A-Za-z0-9_\-\.]+", RegexOptions.None, matchTimeoutMilliseconds: 200)]
    private static partial Regex BearerPattern();

    [GeneratedRegex(@"(?i)authorization:\s*\S+", RegexOptions.None, matchTimeoutMilliseconds: 200)]
    private static partial Regex AuthorizationHeaderPattern();

    [GeneratedRegex(@"(?i)openai-safety-identifier:\s*\S+", RegexOptions.None, matchTimeoutMilliseconds: 200)]
    private static partial Regex SafetyIdentifierPattern();

    [GeneratedRegex(
        @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
        RegexOptions.None,
        matchTimeoutMilliseconds: 200)]
    private static partial Regex UuidPattern();
}
