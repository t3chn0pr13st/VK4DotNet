using System.Text.Json;

namespace VK4DotNet.Internal;

internal static class VkModelParser
{
    public static VkConversationPage ParseConversationPage(JsonElement response)
    {
        var profiles = ParseArray(response, "profiles", ParseProfile);
        var groups = ParseArray(response, "groups", ParseGroup);
        var profileNames = profiles.ToDictionary(profile => profile.Id, profile => profile.DisplayName);
        var groupNames = groups.ToDictionary(group => -group.Id, group => group.Name);
        var items = new List<VkConversationItem>();

        if (response.TryGetProperty("items", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in array.EnumerateArray())
            {
                if (!item.TryGetProperty("conversation", out var conversationElement))
                {
                    continue;
                }

                var conversation = ParseConversation(conversationElement, profileNames, groupNames);
                VkMessage? lastMessage = null;
                if (item.TryGetProperty("last_message", out var lastMessageElement))
                {
                    lastMessage = ParseMessage(lastMessageElement);
                }

                items.Add(new VkConversationItem(conversation, lastMessage));
            }
        }

        return new VkConversationPage(
            VkJson.GetInt32(response, "count"),
            VkJson.GetInt32(response, "unread_count"),
            items,
            profiles,
            groups);
    }

    public static VkMessagePage ParseMessagePage(JsonElement response)
    {
        var profiles = ParseArray(response, "profiles", ParseProfile);
        var groups = ParseArray(response, "groups", ParseGroup);
        var messages = ParseArray(response, "items", ParseMessage);
        return new VkMessagePage(VkJson.GetInt32(response, "count"), messages, profiles, groups);
    }

    public static VkPhoto ParsePhoto(JsonElement photo)
    {
        var sizes = ParseArray(photo, "sizes", ParsePhotoSize);
        return new VkPhoto(
            VkJson.GetInt64(photo, "id"),
            VkJson.GetInt64(photo, "owner_id"),
            VkJson.GetInt64(photo, "album_id"),
            photo.TryGetProperty("user_id", out _) ? VkJson.GetInt64(photo, "user_id") : null,
            VkJson.GetTimestamp(photo, "date"),
            VkJson.GetString(photo, "text"),
            VkJson.GetString(photo, "access_key"),
            sizes);
    }

    private static VkConversation ParseConversation(
        JsonElement element,
        IReadOnlyDictionary<long, string> profileNames,
        IReadOnlyDictionary<long, string> groupNames)
    {
        var peerElement = element.GetProperty("peer");
        var peer = new VkPeer(
            VkJson.GetInt64(peerElement, "id"),
            VkJson.GetInt64(peerElement, "local_id"),
            ParsePeerType(VkJson.GetString(peerElement, "type")));

        string? title = null;
        Uri? photo50 = null;
        Uri? photo100 = null;
        if (element.TryGetProperty("chat_settings", out var chatSettings))
        {
            title = VkJson.GetString(chatSettings, "title");
            if (chatSettings.TryGetProperty("photo", out var photo))
            {
                photo50 = VkJson.GetUri(photo, "photo_50");
                photo100 = VkJson.GetUri(photo, "photo_100");
            }
        }
        else if (!profileNames.TryGetValue(peer.Id, out title))
        {
            groupNames.TryGetValue(peer.Id, out title);
        }

        var canWrite = element.TryGetProperty("can_write", out var canWriteElement)
            && VkJson.GetBoolean(canWriteElement, "allowed");

        return new VkConversation(
            peer,
            VkJson.GetInt64(element, "in_read"),
            VkJson.GetInt64(element, "out_read"),
            VkJson.GetInt32(element, "unread_count"),
            VkJson.GetBoolean(element, "important"),
            VkJson.GetBoolean(element, "unanswered"),
            canWrite,
            title,
            photo50,
            photo100);
    }

    private static VkMessage ParseMessage(JsonElement element)
    {
        VkMessage? reply = null;
        if (element.TryGetProperty("reply_message", out var replyElement) && replyElement.ValueKind == JsonValueKind.Object)
        {
            reply = ParseMessage(replyElement);
        }

        var forwarded = ParseArray(element, "fwd_messages", ParseMessage);
        var attachments = ParseArray(element, "attachments", ParseAttachment);
        return new VkMessage(
            VkJson.GetInt64(element, "id"),
            VkJson.GetInt64(element, "conversation_message_id"),
            VkJson.GetInt64(element, "peer_id"),
            VkJson.GetInt64(element, "from_id"),
            VkJson.GetTimestamp(element, "date") ?? DateTimeOffset.UnixEpoch,
            VkJson.GetTimestamp(element, "update_time"),
            VkJson.GetString(element, "text") ?? string.Empty,
            VkJson.GetBoolean(element, "out"),
            attachments,
            reply,
            forwarded);
    }

    private static VkAttachment ParseAttachment(JsonElement element)
    {
        var type = VkJson.GetString(element, "type") ?? "unknown";
        if (type == "photo" && element.TryGetProperty("photo", out var photo))
        {
            return new VkPhotoAttachment(ParsePhoto(photo));
        }

        return new VkUnknownAttachment(
            type,
            element.TryGetProperty(type, out var data) ? data.Clone() : element.Clone());
    }

    private static VkPhotoSize ParsePhotoSize(JsonElement element) => new(
        VkJson.GetString(element, "type") ?? string.Empty,
        VkJson.GetUri(element, "url") ?? new Uri("about:blank"),
        VkJson.GetInt32(element, "width"),
        VkJson.GetInt32(element, "height"));

    private static VkProfile ParseProfile(JsonElement element) => new(
        VkJson.GetInt64(element, "id"),
        VkJson.GetString(element, "first_name") ?? string.Empty,
        VkJson.GetString(element, "last_name") ?? string.Empty,
        VkJson.GetUri(element, "photo_50"),
        VkJson.GetUri(element, "photo_100"));

    private static VkGroup ParseGroup(JsonElement element) => new(
        VkJson.GetInt64(element, "id"),
        VkJson.GetString(element, "name") ?? string.Empty,
        VkJson.GetString(element, "screen_name"),
        VkJson.GetUri(element, "photo_50"),
        VkJson.GetUri(element, "photo_100"));

    private static VkPeerType ParsePeerType(string? type) => type switch
    {
        "user" => VkPeerType.User,
        "chat" => VkPeerType.Chat,
        "group" => VkPeerType.Group,
        "email" => VkPeerType.Email,
        _ => VkPeerType.Unknown
    };

    private static IReadOnlyList<T> ParseArray<T>(JsonElement element, string name, Func<JsonElement, T> parser)
    {
        if (!element.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return array.EnumerateArray().Select(parser).ToArray();
    }
}
