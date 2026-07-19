using System.Text.Json;

namespace VK4DotNet;

public sealed class VkClientOptions
{
    public const string DefaultApiVersion = "5.199";

    public Uri ApiBaseUri { get; init; } = new("https://api.vk.com/method/");
    public string ApiVersion { get; init; } = DefaultApiVersion;
    public string? Language { get; init; }
    public JsonSerializerOptions SerializerOptions { get; init; } = new(JsonSerializerDefaults.Web);

    internal void Validate()
    {
        if (!ApiBaseUri.IsAbsoluteUri || ApiBaseUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new VkValidationException("The VK API base URI must be an absolute HTTPS URI.");
        }

        if (string.IsNullOrWhiteSpace(ApiVersion))
        {
            throw new VkValidationException("The VK API version is required.");
        }
    }
}
