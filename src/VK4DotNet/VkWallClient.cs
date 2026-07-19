using System.Globalization;

namespace VK4DotNet;

public sealed class VkWallClient(VkClient client)
{
    public async Task<VkWallPostResult> PublishAsync(
        VkPublishPostRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Target);
        VkMessagesClient.ValidatePhotoCount(request.Photos);
        if (string.IsNullOrWhiteSpace(request.Message) && request.Photos.Count == 0)
        {
            throw new VkValidationException("Post text or at least one photo is required.");
        }

        var uploadedPhotos = new List<VkPhoto>(request.Photos.Count);
        foreach (var photo in request.Photos)
        {
            uploadedPhotos.Add(await client.Photos.UploadWallPhotoAsync(request.Target, photo, cancellationToken).ConfigureAwait(false));
        }

        long? ownerId = null;
        string? fromGroup = null;
        if (request.Target is VkWallTarget.CommunityWallTarget community)
        {
            if (community.GroupId <= 0)
            {
                throw new VkValidationException("A community group ID must be positive.");
            }

            ownerId = -community.GroupId;
            fromGroup = community.PublishAsCommunity ? "1" : "0";
        }

        var parameters = new Dictionary<string, string?>
        {
            ["owner_id"] = ownerId?.ToString(CultureInfo.InvariantCulture),
            ["from_group"] = fromGroup,
            ["message"] = request.Message,
            ["attachments"] = uploadedPhotos.Count > 0 ? string.Join(',', uploadedPhotos.Select(photo => photo.AttachmentKey)) : null,
            ["guid"] = (request.IdempotencyKey ?? Guid.NewGuid()).ToString("N")
        };

        var response = await client.CallElementAsync("wall.post", parameters, cancellationToken).ConfigureAwait(false);
        var postId = Internal.VkJson.GetInt64(response, "post_id");
        return new VkWallPostResult(postId, ownerId, uploadedPhotos);
    }
}
