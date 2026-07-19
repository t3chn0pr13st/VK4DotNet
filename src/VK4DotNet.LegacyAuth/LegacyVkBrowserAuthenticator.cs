using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VK4DotNet.LegacyAuth;

/// <summary>
/// Starts legacy VK OAuth in a browser owned by the consuming application. VK handles all phone,
/// confirmation-code, password, CAPTCHA, and consent screens; this class only processes the final callback.
/// </summary>
public sealed class LegacyVkBrowserAuthenticator : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly IReadOnlyList<string> _scopes;
    private readonly long _scopeMask;
    private bool _disposed;

    public LegacyVkBrowserAuthenticator(LegacyVkBrowserAuthOptions options, HttpClient? httpClient = null)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
        Options.Validate();
        _scopes = Array.AsReadOnly(Options.Scopes.ToArray());
        _scopeMask = BuildScopeMask(_scopes);
        _httpClient = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;
    }

    public LegacyVkBrowserAuthOptions Options { get; }

    /// <summary>Creates a short-lived authorization URL and state value. The caller opens the URL in a system browser.</summary>
    public LegacyVkBrowserAuthorizationSession CreateAuthorizationSession()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        var parameters = new Dictionary<string, string?>
        {
            ["client_id"] = Options.ClientId.ToString(CultureInfo.InvariantCulture),
            ["redirect_uri"] = Options.RedirectUri.ToString(),
            ["display"] = Options.Display,
            ["scope"] = _scopeMask.ToString(CultureInfo.InvariantCulture),
            ["response_type"] = Options.Flow == LegacyVkBrowserFlow.AuthorizationCode ? "code" : "token",
            ["v"] = Options.ApiVersion,
            ["state"] = state,
            ["revoke"] = Options.RevokeExistingGrant ? "1" : null
        };

        var builder = new UriBuilder(Options.AuthorizationEndpoint)
        {
            Query = EncodeParameters(parameters)
        };
        return new LegacyVkBrowserAuthorizationSession(
            builder.Uri,
            state,
            Options.RedirectUri,
            Options.Flow,
            _scopes);
    }

    /// <summary>
    /// Validates a browser callback and returns its token. Fragment callbacks must be captured client-side because
    /// URI fragments are not sent to an HTTP callback server.
    /// </summary>
    public async Task<VkAccessToken> CompleteAsync(
        Uri callbackUri,
        LegacyVkBrowserAuthorizationSession session,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(callbackUri);
        ArgumentNullException.ThrowIfNull(session);

        if (!MatchesRedirect(callbackUri, session.RedirectUri)
            || !MatchesRedirect(session.RedirectUri, Options.RedirectUri)
            || session.Flow != Options.Flow)
        {
            throw new VkOAuthException("invalid_callback", "The legacy OAuth callback does not match its authorization session.");
        }

        Dictionary<string, string> parameters;
        try
        {
            parameters = ParseCallbackParameters(callbackUri);
        }
        catch (FormatException exception)
        {
            throw new VkOAuthException("invalid_callback", "The legacy OAuth callback contains malformed parameters.", exception);
        }

        if (!parameters.TryGetValue("state", out var returnedState)
            || !FixedTimeEquals(returnedState, session.State))
        {
            throw new VkOAuthException("state_mismatch", "The OAuth state returned by VK does not match the legacy authorization session.");
        }

        if (parameters.TryGetValue("error", out var callbackError))
        {
            var description = parameters.GetValueOrDefault("error_description") ?? "Legacy browser authorization was denied.";
            throw new VkOAuthException(
                callbackError,
                Redact(
                    description,
                    session.State,
                    parameters.GetValueOrDefault("code"),
                    parameters.GetValueOrDefault("access_token")));
        }

        return session.Flow switch
        {
            LegacyVkBrowserFlow.AuthorizationCode => await ExchangeCodeAsync(parameters, session, cancellationToken).ConfigureAwait(false),
            LegacyVkBrowserFlow.AccessToken => ParseAccessToken(parameters, "legacy browser callback"),
            _ => throw new VkOAuthException("unsupported_response_type", "The legacy browser authorization flow is not supported.")
        };
    }

    private async Task<VkAccessToken> ExchangeCodeAsync(
        IReadOnlyDictionary<string, string> callbackParameters,
        LegacyVkBrowserAuthorizationSession session,
        CancellationToken cancellationToken)
    {
        if (!callbackParameters.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code))
        {
            throw new VkOAuthException("invalid_callback", "The legacy OAuth callback is missing its authorization code.");
        }

        var form = new Dictionary<string, string>
        {
            ["client_id"] = Options.ClientId.ToString(CultureInfo.InvariantCulture),
            ["client_secret"] = Options.ClientSecret!,
            ["redirect_uri"] = session.RedirectUri.ToString(),
            ["code"] = code
        };

        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, Options.TokenEndpoint)
            {
                Content = new FormUrlEncodedContent(form)
            };
            message.Headers.UserAgent.ParseAdd(Options.UserAgent);
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await _httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            if (!response.IsSuccessStatusCode || root.TryGetProperty("error", out _))
            {
                var error = GetString(root, "error") ?? $"http_{(int)response.StatusCode}";
                var description = GetString(root, "error_description") ?? "Legacy authorization-code exchange failed.";
                throw new VkOAuthException(error, Redact(description, Options.ClientSecret, code));
            }

            return ParseAccessToken(root, "legacy token endpoint");
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
            throw new VkOAuthException("transport_error", "Legacy authorization-code exchange failed at the transport layer.", exception);
        }
    }

    private static VkAccessToken ParseAccessToken(IReadOnlyDictionary<string, string> values, string source)
    {
        if (!values.TryGetValue("access_token", out var accessToken) || string.IsNullOrWhiteSpace(accessToken))
        {
            throw new VkOAuthException("invalid_response", $"The {source} did not return an access token.");
        }

        var expiresIn = ParseInt64(values.GetValueOrDefault("expires_in"));
        var userId = ParseNullableInt64(values.GetValueOrDefault("user_id"));
        return new VkAccessToken(
            accessToken,
            expiresIn > 0 ? DateTimeOffset.UtcNow.AddSeconds(expiresIn) : null,
            userId);
    }

    private static VkAccessToken ParseAccessToken(JsonElement root, string source)
    {
        var accessToken = GetString(root, "access_token");
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new VkOAuthException("invalid_response", $"The {source} did not return an access token.");
        }

        var expiresIn = GetInt64(root, "expires_in");
        var userId = root.TryGetProperty("user_id", out var value) ? ParseNullableInt64(value.ToString()) : null;
        return new VkAccessToken(
            accessToken,
            expiresIn > 0 ? DateTimeOffset.UtcNow.AddSeconds(expiresIn) : null,
            userId);
    }

    private static Dictionary<string, string> ParseCallbackParameters(Uri callbackUri)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        AddParameters(result, callbackUri.Query);
        AddParameters(result, callbackUri.Fragment);
        return result;
    }

    private static void AddParameters(IDictionary<string, string> destination, string source)
    {
        foreach (var part in source.TrimStart('?', '#').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            var key = Decode(separator >= 0 ? part[..separator] : part);
            var value = Decode(separator >= 0 ? part[(separator + 1)..] : string.Empty);
            if (string.IsNullOrEmpty(key) || destination.ContainsKey(key))
            {
                throw new FormatException("A callback parameter is empty or duplicated.");
            }

            destination[key] = value;
        }
    }

    private static bool MatchesRedirect(Uri actual, Uri expected) =>
        actual.IsAbsoluteUri
        && string.Equals(actual.Scheme, expected.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(actual.Host, expected.Host, StringComparison.OrdinalIgnoreCase)
        && actual.Port == expected.Port
        && string.Equals(actual.UserInfo, expected.UserInfo, StringComparison.Ordinal)
        && string.Equals(actual.AbsolutePath, expected.AbsolutePath, StringComparison.Ordinal);

    private static string EncodeParameters(IEnumerable<KeyValuePair<string, string?>> parameters) =>
        string.Join('&', parameters
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value!)}"));

    private static string Decode(string value) => Uri.UnescapeDataString(value.Replace('+', ' '));

    private static long BuildScopeMask(IEnumerable<string> scopes)
    {
        long mask = 0;
        foreach (var scope in scopes)
        {
            var normalized = scope.Trim().ToLowerInvariant();
            var value = normalized switch
            {
                "notify" => 1L,
                "friends" => 2L,
                "photos" => 4L,
                "audio" => 8L,
                "video" => 16L,
                "pages" => 32L,
                "link" => 256L,
                "status" => 1024L,
                "notes" => 2048L,
                "messages" => 4096L,
                "wall" => 8192L,
                "ads" => 32768L,
                "offline" => 65536L,
                "docs" => 131072L,
                "groups" => 262144L,
                "notifications" => 524288L,
                "stats" => 1048576L,
                "email" => 4194304L,
                "market" => 134217728L,
                _ when long.TryParse(normalized, NumberStyles.None, CultureInfo.InvariantCulture, out var numeric) && numeric > 0 => numeric,
                _ => throw new VkValidationException($"Unknown legacy OAuth scope '{scope}'. Use a supported name or positive numeric mask value.")
            };
            mask |= value;
        }

        return mask;
    }

    private static string Redact(string value, params string?[] sensitiveValues)
    {
        foreach (var sensitive in sensitiveValues.Where(item => !string.IsNullOrEmpty(item)))
        {
            value = value.Replace(sensitive!, "<redacted>", StringComparison.Ordinal);
        }

        return value;
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool FixedTimeEquals(string left, string right) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString()
            : null;

    private static long GetInt64(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) ? ParseInt64(value.ToString()) : 0;

    private static long ParseInt64(string? value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : 0;

    private static long? ParseNullableInt64(string? value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : null;

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
