using System.Text.Json;

namespace VK4DotNet;

public enum VkPeerType
{
    Unknown,
    User,
    Chat,
    Group,
    Email
}

public enum VkConversationFilter
{
    All,
    Unread,
    Archive,
    Important,
    Unanswered
}

public sealed record VkPeer(long Id, long LocalId, VkPeerType Type);

public sealed record VkConversation(
    VkPeer Peer,
    long InReadMessageId,
    long OutReadMessageId,
    int UnreadCount,
    bool IsImportant,
    bool IsUnanswered,
    bool CanWrite,
    string? Title,
    Uri? Photo50,
    Uri? Photo100);

public sealed record VkConversationItem(VkConversation Conversation, VkMessage? LastMessage);

public sealed record VkConversationPage(
    int TotalCount,
    int UnreadCount,
    IReadOnlyList<VkConversationItem> Items,
    IReadOnlyList<VkProfile> Profiles,
    IReadOnlyList<VkGroup> Groups);

public sealed record VkMessagePage(
    int TotalCount,
    IReadOnlyList<VkMessage> Items,
    IReadOnlyList<VkProfile> Profiles,
    IReadOnlyList<VkGroup> Groups);

public sealed record VkMessage(
    long Id,
    long ConversationMessageId,
    long PeerId,
    long FromId,
    DateTimeOffset SentAt,
    DateTimeOffset? UpdatedAt,
    string Text,
    bool IsOutgoing,
    IReadOnlyList<VkAttachment> Attachments,
    VkMessage? ReplyMessage,
    IReadOnlyList<VkMessage> ForwardedMessages);

public abstract record VkAttachment
{
    public abstract string Type { get; }
}

public sealed record VkPhotoAttachment(VkPhoto Photo) : VkAttachment
{
    public override string Type => "photo";
}

public sealed record VkUnknownAttachment(string AttachmentType, JsonElement Data) : VkAttachment
{
    public override string Type => AttachmentType;
}

public sealed record VkPhoto(
    long Id,
    long OwnerId,
    long AlbumId,
    long? UserId,
    DateTimeOffset? CreatedAt,
    string? Text,
    string? AccessKey,
    IReadOnlyList<VkPhotoSize> Sizes)
{
    private static readonly HashSet<string> CroppedTypes = new(StringComparer.Ordinal) { "o", "p", "q", "r" };

    public string AttachmentKey => string.IsNullOrWhiteSpace(AccessKey)
        ? $"photo{OwnerId}_{Id}"
        : $"photo{OwnerId}_{Id}_{AccessKey}";

    public VkPhotoSize? GetBestSize(int preferredWidth, bool avoidCropped = false)
    {
        if (preferredWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(preferredWidth));
        }

        var candidates = avoidCropped
            ? Sizes.Where(size => !CroppedTypes.Contains(size.Type)).ToArray()
            : Sizes;

        if (candidates.Count == 0)
        {
            return null;
        }

        return candidates
            .Where(size => size.Width >= preferredWidth)
            .OrderBy(size => size.Width)
            .FirstOrDefault()
            ?? candidates.OrderByDescending(size => size.Width).First();
    }
}

public sealed record VkPhotoSize(string Type, Uri Url, int Width, int Height);

public sealed record VkProfile(long Id, string FirstName, string LastName, Uri? Photo50, Uri? Photo100)
{
    public string DisplayName => string.Join(' ', new[] { FirstName, LastName }.Where(value => !string.IsNullOrWhiteSpace(value)));
}

public sealed record VkGroup(long Id, string Name, string? ScreenName, Uri? Photo50, Uri? Photo100);

public sealed record VkGetConversationsRequest(
    int Offset = 0,
    int Count = 20,
    VkConversationFilter Filter = VkConversationFilter.All,
    long? StartMessageId = null);

public sealed record VkGetHistoryRequest(
    long PeerId,
    int Offset = 0,
    int Count = 20,
    long? StartMessageId = null,
    bool Chronological = false);

public sealed class VkUploadFile
{
    public VkUploadFile(Stream content, string fileName, string contentType, bool leaveOpen = true)
    {
        Content = content ?? throw new ArgumentNullException(nameof(content));
        FileName = string.IsNullOrWhiteSpace(fileName)
            ? throw new VkValidationException("An upload file name is required.")
            : fileName;
        ContentType = string.IsNullOrWhiteSpace(contentType)
            ? throw new VkValidationException("An upload content type is required.")
            : contentType;
        LeaveOpen = leaveOpen;
    }

    public Stream Content { get; }
    public string FileName { get; }
    public string ContentType { get; }
    public bool LeaveOpen { get; }
}

public sealed record VkSendMessageRequest
{
    public required long PeerId { get; init; }
    public string? Message { get; init; }
    public IReadOnlyList<VkUploadFile> Photos { get; init; } = [];
    public long? RandomId { get; init; }
    public long? ReplyTo { get; init; }
}

public sealed record VkSendMessageResult(long MessageId, IReadOnlyList<VkPhoto> UploadedPhotos);

public abstract record VkWallTarget
{
    private VkWallTarget() { }

    public static VkWallTarget Self { get; } = new SelfWallTarget();

    public static VkWallTarget Community(long groupId, bool publishAsCommunity = true) =>
        new CommunityWallTarget(groupId, publishAsCommunity);

    internal sealed record SelfWallTarget : VkWallTarget;
    internal sealed record CommunityWallTarget(long GroupId, bool PublishAsCommunity) : VkWallTarget;
}

public sealed record VkPublishPostRequest
{
    public VkWallTarget Target { get; init; } = VkWallTarget.Self;
    public string? Message { get; init; }
    public IReadOnlyList<VkUploadFile> Photos { get; init; } = [];
    public Guid? IdempotencyKey { get; init; }
}

public sealed record VkWallPostResult(long PostId, long? OwnerId, IReadOnlyList<VkPhoto> UploadedPhotos);
