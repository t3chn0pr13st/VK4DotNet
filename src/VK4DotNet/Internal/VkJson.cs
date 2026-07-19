using System.Globalization;
using System.Text.Json;

namespace VK4DotNet.Internal;

internal static class VkJson
{
    public static string? GetString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    public static int GetInt32(JsonElement element, string name) =>
        checked((int)GetInt64(element, name));

    public static long GetInt64(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return 0;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
        {
            return number;
        }

        return long.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number) ? number : 0;
    }

    public static bool GetBoolean(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.Number => value.TryGetInt32(out var number) && number != 0,
            JsonValueKind.String => value.GetString() is "1" or "true",
            _ => false
        };
    }

    public static Uri? GetUri(JsonElement element, string name)
    {
        var value = GetString(element, name);
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri : null;
    }

    public static DateTimeOffset? GetTimestamp(JsonElement element, string name)
    {
        var seconds = GetInt64(element, name);
        return seconds > 0 ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
    }
}
