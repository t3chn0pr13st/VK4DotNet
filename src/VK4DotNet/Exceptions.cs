namespace VK4DotNet;

/// <summary>Base exception for VK4DotNet.</summary>
public class VkException : Exception
{
    public VkException(string message) : base(message) { }
    public VkException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>An error returned by a VK API method.</summary>
public sealed class VkApiException : VkException
{
    internal VkApiException(
        int errorCode,
        string message,
        IReadOnlyDictionary<string, string?> requestParameters,
        string? captchaSid = null,
        Uri? captchaImage = null)
        : base($"VK API error {errorCode}: {message}")
    {
        ErrorCode = errorCode;
        ApiMessage = message;
        RequestParameters = requestParameters;
        CaptchaSid = captchaSid;
        CaptchaImage = captchaImage;
    }

    public int ErrorCode { get; }
    public string ApiMessage { get; }
    public IReadOnlyDictionary<string, string?> RequestParameters { get; }
    public string? CaptchaSid { get; }
    public Uri? CaptchaImage { get; }
}

/// <summary>An error returned by a VK OAuth endpoint.</summary>
public sealed class VkOAuthException : VkException
{
    public VkOAuthException(string error, string description)
        : base($"VK OAuth error '{error}': {description}")
    {
        Error = error;
        Description = description;
    }

    public VkOAuthException(string error, string description, Exception innerException)
        : base($"VK OAuth error '{error}': {description}", innerException)
    {
        Error = error;
        Description = description;
    }

    public string Error { get; }
    public string Description { get; }
}

/// <summary>A transport-level HTTP error.</summary>
public sealed class VkTransportException : VkException
{
    public VkTransportException(string message, Exception innerException) : base(message, innerException) { }
    public VkTransportException(string message) : base(message) { }
}

/// <summary>A caller supplied an invalid request.</summary>
public sealed class VkValidationException : VkException
{
    public VkValidationException(string message) : base(message) { }
}
