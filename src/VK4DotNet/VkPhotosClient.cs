using System.Globalization;
using System.Text.Json;
using VK4DotNet.Internal;

namespace VK4DotNet;

public sealed class VkPhotosClient(VkClient client)
{
    public async Task<VkPhoto> UploadMessagePhotoAsync(
        long peerId,
        VkUploadFile file,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (peerId == 0)
        {
            throw new VkValidationException("A non-zero peer ID is required.");
        }

        var server = await client.CallElementAsync(
            "photos.getMessagesUploadServer",
            new Dictionary<string, string?> { ["peer_id"] = Format(peerId) },
            cancellationToken).ConfigureAwait(false);

        var upload = await UploadAsync(server, file, cancellationToken).ConfigureAwait(false);
        var saved = await client.CallElementAsync(
            "photos.saveMessagesPhoto",
            CreateSaveParameters(upload),
            cancellationToken).ConfigureAwait(false);
        return ParseFirstPhoto(saved);
    }

    public async Task<VkPhoto> UploadWallPhotoAsync(
        VkWallTarget target,
        VkUploadFile file,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(file);

        var groupId = target is VkWallTarget.CommunityWallTarget community ? community.GroupId : (long?)null;
        if (groupId is <= 0)
        {
            throw new VkValidationException("A community group ID must be positive.");
        }

        var serverParameters = new Dictionary<string, string?>
        {
            ["group_id"] = groupId is { } value ? Format(value) : null
        };
        var server = await client.CallElementAsync("photos.getWallUploadServer", serverParameters, cancellationToken).ConfigureAwait(false);
        var upload = await UploadAsync(server, file, cancellationToken).ConfigureAwait(false);
        var saveParameters = CreateSaveParameters(upload);
        saveParameters["group_id"] = groupId is { } id ? Format(id) : null;
        var saved = await client.CallElementAsync("photos.saveWallPhoto", saveParameters, cancellationToken).ConfigureAwait(false);
        return ParseFirstPhoto(saved);
    }

    private async Task<JsonElement> UploadAsync(JsonElement server, VkUploadFile file, CancellationToken cancellationToken)
    {
        var uploadUrl = VkJson.GetUri(server, "upload_url") ?? VkJson.GetUri(server, "url");
        if (uploadUrl is null)
        {
            throw new VkTransportException("VK did not return a photo upload URL.");
        }

        return await client.SendUploadAsync(uploadUrl, "photo", file, cancellationToken).ConfigureAwait(false);
    }

    private static Dictionary<string, string?> CreateSaveParameters(JsonElement upload)
    {
        var photo = VkJson.GetString(upload, "photo");
        var hash = VkJson.GetString(upload, "hash");
        var server = VkJson.GetString(upload, "server");
        if (string.IsNullOrWhiteSpace(photo) || string.IsNullOrWhiteSpace(hash) || string.IsNullOrWhiteSpace(server))
        {
            throw new VkTransportException("VK upload response is missing photo, server, or hash.");
        }

        return new Dictionary<string, string?>
        {
            ["photo"] = photo,
            ["server"] = server,
            ["hash"] = hash
        };
    }

    private static VkPhoto ParseFirstPhoto(JsonElement response)
    {
        if (response.ValueKind != JsonValueKind.Array || response.GetArrayLength() == 0)
        {
            throw new VkTransportException("VK did not return a saved photo.");
        }

        return VkModelParser.ParsePhoto(response[0]);
    }

    private static string Format(long value) => value.ToString(CultureInfo.InvariantCulture);
}
