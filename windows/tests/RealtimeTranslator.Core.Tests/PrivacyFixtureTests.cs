using RealtimeTranslator.Core.OpenAI;
using Xunit;

namespace RealtimeTranslator.Core.Tests;

public sealed class PrivacyFixtureTests
{
    public static TheoryData<string> SanitizeCases => SharedFixtures.CaseNames("privacy", "sanitizedServerMessage");

    public static TheoryData<string> AuthenticationCases =>
        SharedFixtures.CaseNames("privacy", "isAuthenticationFailure");

    // Given: shared fixture の汎用サーバーエラー文言
    // When: 実装定数と照合する
    // Then: 利用者へ見せる文言が完全に一致する
    [Fact]
    public void GenericMessageMatchesFixture()
    {
        Assert.Equal(
            SharedFixtures.Text(SharedFixtures.Load("privacy")["genericErrorMessage"]),
            RealtimeTranslationException.GenericServerMessage);
    }

    // Given: 資格情報や内部情報を含みうるサーバーメッセージ
    // When: プライバシー安全な正規化を行う
    // Then: fixture が許容する文言だけが残る
    [Theory]
    [MemberData(nameof(SanitizeCases))]
    public void SanitizeMatchesFixture(string name)
    {
        var fixture = SharedFixtures.Case("privacy", "sanitizedServerMessage", name);

        Assert.Equal(
            SharedFixtures.Text(fixture["expected"]),
            RealtimeTranslationException.SanitizeServerMessage(SharedFixtures.Text(fixture["input"])));
    }

    // Given: fixture の認証失敗・非認証エラー
    // When: 認証失敗判定を行う
    // Then: 期待どおりに認証失敗だけを検出する
    [Theory]
    [MemberData(nameof(AuthenticationCases))]
    public void AuthenticationDetectionMatchesFixture(string name)
    {
        var fixture = SharedFixtures.Case("privacy", "isAuthenticationFailure", name);

        Assert.Equal(
            SharedFixtures.Flag(fixture["expected"]),
            RealtimeTranslationException.IsAuthenticationFailure(
                SharedFixtures.OptionalText(fixture["code"]),
                SharedFixtures.Text(fixture["message"])));
    }

    // Given: fixture のエラー種別と回復可否対応表
    // When: 各エラーの回復可否を求める
    // Then: 再接続対象と致命エラーの区別が一致する
    [Fact]
    public void RecoverabilityMatchesFixture()
    {
        foreach (var item in SharedFixtures.Section("privacy", "recoverability"))
        {
            var fixture = item!.AsObject();
            var kind = ParseKind(SharedFixtures.Text(fixture["error"]));

            Assert.Equal(
                SharedFixtures.Flag(fixture["isRecoverable"]),
                new RealtimeTranslationException(kind).IsRecoverable);
        }
    }

    /// <summary>server message を持つ例外でも、生の資格情報が Message に載らないこと。</summary>
    // Given: API キーらしき文字列を含む致命的サーバーエラー
    // When: 例外メッセージを取得する
    // Then: 汎用文言に置換され資格情報が表に出ない
    [Fact]
    public void FatalServerErrorNeverLeaksCredentials()
    {
        var error = new RealtimeTranslationException(
            RealtimeTranslationErrorKind.FatalServerError,
            "Bearer sk-should-never-surface");

        Assert.Equal(RealtimeTranslationException.GenericServerMessage, error.Message);
    }

    private static RealtimeTranslationErrorKind ParseKind(string value) => value switch
    {
        "missingAPIKey" => RealtimeTranslationErrorKind.MissingApiKey,
        "notConnected" => RealtimeTranslationErrorKind.NotConnected,
        "invalidMessage" => RealtimeTranslationErrorKind.InvalidMessage,
        "authenticationFailed" => RealtimeTranslationErrorKind.AuthenticationFailed,
        "fatalServerError" => RealtimeTranslationErrorKind.FatalServerError,
        "recoverableTransportFailure" => RealtimeTranslationErrorKind.RecoverableTransportFailure,
        "sessionUpdateTimeout" => RealtimeTranslationErrorKind.SessionUpdateTimeout,
        "closeTimeout" => RealtimeTranslationErrorKind.CloseTimeout,
        "cancelled" => RealtimeTranslationErrorKind.Cancelled,
        _ => throw new Xunit.Sdk.XunitException("unhandled error kind " + value),
    };
}
