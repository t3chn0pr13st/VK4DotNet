using VK4DotNet.LegacyAuth;

namespace VK4DotNet.Tests;

public sealed class LegacyAuthTests
{
    [Fact]
    public async Task Success_returns_token_without_exposing_credentials()
    {
        var handler = new QueueHttpMessageHandler();
        handler.EnqueueJson("""{"access_token":"token","expires_in":3600,"user_id":7}""");
        using var auth = CreateAuth(handler);

        var result = await auth.AuthenticateAsync(new LegacyVkAuthRequest { Username = "user", Password = "password" });

        var success = Assert.IsType<LegacyVkAuthSuccess>(result);
        Assert.Equal("token", success.AccessToken.Value);
        Assert.Equal(7, success.AccessToken.UserId);
        Assert.Equal("VK4DotNet.Test/1.0", handler.Requests[0].UserAgent);
        Assert.Contains("scope=messages%2Cphotos%2Cwall%2Cgroups%2Coffline", handler.Requests[0].Body);
    }

    [Fact]
    public void Diagnostic_strings_redact_password_codes_and_client_secret()
    {
        var options = new LegacyVkAuthOptions
        {
            ClientId = 123,
            ClientSecret = "own-secret",
            ClientName = "test",
            UserAgent = "VK4DotNet.Test/1.1"
        };
        var request = new LegacyVkAuthRequest
        {
            Username = "user@example.test",
            Password = "p@ssw0rd",
            TwoFactorCode = "123456",
            CaptchaKey = "captcha-answer"
        };

        Assert.DoesNotContain("own-secret", options.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("user@example.test", request.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("p@ssw0rd", request.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("123456", request.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("captcha-answer", request.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Captcha_is_returned_as_challenge()
    {
        var handler = new QueueHttpMessageHandler();
        handler.EnqueueJson("""{"error":"need_captcha","error_description":"captcha","captcha_sid":"sid","captcha_img":"https://img/captcha"}""");
        using var auth = CreateAuth(handler);

        var result = await auth.AuthenticateAsync(new LegacyVkAuthRequest { Username = "user", Password = "password" });

        var challenge = Assert.IsType<LegacyVkAuthChallenge>(result);
        Assert.Equal(LegacyVkChallengeKind.Captcha, challenge.Kind);
        Assert.Equal("sid", challenge.Sid);
    }

    [Theory]
    [InlineData("2fa_app", LegacyVkChallengeKind.TwoFactor)]
    [InlineData("2fa_sms", LegacyVkChallengeKind.SmsValidation)]
    public async Task Validation_is_returned_as_typed_challenge(string validationType, LegacyVkChallengeKind expected)
    {
        var handler = new QueueHttpMessageHandler();
        handler.EnqueueJson($$"""{"error":"need_validation","error_description":"verify","validation_type":"{{validationType}}","validation_sid":"sid","delay":120}""");
        using var auth = CreateAuth(handler);

        var result = await auth.AuthenticateAsync(new LegacyVkAuthRequest { Username = "user", Password = "password" });

        var challenge = Assert.IsType<LegacyVkAuthChallenge>(result);
        Assert.Equal(expected, challenge.Kind);
        Assert.Equal(TimeSpan.FromSeconds(120), challenge.RetryAfter);
    }

    private static LegacyVkPasswordAuthenticator CreateAuth(QueueHttpMessageHandler handler) => new(
        new LegacyVkAuthOptions
        {
            ClientId = 123,
            ClientSecret = "own-secret",
            ClientName = "test",
            UserAgent = "VK4DotNet.Test/1.0"
        },
        new HttpClient(handler));
}
