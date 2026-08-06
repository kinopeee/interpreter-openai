using System;
using System.Security.Cryptography;
using System.Text;

namespace RealtimeTranslator.Core.Security;

/// <summary>非 PII の安定識別子。初回生成した install ID そのものではなく SHA-256 の hex を送る。</summary>
public static class OpenAISafetyIdentifier
{
    public static string HashedValue(string installIdentifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installIdentifier);

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(installIdentifier));
        return Convert.ToHexStringLower(digest);
    }
}
