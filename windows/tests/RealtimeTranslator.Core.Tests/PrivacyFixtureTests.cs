using RealtimeTranslator.Core.OpenAI;
using Xunit;

namespace RealtimeTranslator.Core.Tests;

public sealed class PrivacyFixtureTests
{
    public static TheoryData<string> SanitizeCases => SharedFixtures.CaseNames("privacy", "sanitizedServerMessage");

    public static TheoryData<string> AuthenticationCases =>
        SharedFixtures.CaseNames("privacy", "isAuthenticationFailure");

    [Fact]
    public void GenericMessageMatchesFixture()
    {
        Assert.Equal(
            SharedFixtures.Text(SharedFixtures.Load("privacy")["genericErrorMessage"]),
            RealtimeTranslationException.GenericServerMessage);
    }

    [Theory]
    [MemberData(nameof(SanitizeCases))]
    public void SanitizeMatchesFixture(string name)
    {
        var fixture = SharedFixtures.Case("privacy", "sanitizedServerMessage", name);

        Assert.Equal(
            SharedFixtures.Text(fixture["expected"]),
            RealtimeTranslationException.SanitizeServerMessage(SharedFixtures.Text(fixture["input"])));
    }

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
