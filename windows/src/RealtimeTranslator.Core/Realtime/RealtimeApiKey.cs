using RealtimeTranslator.Core.OpenAI;
using RealtimeTranslator.Core.Security;

namespace RealtimeTranslator.Core.Realtime;

/// <summary>接続直前のキー正規化。空は欠落、形式不正は認証失敗として送らない。</summary>
internal static class RealtimeApiKey
{
    public static string Require(string? apiKey)
    {
        var result = ApiKeyNormalizer.Normalize(apiKey);
        if (result.Status == ApiKeyNormalizationStatus.Valid && result.Value is { } value)
        {
            return value;
        }

        throw result.Status == ApiKeyNormalizationStatus.Malformed
            ? new RealtimeTranslationException(RealtimeTranslationErrorKind.AuthenticationFailed)
            : new RealtimeTranslationException(RealtimeTranslationErrorKind.MissingApiKey);
    }
}
