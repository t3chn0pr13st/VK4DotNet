using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using VK4DotNet.Internal;

namespace VK4DotNet;

public sealed class VkMessagesClient(VkClient client)
{
    private const string ExtendedFields = "photo_50,photo_100,screen_name";

    public async Task<VkConversationPage> GetConversationsAsync(
        VkGetConversationsRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        request ??= new VkGetConversationsRequest();
        ValidatePage(request.Offset, request.Count);
        var parameters = new Dictionary<string, string?>
        {
            ["offset"] = Format(request.Offset),
            ["count"] = Format(request.Count),
            ["filter"] = Format(request.Filter),
            ["start_message_id"] = request.StartMessageId is { } id ? Format(id) : null,
            ["extended"] = "1",
            ["fields"] = ExtendedFields
        };

        var response = await client.CallElementAsync("messages.getConversations", parameters, cancellationToken).ConfigureAwait(false);
        return VkModelParser.ParseConversationPage(response);
    }

    public async IAsyncEnumerable<VkConversationItem> EnumerateConversationsAsync(
        VkGetConversationsRequest? request = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        request ??= new VkGetConversationsRequest();
        var offset = request.Offset;
        while (true)
        {
            var page = await GetConversationsAsync(request with { Offset = offset }, cancellationToken).ConfigureAwait(false);
            foreach (var item in page.Items)
            {
                yield return item;
            }

            offset += page.Items.Count;
            if (page.Items.Count == 0 || offset >= page.TotalCount)
            {
                yield break;
            }
        }
    }

    public async Task<VkMessagePage> GetHistoryAsync(
        VkGetHistoryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.PeerId == 0)
        {
            throw new VkValidationException("A non-zero peer ID is required.");
        }

        ValidatePage(request.Offset, request.Count);
        var parameters = new Dictionary<string, string?>
        {
            ["peer_id"] = Format(request.PeerId),
            ["offset"] = Format(request.Offset),
            ["count"] = Format(request.Count),
            ["start_message_id"] = request.StartMessageId is { } id ? Format(id) : null,
            ["rev"] = request.Chronological ? "1" : "0",
            ["extended"] = "1",
            ["fields"] = ExtendedFields
        };

        var response = await client.CallElementAsync("messages.getHistory", parameters, cancellationToken).ConfigureAwait(false);
        return VkModelParser.ParseMessagePage(response);
    }

    public async IAsyncEnumerable<VkMessage> EnumerateHistoryAsync(
        VkGetHistoryRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var offset = request.Offset;
        while (true)
        {
            var page = await GetHistoryAsync(request with { Offset = offset }, cancellationToken).ConfigureAwait(false);
            foreach (var message in page.Items)
            {
                yield return message;
            }

            offset += page.Items.Count;
            if (page.Items.Count == 0 || offset >= page.TotalCount)
            {
                yield break;
            }
        }
    }

    public async Task<VkSendMessageResult> SendAsync(
        VkSendMessageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.PeerId == 0)
        {
            throw new VkValidationException("A non-zero peer ID is required.");
        }

        ValidatePhotoCount(request.Photos);
        if (string.IsNullOrWhiteSpace(request.Message) && request.Photos.Count == 0)
        {
            throw new VkValidationException("A message or at least one photo is required.");
        }

        var uploadedPhotos = new List<VkPhoto>(request.Photos.Count);
        foreach (var photo in request.Photos)
        {
            uploadedPhotos.Add(await client.Photos.UploadMessagePhotoAsync(request.PeerId, photo, cancellationToken).ConfigureAwait(false));
        }

        var randomId = request.RandomId ?? RandomNumberGenerator.GetInt32(1, int.MaxValue);
        var parameters = new Dictionary<string, string?>
        {
            ["peer_id"] = Format(request.PeerId),
            ["message"] = request.Message,
            ["attachment"] = uploadedPhotos.Count > 0 ? string.Join(',', uploadedPhotos.Select(photo => photo.AttachmentKey)) : null,
            ["random_id"] = Format(randomId),
            ["reply_to"] = request.ReplyTo is { } replyTo ? Format(replyTo) : null
        };

        var response = await client.CallElementAsync("messages.send", parameters, cancellationToken).ConfigureAwait(false);
        var messageId = response.ValueKind == System.Text.Json.JsonValueKind.Number && response.TryGetInt64(out var value)
            ? value
            : 0;
        return new VkSendMessageResult(messageId, uploadedPhotos);
    }

    private static void ValidatePage(int offset, int count)
    {
        if (offset < 0 || count is < 1 or > 200)
        {
            throw new VkValidationException("Offset must be non-negative and count must be between 1 and 200.");
        }
    }

    internal static void ValidatePhotoCount(IReadOnlyList<VkUploadFile> photos)
    {
        if (photos.Count > 10)
        {
            throw new VkValidationException("VK accepts at most 10 photo attachments.");
        }
    }

    private static string Format(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Format(VkConversationFilter filter) => filter switch
    {
        VkConversationFilter.All => "all",
        VkConversationFilter.Unread => "unread",
        VkConversationFilter.Archive => "archive",
        VkConversationFilter.Important => "important",
        VkConversationFilter.Unanswered => "unanswered",
        _ => throw new ArgumentOutOfRangeException(nameof(filter))
    };
}
