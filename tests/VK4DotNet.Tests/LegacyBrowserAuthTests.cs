using VK4DotNet.LegacyAuth;

namespace VK4DotNet.Tests;

public sealed class LegacyBrowserAuthTests
{
    [Fact]
    public void Session_uses_hosted_authorization_without_exposing_secret()
    {
        using var auth = CreateAuth(new QueueHttpMessageHandler());

        var first = auth.CreateAuthorizationSession();
        var second = auth.CreateAuthorizationSession();
        var parameters = ParseParameters(first.AuthorizationUri.Query);

        Assert.Equal("https", first.AuthorizationUri.Scheme);
        Assert.Equal("oauth.vk.com", first.AuthorizationUri.Host);
        Assert.Equal("/authorize", first.AuthorizationUri.AbsolutePath);
        Assert.Equal("123", parameters["client_id"]);
        Assert.Equal("https://app.example/callback", parameters["redirect_uri"]);
        Assert.Equal("339972", parameters["scope"]);
        Assert.Equal("code", parameters["response_type"]);
        Assert.Equal("5.199", parameters["v"]);
        Assert.Equal("1", parameters["revoke"]);
        Assert.Equal(first.State, parameters["state"]);
        Assert.NotEqual(first.State, second.State);
        Assert.DoesNotContain("own-secret", first.AuthorizationUri.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(first.State, first.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Code_callback_is_exchanged_after_state_validation()
    {
        var handler = new QueueHttpMessageHandler();
        handler.EnqueueJson("""{"access_token":"user-token","expires_in":3600,"user_id":7}""");
        using var auth = CreateAuth(handler);
        var session = auth.CreateAuthorizationSession();
        var callback = Callback(session, $"code=authorization-code&state={Uri.EscapeDataString(session.State)}");

        var token = await auth.CompleteAsync(callback, session);

        Assert.Equal("user-token", token.Value);
        Assert.Equal(7, token.UserId);
        Assert.NotNull(token.ExpiresAt);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(new Uri("https://oauth.vk.com/access_token"), request.Uri);
        Assert.Contains("client_id=123", request.Body, StringComparison.Ordinal);
        Assert.Contains("client_secret=own-secret", request.Body, StringComparison.Ordinal);
        Assert.Contains("code=authorization-code", request.Body, StringComparison.Ordinal);
        Assert.Contains("redirect_uri=https%3A%2F%2Fapp.example%2Fcallback", request.Body, StringComparison.Ordinal);
        Assert.Equal("VK4DotNet.Test/1.1", request.UserAgent);
    }

    [Fact]
    public async Task Token_fragment_is_completed_without_network_or_client_secret()
    {
        var handler = new QueueHttpMessageHandler();
        using var auth = CreateAuth(handler, LegacyVkBrowserFlow.AccessToken, clientSecret: null);
        var session = auth.CreateAuthorizationSession();
        var callback = Callback(
            session,
            $"access_token=fragment-token&expires_in=0&user_id=9&state={Uri.EscapeDataString(session.State)}",
            fragment: true);

        var token = await auth.CompleteAsync(callback, session);

        Assert.Equal("fragment-token", token.Value);
        Assert.Equal(9, token.UserId);
        Assert.Null(token.ExpiresAt);
        Assert.Empty(handler.Requests);
        Assert.Equal("token", ParseParameters(session.AuthorizationUri.Query)["response_type"]);
    }

    [Fact]
    public async Task State_mismatch_stops_before_code_exchange()
    {
        var handler = new QueueHttpMessageHandler();
        using var auth = CreateAuth(handler);
        var session = auth.CreateAuthorizationSession();
        var callback = Callback(session, "code=authorization-code&state=wrong-state");

        var exception = await Assert.ThrowsAsync<VkOAuthException>(() => auth.CompleteAsync(callback, session));

        Assert.Equal("state_mismatch", exception.Error);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Callback_must_use_the_registered_redirect_location()
    {
        var handler = new QueueHttpMessageHandler();
        using var auth = CreateAuth(handler);
        var session = auth.CreateAuthorizationSession();
        var callback = new Uri($"https://attacker.example/callback?code=code&state={Uri.EscapeDataString(session.State)}");

        var exception = await Assert.ThrowsAsync<VkOAuthException>(() => auth.CompleteAsync(callback, session));

        Assert.Equal("invalid_callback", exception.Error);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task OAuth_denial_is_typed_and_does_not_call_token_endpoint()
    {
        var handler = new QueueHttpMessageHandler();
        using var auth = CreateAuth(handler);
        var session = auth.CreateAuthorizationSession();
        var callback = Callback(
            session,
            $"error=access_denied&error_description=Denied&state={Uri.EscapeDataString(session.State)}");

        var exception = await Assert.ThrowsAsync<VkOAuthException>(() => auth.CompleteAsync(callback, session));

        Assert.Equal("access_denied", exception.Error);
        Assert.Equal("Denied", exception.Description);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Cancellation_propagates_during_code_exchange()
    {
        var handler = new QueueHttpMessageHandler();
        handler.Enqueue(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable");
        });
        using var auth = CreateAuth(handler);
        var session = auth.CreateAuthorizationSession();
        var callback = Callback(session, $"code=code&state={Uri.EscapeDataString(session.State)}");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => auth.CompleteAsync(callback, session, cancellation.Token));
    }

    [Fact]
    public async Task Token_endpoint_errors_redact_secret_and_authorization_code()
    {
        var handler = new QueueHttpMessageHandler();
        handler.EnqueueJson(
            """{"error":"invalid_client","error_description":"Rejected own-secret with authorization-code"}""",
            System.Net.HttpStatusCode.Unauthorized);
        using var auth = CreateAuth(handler);
        var session = auth.CreateAuthorizationSession();
        var callback = Callback(session, $"code=authorization-code&state={Uri.EscapeDataString(session.State)}");

        var exception = await Assert.ThrowsAsync<VkOAuthException>(() => auth.CompleteAsync(callback, session));

        Assert.Equal("invalid_client", exception.Error);
        Assert.DoesNotContain("own-secret", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("authorization-code", exception.ToString(), StringComparison.Ordinal);
        Assert.Contains("<redacted>", exception.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Code_flow_requires_own_client_secret()
    {
        var options = CreateOptions(LegacyVkBrowserFlow.AuthorizationCode, clientSecret: null);

        var exception = Assert.Throws<VkValidationException>(() => new LegacyVkBrowserAuthenticator(options));

        Assert.Contains("client secret", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unknown_scope_is_rejected_before_browser_session_is_created()
    {
        var options = new LegacyVkBrowserAuthOptions
        {
            ClientId = 123,
            ClientSecret = "own-secret",
            RedirectUri = new Uri("https://app.example/callback"),
            UserAgent = "VK4DotNet.Test/1.1",
            Scopes = ["messages", "unknown"]
        };

        var exception = Assert.Throws<VkValidationException>(() => new LegacyVkBrowserAuthenticator(options));

        Assert.Contains("unknown", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static LegacyVkBrowserAuthenticator CreateAuth(
        QueueHttpMessageHandler handler,
        LegacyVkBrowserFlow flow = LegacyVkBrowserFlow.AuthorizationCode,
        string? clientSecret = "own-secret") =>
        new(CreateOptions(flow, clientSecret), new HttpClient(handler));

    private static LegacyVkBrowserAuthOptions CreateOptions(LegacyVkBrowserFlow flow, string? clientSecret) => new()
    {
        ClientId = 123,
        ClientSecret = clientSecret,
        RedirectUri = new Uri("https://app.example/callback"),
        UserAgent = "VK4DotNet.Test/1.1",
        Flow = flow
    };

    private static Uri Callback(LegacyVkBrowserAuthorizationSession session, string parameters, bool fragment = false) =>
        new($"{session.RedirectUri}{(fragment ? '#' : '?')}{parameters}");

    private static Dictionary<string, string> ParseParameters(string source) => source
        .TrimStart('?', '#')
        .Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(part => part.Split('=', 2))
        .ToDictionary(
            part => Uri.UnescapeDataString(part[0].Replace('+', ' ')),
            part => Uri.UnescapeDataString(part.ElementAtOrDefault(1)?.Replace('+', ' ') ?? string.Empty),
            StringComparer.Ordinal);
}
