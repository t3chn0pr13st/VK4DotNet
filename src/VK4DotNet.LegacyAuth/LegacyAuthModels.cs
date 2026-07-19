namespace VK4DotNet.LegacyAuth;

public sealed record LegacyVkAuthOptions
{
    public required long ClientId { get; init; }
    public required string ClientSecret { get; init; }
    public required string ClientName { get; init; }
    public required string UserAgent { get; init; }
    public Uri TokenEndpoint { get; init; } = new("https://oauth.vk.com/token");
    public IReadOnlyList<string> Scopes { get; init; } = ["messages", "photos", "wall", "groups", "offline"];

    internal void Validate()
    {
        if (ClientId <= 0 || string.IsNullOrWhiteSpace(ClientSecret)
            || string.IsNullOrWhiteSpace(ClientName) || string.IsNullOrWhiteSpace(UserAgent))
        {
            throw new VkValidationException("Legacy auth requires client ID, client secret, client name, and User-Agent.");
        }

        if (!TokenEndpoint.IsAbsoluteUri || TokenEndpoint.Scheme != Uri.UriSchemeHttps)
        {
            throw new VkValidationException("The legacy token endpoint must be an absolute HTTPS URI.");
        }

        if (Scopes.Count == 0 || Scopes.Any(string.IsNullOrWhiteSpace))
        {
            throw new VkValidationException("At least one valid legacy scope is required.");
        }
    }

    public override string ToString() =>
        $"{nameof(LegacyVkAuthOptions)} {{ ClientId = {ClientId}, ClientSecret = <redacted>, ClientName = {ClientName}, UserAgent = {UserAgent} }}";
}

public sealed record LegacyVkAuthRequest
{
    public required string Username { get; init; }
    public required string Password { get; init; }
    public string? TwoFactorCode { get; init; }
    public string? CaptchaSid { get; init; }
    public string? CaptchaKey { get; init; }
    public bool ForceSms { get; init; }

    public override string ToString() =>
        $"{nameof(LegacyVkAuthRequest)} {{ Username = <redacted>, Password = <redacted>, TwoFactorCode = <redacted>, CaptchaSid = {CaptchaSid}, CaptchaKey = <redacted>, ForceSms = {ForceSms} }}";
}

public enum LegacyVkChallengeKind
{
    Captcha,
    TwoFactor,
    SmsValidation
}

public abstract record LegacyVkAuthResult;

public sealed record LegacyVkAuthSuccess(VkAccessToken AccessToken) : LegacyVkAuthResult;

public sealed record LegacyVkAuthChallenge(
    LegacyVkChallengeKind Kind,
    string? Sid,
    Uri? CaptchaImage,
    string? Description,
    TimeSpan? RetryAfter) : LegacyVkAuthResult;

public sealed record LegacyVkAuthFailure(string Error, string Description) : LegacyVkAuthResult;
