namespace VK4DotNet;

public sealed record VkAccessToken(string Value, DateTimeOffset? ExpiresAt = null, long? UserId = null)
{
    public bool IsExpired(TimeSpan? clockSkew = null) =>
        ExpiresAt is { } expiresAt && expiresAt <= DateTimeOffset.UtcNow.Add(clockSkew ?? TimeSpan.Zero);
}

public interface IVkTokenProvider
{
    ValueTask<VkAccessToken> GetTokenAsync(CancellationToken cancellationToken = default);

    ValueTask<VkAccessToken?> RefreshTokenAsync(
        VkAccessToken currentToken,
        CancellationToken cancellationToken = default);
}

public sealed class StaticVkTokenProvider : IVkTokenProvider
{
    private readonly VkAccessToken _token;

    public StaticVkTokenProvider(string accessToken)
        : this(new VkAccessToken(accessToken)) { }

    public StaticVkTokenProvider(VkAccessToken accessToken)
    {
        ArgumentNullException.ThrowIfNull(accessToken);
        if (string.IsNullOrWhiteSpace(accessToken.Value))
        {
            throw new VkValidationException("The access token is required.");
        }

        _token = accessToken;
    }

    public ValueTask<VkAccessToken> GetTokenAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(_token);

    public ValueTask<VkAccessToken?> RefreshTokenAsync(
        VkAccessToken currentToken,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<VkAccessToken?>(null);
}
