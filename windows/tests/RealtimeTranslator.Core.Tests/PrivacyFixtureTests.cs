using System;
using RealtimeTranslator.Core.Localization;
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
        // Given: privacy fixture の汎用メッセージ
        // When/Then: GenericServerMessage が一致する
        Assert.Equal(
            SharedFixtures.Text(SharedFixtures.Load("privacy")["genericErrorMessage"]),
            RealtimeTranslationException.GenericServerMessage);
    }

    // Given: ui.json の error.genericServer ja
    // When: privacy fixture の genericErrorMessage と照合する
    // Then: fixtures/v1 を変えずにカタログ ja が一致する
    [Fact]
    public void CatalogJapaneseGenericServerMatchesFixture()
    {
        var ja = UserCopy.Parse(SharedFixtures.UiCatalogJson, UiLocale.Ja);
        Assert.Equal(
            SharedFixtures.Text(SharedFixtures.Load("privacy")["genericErrorMessage"]),
            ja.Text("error.genericServer"));
    }

    // Given: 資格情報や内部情報を含みうるサーバーメッセージ
    // When: プライバシー安全な正規化を行う
    // Then: fixture が許容する文言だけが残る
    [Theory]
    [MemberData(nameof(SanitizeCases))]
    public void SanitizeMatchesFixture(string name)
    {
        // Given: サーバー文言 sanitize fixture
        var fixture = SharedFixtures.Case("privacy", "sanitizedServerMessage", name);

        // When/Then: 資格情報を含む文言は汎用メッセージへ落ちる
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
        // Given: 認証失敗判定 fixture
        var fixture = SharedFixtures.Case("privacy", "isAuthenticationFailure", name);

        // When/Then: code/message から認証失敗を判定する
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
        // Given: エラー種別ごとの recoverability 表
        foreach (var item in SharedFixtures.Section("privacy", "recoverability"))
        {
            var fixture = item!.AsObject();
            var kind = ParseKind(SharedFixtures.Text(fixture["error"]));

            // When/Then: IsRecoverable が一致する
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
        // Given: 資格情報を含む server message
        // When: FatalServerError を作る
        var error = new RealtimeTranslationException(
            RealtimeTranslationErrorKind.FatalServerError,
            "Bearer sk-should-never-surface");

        // Then: 表示用 Message は汎用文言になる
        Assert.Equal(RealtimeTranslationException.GenericServerMessage, error.Message);
        Assert.Equal(RealtimeTranslationException.GenericServerMessage, error.ServerMessage);
        Assert.DoesNotContain("sk-", error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Bearer", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    // Given: 認証失敗例外に生のキー断片を渡す
    // When: ServerMessage / ToString を取る
    // Then: 生文言は保持されず、表示にも出ない
    [Fact]
    public void NonFatalExceptionDoesNotRetainRawServerMessage()
    {
        var error = new RealtimeTranslationException(
            RealtimeTranslationErrorKind.AuthenticationFailed,
            "Incorrect API key provided: sk-should-never-surface");

        Assert.Null(error.ServerMessage);
        Assert.DoesNotContain("sk-", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sk-", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    // Given: Format 文字や Unicode 空白で伏せたキー断片・api key 文言
    // When: Sanitize する
    // Then: 汎用文言へ落ち、原文のキー断片を返さない
    [Fact]
    public void SanitizeRedactsUnicodeObfuscatedKeyMaterial()
    {
        Assert.Equal(
            RealtimeTranslationException.GenericServerMessage,
            RealtimeTranslationException.SanitizeServerMessage("invalid key s\u200bk-abcdef"));
        Assert.Equal(
            RealtimeTranslationException.GenericServerMessage,
            RealtimeTranslationException.SanitizeServerMessage("Incorrect API\u00a0key provided"));
        Assert.Equal(
            RealtimeTranslationException.GenericServerMessage,
            RealtimeTranslationException.SanitizeServerMessage("Missing bearer\u00a0or basic authentication"));
        Assert.Equal(
            RealtimeTranslationException.GenericServerMessage,
            RealtimeTranslationException.SanitizeServerMessage("invalid key s\tk-abcdef"));
        Assert.Equal(
            RealtimeTranslationException.GenericServerMessage,
            RealtimeTranslationException.SanitizeServerMessage("Incorrect API\tkey provided"));
        Assert.Equal(
            RealtimeTranslationException.GenericServerMessage,
            RealtimeTranslationException.SanitizeServerMessage("Bearer\tabc123 is not valid"));
        Assert.Equal("bearerless request", RealtimeTranslationException.SanitizeServerMessage("bearerless request"));
    }

    // Given: ZWSP を挟んだ認証失敗フレーズ
    // When: 認証失敗判定する
    // Then: authority / 4010 は誤爆せず、api key フレーズは検出する
    [Fact]
    public void AuthenticationDetectionSurvivesUnicodeObfuscationWithoutFalsePositives()
    {
        Assert.True(
            RealtimeTranslationException.IsAuthenticationFailure(
                null,
                "Incorrect API\u00a0key provided"));
        Assert.True(
            RealtimeTranslationException.IsAuthenticationFailure(
                "invalid_api\u200b_key",
                string.Empty));
        Assert.True(
            RealtimeTranslationException.IsAuthenticationFailure(
                "invalid_api\t_key",
                string.Empty));
        Assert.False(
            RealtimeTranslationException.IsAuthenticationFailure(
                "authority_error",
                "authority mismatch"));
        Assert.False(
            RealtimeTranslationException.IsAuthenticationFailure(
                null,
                "error 4010 occurred"));
        Assert.False(
            RealtimeTranslationException.IsAuthenticationFailure(
                null,
                "error 4\t01 occurred"));
    }

    // Given: 各エラー種別
    // When: 例外メッセージを取る
    // Then: Current（ja）カタログの対応キーと一致し、未知 kind だけ generic へ倒す
    [Theory]
    [InlineData(RealtimeTranslationErrorKind.MissingApiKey, "error.missingApiKey")]
    [InlineData(RealtimeTranslationErrorKind.NotConnected, "error.notConnected")]
    [InlineData(RealtimeTranslationErrorKind.InvalidMessage, "error.invalidMessage")]
    [InlineData(RealtimeTranslationErrorKind.AuthenticationFailed, "error.authenticationFailed")]
    [InlineData(RealtimeTranslationErrorKind.RecoverableTransportFailure, "error.transportDisconnected")]
    [InlineData(RealtimeTranslationErrorKind.SessionUpdateTimeout, "error.sessionUpdateTimeout")]
    [InlineData(RealtimeTranslationErrorKind.CloseTimeout, "error.closeTimeout")]
    [InlineData(RealtimeTranslationErrorKind.Cancelled, "error.cancelled")]
    public void ErrorKindMessageMatchesJapaneseCatalog(RealtimeTranslationErrorKind kind, string key)
    {
        var ja = UserCopy.Parse(SharedFixtures.UiCatalogJson, UiLocale.Ja);
        var en = UserCopy.Parse(SharedFixtures.UiCatalogJson, UiLocale.En);

        Assert.Equal(ja.Text(key), new RealtimeTranslationException(kind).Message);
        Assert.NotEqual(ja.Text(key), en.Text(key));
        Assert.DoesNotContain("sk-", en.Text(key), StringComparison.OrdinalIgnoreCase);
    }

    // Given: 空のサーバー文言、または英語カタログの "API key" を含む認証・欠落キー文言
    // When: Sanitize する / 英語 copy で例外を組み立てる
    // Then: 空は generic。例外経路はカタログ文言のまま（再サニタイズしない）
    [Fact]
    public void CatalogErrorCopyIsNotReSanitized()
    {
        Assert.Equal(
            RealtimeTranslationException.GenericServerMessage,
            RealtimeTranslationException.SanitizeServerMessage(string.Empty));
        Assert.Equal(
            RealtimeTranslationException.GenericServerMessage,
            new RealtimeTranslationException(RealtimeTranslationErrorKind.FatalServerError, string.Empty).Message);

        var en = UserCopy.Parse(SharedFixtures.UiCatalogJson, UiLocale.En);
        (RealtimeTranslationErrorKind Kind, string Key)[] cases =
        [
            (RealtimeTranslationErrorKind.AuthenticationFailed, "error.authenticationFailed"),
            (RealtimeTranslationErrorKind.MissingApiKey, "error.missingApiKey"),
        ];
        foreach (var (kind, key) in cases)
        {
            var catalogText = en.Text(key);
            Assert.Contains("API key", catalogText, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(
                RealtimeTranslationException.GenericServerMessage,
                RealtimeTranslationException.SanitizeServerMessage(catalogText));
            Assert.Equal(
                catalogText,
                new RealtimeTranslationException(kind, serverMessage: null, en).Message);
        }
    }

    private static RealtimeTranslationErrorKind ParseKind(string value) => value switch
    {
        "missingAPIKey" => RealtimeTranslationErrorKind.MissingApiKey,
        "notConnected" => RealtimeTranslationErrorKind.NotConnected,
        "invalidMessage" => RealtimeTranslationErrorKind.InvalidMessage,
        "authenticationFailed" => RealtimeTranslationErrorKind.AuthenticationFailed,
         "fatalServerError" => RealtimeTranslationErrorKind.FatalServerError,
         "recoverableTransportFailure" => RealtimeTranslationErrorKind.RecoverableTransportFailure,
         "receiveOverflow" => RealtimeTranslationErrorKind.ReceiveOverflow,
        "sessionUpdateTimeout" => RealtimeTranslationErrorKind.SessionUpdateTimeout,
        "closeTimeout" => RealtimeTranslationErrorKind.CloseTimeout,
        "cancelled" => RealtimeTranslationErrorKind.Cancelled,
        _ => throw new Xunit.Sdk.XunitException("unhandled error kind " + value),
    };
}
