namespace VK4DotNet.Auth;

public sealed record VkIdAuthOptions
{
    public required long ClientId { get; init; }
    public required Uri RedirectUri { get; init; }
    public Uri BaseUri { get; init; } = new("https://id.vk.com/");
    public IReadOnlyList<string> Scopes { get; init; } =
        ["vkid.personal_info", "wall", "photos", "groups"];

    internal void Validate()
    {
        if (ClientId <= 0)
        {
            throw new VkValidationException("A positive VK ID client ID is required.");
        }

        if (!RedirectUri.IsAbsoluteUri)
        {
            throw new VkValidationException("An absolute VK ID redirect URI is required.");
        }

        if (!BaseUri.IsAbsoluteUri || BaseUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new VkValidationException("The VK ID base URI must be an absolute HTTPS URI.");
        }

        if (Scopes.Count == 0 || Scopes.Any(string.IsNullOrWhiteSpace))
        {
            throw new VkValidationException("At least one valid VK ID scope is required.");
        }
    }
}

public sealed record VkIdAuthorizationRequest
{
    public IReadOnlyList<string>? Scopes { get; init; }
    public string? Prompt { get; init; }
    public string Provider { get; init; } = "vkid";
    public int LanguageId { get; init; }
    public string ColorScheme { get; init; } = "light";
}

public sealed record VkIdAuthorizationSession(
    Uri AuthorizationUri,
    string CodeVerifier,
    string State,
    Uri RedirectUri,
    IReadOnlyList<string> Scopes);

public sealed record VkIdTokenSet(
    string AccessToken,
    string RefreshToken,
    string? IdToken,
    string DeviceId,
    long? UserId,
    DateTimeOffset? ExpiresAt,
    IReadOnlyList<string> Scopes)
{
    public VkAccessToken ToAccessToken() => new(AccessToken, ExpiresAt, UserId);
}
