using System.Security.Cryptography;
using System.Text;
using VK4DotNet.Auth;

namespace VK4DotNet.Tests;

public sealed class VkIdAuthTests
{
    [Fact]
    public void Authorization_session_uses_random_state_and_s256_pkce()
    {
        using var auth = CreateAuth(new QueueHttpMessageHandler());

        var session = auth.CreateAuthorizationSession();
        var query = ParseQuery(session.AuthorizationUri.Query);
        var expectedChallenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(session.CodeVerifier)));

        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal(expectedChallenge, query["code_challenge"]);
        Assert.Equal(session.State, query["state"]);
        Assert.Contains("wall", query["scope"]);
        Assert.DoesNotContain("messages", query["scope"]);
    }

    [Fact]
    public async Task State_mismatch_stops_before_token_exchange()
    {
        var handler = new QueueHttpMessageHandler();
        using var auth = CreateAuth(handler);
        var session = auth.CreateAuthorizationSession();

        var error = await Assert.ThrowsAsync<VkOAuthException>(() => auth.ExchangeCodeAsync(
            new Uri("https://app.test/callback?code=x&device_id=d&state=wrong"), session));

        Assert.Equal("state_mismatch", error.Error);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Code_exchange_and_refresh_preserve_device_and_tokens()
    {
        var handler = new QueueHttpMessageHandler();
        handler.EnqueueJson("""{"access_token":"access","refresh_token":"refresh","id_token":"id","expires_in":3600,"user_id":7}""");
        handler.EnqueueJson("""{"access_token":"new-access","refresh_token":"new-refresh","expires_in":3600,"user_id":7}""");
        using var auth = CreateAuth(handler);
        var session = auth.CreateAuthorizationSession();

        var tokens = await auth.ExchangeCodeAsync(
            new Uri($"https://app.test/callback?code=code&device_id=device&state={session.State}"), session);
        var refreshed = await auth.RefreshAsync(tokens);

        Assert.Equal("access", tokens.AccessToken);
        Assert.Equal("device", tokens.DeviceId);
        Assert.Equal("new-access", refreshed.AccessToken);
        Assert.Contains("code_verifier=", handler.Requests[0].Body);
        Assert.Contains("grant_type=refresh_token", Uri.UnescapeDataString(handler.Requests[1].Body));
    }

    [Fact]
    public async Task Token_provider_refreshes_expired_token_and_notifies_host()
    {
        var handler = new QueueHttpMessageHandler();
        handler.EnqueueJson("""{"access_token":"new","refresh_token":"new-refresh","expires_in":3600}""");
        using var auth = CreateAuth(handler);
        VkIdTokenSet? changed = null;
        using var provider = new VkIdTokenProvider(
            auth,
            new VkIdTokenSet("old", "refresh", null, "device", null, DateTimeOffset.UtcNow.AddMinutes(-1), ["wall"]),
            (tokens, _) => { changed = tokens; return ValueTask.CompletedTask; });

        var token = await provider.GetTokenAsync();

        Assert.Equal("new", token.Value);
        Assert.Equal("new", changed!.AccessToken);
    }

    private static VkIdAuthClient CreateAuth(QueueHttpMessageHandler handler) => new(
        new VkIdAuthOptions
        {
            ClientId = 123,
            RedirectUri = new Uri("https://app.test/callback")
        },
        new HttpClient(handler));

    private static Dictionary<string, string> ParseQuery(string query) => query.TrimStart('?').Split('&')
        .Select(part => part.Split('=', 2))
        .ToDictionary(parts => Uri.UnescapeDataString(parts[0]), parts => Uri.UnescapeDataString(parts[1]));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
