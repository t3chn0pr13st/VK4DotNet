using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace VK4DotNet.LegacyAuth;

/// <summary>
/// Implements VK's deprecated resource-owner password flow. This flow may stop working at any time;
/// callers must provide credentials for their own VK application and obtain informed user consent.
/// </summary>
public sealed class LegacyVkPasswordAuthenticator : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private bool _disposed;

    public LegacyVkPasswordAuthenticator(LegacyVkAuthOptions options, HttpClient? httpClient = null)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
        Options.Validate();
        _httpClient = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;
    }

    public LegacyVkAuthOptions Options { get; }

    public async Task<LegacyVkAuthResult> AuthenticateAsync(
        LegacyVkAuthRequest request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
        {
            throw new VkValidationException("A VK username and password are required for legacy auth.");
        }

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = Options.ClientId.ToString(CultureInfo.InvariantCulture),
            ["client_secret"] = Options.ClientSecret,
            ["client_name"] = Options.ClientName,
            ["username"] = request.Username,
            ["password"] = request.Password,
            ["scope"] = string.Join(',', Options.Scopes),
            ["2fa_supported"] = "1",
            ["libverify_support"] = "1"
        };
        AddIfPresent(form, "code", request.TwoFactorCode);
        AddIfPresent(form, "captcha_sid", request.CaptchaSid);
        AddIfPresent(form, "captcha_key", request.CaptchaKey);
        if (request.ForceSms)
        {
            form["force_sms"] = "1";
        }

        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, Options.TokenEndpoint)
            {
                Content = new FormUrlEncodedContent(form)
            };
            message.Headers.UserAgent.Clear();
            message.Headers.UserAgent.ParseAdd(Options.UserAgent);
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await _httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            var accessToken = GetString(root, "access_token");
            if (response.IsSuccessStatusCode && !string.IsNullOrWhiteSpace(accessToken))
            {
                var expiresIn = GetInt64(root, "expires_in");
                long? userId = root.TryGetProperty("user_id", out _) ? GetInt64(root, "user_id") : null;
                return new LegacyVkAuthSuccess(new VkAccessToken(
                    accessToken,
                    expiresIn > 0 ? DateTimeOffset.UtcNow.AddSeconds(expiresIn) : null,
                    userId));
            }

            return ParseError(root, response.StatusCode);
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
            throw new VkTransportException("Legacy VK authorization failed at the transport layer.", exception);
        }
    }

    private static LegacyVkAuthResult ParseError(JsonElement root, System.Net.HttpStatusCode statusCode)
    {
        var error = GetString(root, "error") ?? $"http_{(int)statusCode}";
        var description = GetString(root, "error_description") ?? "Legacy VK authorization failed.";
        var captchaSid = GetString(root, "captcha_sid");
        if (error.Contains("captcha", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(captchaSid))
        {
            return new LegacyVkAuthChallenge(
                LegacyVkChallengeKind.Captcha,
                captchaSid,
                Uri.TryCreate(GetString(root, "captcha_img"), UriKind.Absolute, out var image) ? image : null,
                description,
                null);
        }

        if (error is "need_validation" or "validation_required")
        {
            var validationType = GetString(root, "validation_type");
            var isSms = validationType?.Contains("sms", StringComparison.OrdinalIgnoreCase) == true;
            var delay = GetInt64(root, "delay");
            return new LegacyVkAuthChallenge(
                isSms ? LegacyVkChallengeKind.SmsValidation : LegacyVkChallengeKind.TwoFactor,
                GetString(root, "validation_sid"),
                null,
                description,
                delay > 0 ? TimeSpan.FromSeconds(delay) : null);
        }

        return new LegacyVkAuthFailure(error, description);
    }

    private static void AddIfPresent(IDictionary<string, string> form, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            form[key] = value;
        }
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString()
            : null;

    private static long GetInt64(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.TryGetInt64(out var result) ? result : 0;

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
