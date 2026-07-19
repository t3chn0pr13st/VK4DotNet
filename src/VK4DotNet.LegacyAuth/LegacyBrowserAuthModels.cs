namespace VK4DotNet.LegacyAuth;

/// <summary>Selects the legacy browser response returned by VK.</summary>
public enum LegacyVkBrowserFlow
{
    /// <summary>Return an authorization code and exchange it with the application's client secret.</summary>
    AuthorizationCode,

    /// <summary>Return an access token in the redirect URI fragment. Intended only for legacy native clients.</summary>
    AccessToken
}

/// <summary>Configures hosted legacy OAuth authorization at <c>oauth.vk.com</c>.</summary>
public sealed class LegacyVkBrowserAuthOptions
{
    public required long ClientId { get; init; }
    public string? ClientSecret { get; init; }
    public required Uri RedirectUri { get; init; }
    public required string UserAgent { get; init; }
    public LegacyVkBrowserFlow Flow { get; init; } = LegacyVkBrowserFlow.AuthorizationCode;
    public Uri AuthorizationEndpoint { get; init; } = new("https://oauth.vk.com/authorize");
    public Uri TokenEndpoint { get; init; } = new("https://oauth.vk.com/access_token");
    public IReadOnlyList<string> Scopes { get; init; } = ["messages", "photos", "wall", "groups", "offline"];
    public string ApiVersion { get; init; } = "5.199";
    public string Display { get; init; } = "page";
    public bool RevokeExistingGrant { get; init; } = true;

    internal void Validate()
    {
        if (ClientId <= 0)
        {
            throw new VkValidationException("A positive VK application client ID is required for legacy browser auth.");
        }

        if (Flow == LegacyVkBrowserFlow.AuthorizationCode && string.IsNullOrWhiteSpace(ClientSecret))
        {
            throw new VkValidationException("Legacy authorization-code flow requires the client secret for your own VK application.");
        }

        if (RedirectUri is null || !RedirectUri.IsAbsoluteUri || !string.IsNullOrEmpty(RedirectUri.Fragment))
        {
            throw new VkValidationException("Legacy browser auth requires an absolute redirect URI without a fragment.");
        }

        ValidateEndpoint(AuthorizationEndpoint, "authorization");
        ValidateEndpoint(TokenEndpoint, "token");

        if (string.IsNullOrWhiteSpace(UserAgent))
        {
            throw new VkValidationException("Legacy browser auth requires the consuming application's User-Agent.");
        }

        if (Scopes is null || Scopes.Count == 0 || Scopes.Any(string.IsNullOrWhiteSpace))
        {
            throw new VkValidationException("At least one valid legacy scope is required.");
        }

        if (string.IsNullOrWhiteSpace(ApiVersion) || string.IsNullOrWhiteSpace(Display))
        {
            throw new VkValidationException("Legacy browser auth requires an API version and display mode.");
        }
    }

    private static void ValidateEndpoint(Uri? endpoint, string name)
    {
        if (endpoint is null || !endpoint.IsAbsoluteUri || endpoint.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment))
        {
            throw new VkValidationException($"The legacy {name} endpoint must be an absolute HTTPS URI without query or fragment components.");
        }
    }
}

/// <summary>Ephemeral state for one hosted legacy authorization attempt.</summary>
public sealed class LegacyVkBrowserAuthorizationSession
{
    internal LegacyVkBrowserAuthorizationSession(
        Uri authorizationUri,
        string state,
        Uri redirectUri,
        LegacyVkBrowserFlow flow,
        IReadOnlyList<string> scopes)
    {
        AuthorizationUri = authorizationUri;
        State = state;
        RedirectUri = redirectUri;
        Flow = flow;
        Scopes = Array.AsReadOnly(scopes.ToArray());
    }

    public Uri AuthorizationUri { get; }
    public string State { get; }
    public Uri RedirectUri { get; }
    public LegacyVkBrowserFlow Flow { get; }
    public IReadOnlyList<string> Scopes { get; }

    public override string ToString() =>
        $"{nameof(LegacyVkBrowserAuthorizationSession)} {{ AuthorizationUri = {AuthorizationUri.GetLeftPart(UriPartial.Path)}, State = <redacted>, Flow = {Flow} }}";
}
