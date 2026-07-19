using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VK4DotNet.Internal;

namespace VK4DotNet.Auth;

public sealed class VkIdAuthClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private bool _disposed;

    public VkIdAuthClient(VkIdAuthOptions options, HttpClient? httpClient = null)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
        Options.Validate();
        _httpClient = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;
    }

    public VkIdAuthOptions Options { get; }

    public VkIdAuthorizationSession CreateAuthorizationSession(VkIdAuthorizationRequest? request = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        request ??= new VkIdAuthorizationRequest();
        var scopes = request.Scopes ?? Options.Scopes;
        if (scopes.Count == 0 || scopes.Any(string.IsNullOrWhiteSpace))
        {
            throw new VkValidationException("At least one valid VK ID scope is required.");
        }

        var verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var parameters = new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = Options.ClientId.ToString(CultureInfo.InvariantCulture),
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["redirect_uri"] = Options.RedirectUri.ToString(),
            ["scope"] = string.Join(' ', scopes),
            ["state"] = state,
            ["prompt"] = request.Prompt,
            ["provider"] = request.Provider,
            ["lang_id"] = request.LanguageId.ToString(CultureInfo.InvariantCulture),
            ["scheme"] = request.ColorScheme
        };
        var query = string.Join('&', parameters
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value!)}"));
        var authorizationUri = new Uri(new Uri(Options.BaseUri, "authorize"), "?" + query);
        return new VkIdAuthorizationSession(authorizationUri, verifier, state, Options.RedirectUri, scopes.ToArray());
    }

    public async Task<VkIdTokenSet> ExchangeCodeAsync(
        Uri callbackUri,
        VkIdAuthorizationSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callbackUri);
        ArgumentNullException.ThrowIfNull(session);
        var callback = ParseQuery(callbackUri.Query);
        if (callback.TryGetValue("error", out var error))
        {
            throw new VkOAuthException(error, callback.GetValueOrDefault("error_description") ?? "Authorization was denied.");
        }

        if (!callback.TryGetValue("state", out var state) || !FixedTimeEquals(state, session.State))
        {
            throw new VkOAuthException("state_mismatch", "The OAuth state returned by VK ID does not match the authorization session.");
        }

        if (!callback.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code)
            || !callback.TryGetValue("device_id", out var deviceId) || string.IsNullOrWhiteSpace(deviceId))
        {
            throw new VkOAuthException("invalid_callback", "The VK ID callback is missing code or device_id.");
        }

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = Options.ClientId.ToString(CultureInfo.InvariantCulture),
            ["code_verifier"] = session.CodeVerifier,
            ["redirect_uri"] = session.RedirectUri.ToString(),
            ["code"] = code,
            ["device_id"] = deviceId
        };
        var response = await SendOAuthAsync("oauth2/auth", form, cancellationToken).ConfigureAwait(false);
        return ParseTokenSet(response, deviceId, session.Scopes);
    }

    public async Task<VkIdTokenSet> RefreshAsync(
        VkIdTokenSet tokenSet,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tokenSet);
        if (string.IsNullOrWhiteSpace(tokenSet.RefreshToken) || string.IsNullOrWhiteSpace(tokenSet.DeviceId))
        {
            throw new VkValidationException("A refresh token and device ID are required.");
        }

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = Options.ClientId.ToString(CultureInfo.InvariantCulture),
            ["refresh_token"] = tokenSet.RefreshToken,
            ["device_id"] = tokenSet.DeviceId,
            ["scope"] = string.Join(' ', tokenSet.Scopes)
        };
        var response = await SendOAuthAsync("oauth2/auth", form, cancellationToken).ConfigureAwait(false);
        return ParseTokenSet(response, tokenSet.DeviceId, tokenSet.Scopes, tokenSet.RefreshToken);
    }

    public async Task RevokeAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new VkValidationException("An access token is required.");
        }

        await SendOAuthAsync(
            "oauth2/revoke",
            new Dictionary<string, string>
            {
                ["client_id"] = Options.ClientId.ToString(CultureInfo.InvariantCulture),
                ["access_token"] = accessToken
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonElement> SendOAuthAsync(
        string path,
        IReadOnlyDictionary<string, string> form,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            using var content = new FormUrlEncodedContent(form);
            using var response = await _httpClient.PostAsync(new Uri(Options.BaseUri, path), content, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (!response.IsSuccessStatusCode || root.TryGetProperty("error", out _))
            {
                var error = VkJson.GetString(root, "error") ?? $"http_{(int)response.StatusCode}";
                var description = VkJson.GetString(root, "error_description") ?? "VK ID request failed.";
                throw new VkOAuthException(error, description);
            }

            return root.Clone();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (VkException)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or IOException)
        {
            throw new VkOAuthException("transport_error", "VK ID request failed at the transport layer.", exception);
        }
    }

    private static VkIdTokenSet ParseTokenSet(
        JsonElement response,
        string deviceId,
        IReadOnlyList<string> scopes,
        string? fallbackRefreshToken = null)
    {
        var accessToken = VkJson.GetString(response, "access_token");
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new VkOAuthException("invalid_response", "VK ID did not return an access token.");
        }

        var refreshToken = VkJson.GetString(response, "refresh_token") ?? fallbackRefreshToken;
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            throw new VkOAuthException("invalid_response", "VK ID did not return a refresh token.");
        }

        var expiresIn = VkJson.GetInt64(response, "expires_in");
        var responseScope = VkJson.GetString(response, "scope");
        return new VkIdTokenSet(
            accessToken,
            refreshToken,
            VkJson.GetString(response, "id_token"),
            VkJson.GetString(response, "device_id") ?? deviceId,
            response.TryGetProperty("user_id", out _) ? VkJson.GetInt64(response, "user_id") : null,
            expiresIn > 0 ? DateTimeOffset.UtcNow.AddSeconds(expiresIn) : null,
            string.IsNullOrWhiteSpace(responseScope)
                ? scopes.ToArray()
                : responseScope.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            var key = separator >= 0 ? part[..separator] : part;
            var value = separator >= 0 ? part[(separator + 1)..] : string.Empty;
            values[Uri.UnescapeDataString(key.Replace('+', ' '))] = Uri.UnescapeDataString(value.Replace('+', ' '));
        }

        return values;
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool FixedTimeEquals(string left, string right) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}
