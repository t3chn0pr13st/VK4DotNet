using System.Net.Http.Headers;
using System.Text.Json;

namespace VK4DotNet;

public sealed class VkClient : IDisposable
{
    private static readonly HashSet<string> SensitiveParameterNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "access_token", "refresh_token", "password", "client_secret", "code", "code_verifier"
    };

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly IVkTokenProvider _tokenProvider;
    private bool _disposed;

    public VkClient(string accessToken, VkClientOptions? options = null, HttpClient? httpClient = null)
        : this(new StaticVkTokenProvider(accessToken), options, httpClient) { }

    public VkClient(IVkTokenProvider tokenProvider, VkClientOptions? options = null, HttpClient? httpClient = null)
    {
        _tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
        Options = options ?? new VkClientOptions();
        Options.Validate();
        _httpClient = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;

        Messages = new VkMessagesClient(this);
        Photos = new VkPhotosClient(this);
        Wall = new VkWallClient(this);
    }

    public VkClientOptions Options { get; }
    public VkMessagesClient Messages { get; }
    public VkPhotosClient Photos { get; }
    public VkWallClient Wall { get; }

    public async Task<TResponse> CallAsync<TResponse>(
        string method,
        IReadOnlyDictionary<string, string?>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        var response = await CallElementAsync(method, parameters, cancellationToken).ConfigureAwait(false);
        try
        {
            return response.Deserialize<TResponse>(Options.SerializerOptions)
                ?? throw new VkTransportException($"VK method '{method}' returned an empty response.");
        }
        catch (JsonException exception)
        {
            throw new VkTransportException($"VK method '{method}' returned an unexpected JSON response.", exception);
        }
    }

    internal async Task<JsonElement> CallElementAsync(
        string method,
        IReadOnlyDictionary<string, string?>? parameters,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateMethod(method);

        var token = await _tokenProvider.GetTokenAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await SendApiRequestAsync(method, parameters, token, cancellationToken).ConfigureAwait(false);
        }
        catch (VkApiException exception) when (exception.ErrorCode == 5)
        {
            var refreshed = await _tokenProvider.RefreshTokenAsync(token, cancellationToken).ConfigureAwait(false);
            if (refreshed is null || string.IsNullOrWhiteSpace(refreshed.Value))
            {
                throw;
            }

            return await SendApiRequestAsync(method, parameters, refreshed, cancellationToken).ConfigureAwait(false);
        }
    }

    internal async Task<JsonElement> SendUploadAsync(
        Uri uploadUri,
        string fieldName,
        VkUploadFile file,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!uploadUri.IsAbsoluteUri || uploadUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new VkTransportException("VK returned a non-HTTPS upload URL.");
        }

        using var multipart = new MultipartFormDataContent();
        var source = file.LeaveOpen ? new Internal.NonDisposingStream(file.Content) : file.Content;
        var streamContent = new StreamContent(source);
        streamContent.Headers.ContentType = MediaTypeHeaderValue.Parse(file.ContentType);
        multipart.Add(streamContent, fieldName, file.FileName);

        try
        {
            using var response = await _httpClient.PostAsync(uploadUri, multipart, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new VkTransportException($"VK upload server returned HTTP {(int)response.StatusCode}.");
            }

            using var document = JsonDocument.Parse(body);
            return document.RootElement.Clone();
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
            throw new VkTransportException("VK photo upload failed.", exception);
        }
    }

    private async Task<JsonElement> SendApiRequestAsync(
        string method,
        IReadOnlyDictionary<string, string?>? parameters,
        VkAccessToken token,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token.Value))
        {
            throw new VkValidationException("The token provider returned an empty access token.");
        }

        var form = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["access_token"] = token.Value,
            ["v"] = Options.ApiVersion
        };

        if (!string.IsNullOrWhiteSpace(Options.Language))
        {
            form["lang"] = Options.Language;
        }

        if (parameters is not null)
        {
            foreach (var parameter in parameters)
            {
                if (parameter.Value is not null)
                {
                    form[parameter.Key] = parameter.Value;
                }
            }
        }

        var requestUri = new Uri(Options.ApiBaseUri, Uri.EscapeDataString(method));
        try
        {
            using var content = new FormUrlEncodedContent(form);
            using var response = await _httpClient.PostAsync(requestUri, content, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new VkTransportException($"VK API returned HTTP {(int)response.StatusCode} for '{method}'.");
            }

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.TryGetProperty("error", out var error))
            {
                throw ParseApiException(error);
            }

            if (!root.TryGetProperty("response", out var apiResponse))
            {
                throw new VkTransportException($"VK method '{method}' returned neither response nor error.");
            }

            return apiResponse.Clone();
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
            throw new VkTransportException($"VK method '{method}' failed at the transport layer.", exception);
        }
    }

    private static VkApiException ParseApiException(JsonElement error)
    {
        var errorCode = Internal.VkJson.GetInt32(error, "error_code");
        var message = Internal.VkJson.GetString(error, "error_msg") ?? "Unknown VK API error";
        var safeParameters = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        if (error.TryGetProperty("request_params", out var requestParameters) && requestParameters.ValueKind == JsonValueKind.Array)
        {
            foreach (var parameter in requestParameters.EnumerateArray())
            {
                var key = Internal.VkJson.GetString(parameter, "key");
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                safeParameters[key] = SensitiveParameterNames.Contains(key)
                    ? "[REDACTED]"
                    : Internal.VkJson.GetString(parameter, "value");
            }
        }

        var captchaSid = Internal.VkJson.GetString(error, "captcha_sid");
        var captchaImage = Internal.VkJson.GetUri(error, "captcha_img");
        return new VkApiException(errorCode, message, safeParameters, captchaSid, captchaImage);
    }

    private static void ValidateMethod(string method)
    {
        if (string.IsNullOrWhiteSpace(method) || method.Any(character => !(char.IsLetterOrDigit(character) || character is '.' or '_')))
        {
            throw new VkValidationException("A VK method name may contain only letters, digits, dots, and underscores.");
        }
    }

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
