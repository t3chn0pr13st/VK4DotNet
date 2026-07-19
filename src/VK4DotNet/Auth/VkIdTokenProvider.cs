namespace VK4DotNet.Auth;

public sealed class VkIdTokenProvider : IVkTokenProvider, IDisposable
{
    private readonly VkIdAuthClient _authClient;
    private readonly Func<VkIdTokenSet, CancellationToken, ValueTask>? _tokensChanged;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private VkIdTokenSet _tokens;
    private bool _disposed;

    public VkIdTokenProvider(
        VkIdAuthClient authClient,
        VkIdTokenSet initialTokens,
        Func<VkIdTokenSet, CancellationToken, ValueTask>? tokensChanged = null)
    {
        _authClient = authClient ?? throw new ArgumentNullException(nameof(authClient));
        _tokens = initialTokens ?? throw new ArgumentNullException(nameof(initialTokens));
        _tokensChanged = tokensChanged;
    }

    public async ValueTask<VkAccessToken> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var token = _tokens.ToAccessToken();
        if (token.IsExpired(TimeSpan.FromSeconds(30)))
        {
            return await RefreshRequiredAsync(token, cancellationToken).ConfigureAwait(false);
        }

        return token;
    }

    public async ValueTask<VkAccessToken?> RefreshTokenAsync(
        VkAccessToken currentToken,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return await RefreshRequiredAsync(currentToken, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<VkAccessToken> RefreshRequiredAsync(VkAccessToken currentToken, CancellationToken cancellationToken)
    {
        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!string.Equals(_tokens.AccessToken, currentToken.Value, StringComparison.Ordinal))
            {
                return _tokens.ToAccessToken();
            }

            _tokens = await _authClient.RefreshAsync(_tokens, cancellationToken).ConfigureAwait(false);
            if (_tokensChanged is not null)
            {
                await _tokensChanged(_tokens, cancellationToken).ConfigureAwait(false);
            }

            return _tokens.ToAccessToken();
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _refreshLock.Dispose();
    }
}
