using System.Net;
using System.Text;
using VK4DotNet.Auth;

namespace VK4DotNet.Tests;

public sealed class VkClientTests
{
    [Fact]
    public async Task Conversations_parse_peers_profiles_and_nested_messages()
    {
        var handler = new QueueHttpMessageHandler();
        handler.EnqueueJson("""
        {"response":{"count":1,"unread_count":2,"profiles":[{"id":7,"first_name":"Ada","last_name":"Lovelace","photo_50":"https://img/50"}],"groups":[],"items":[{"conversation":{"peer":{"id":7,"local_id":7,"type":"user"},"in_read":10,"out_read":9,"unread_count":2,"can_write":{"allowed":1}},"last_message":{"id":11,"conversation_message_id":5,"peer_id":7,"from_id":7,"date":1700000000,"text":"hello","out":0,"attachments":[{"type":"photo","photo":{"id":2,"owner_id":7,"album_id":1,"access_key":"key","sizes":[{"type":"x","url":"https://img/x","width":604,"height":400}]}},{"type":"video","video":{"id":3}}],"reply_message":{"id":8,"peer_id":7,"from_id":1,"date":1699999999,"text":"reply"},"fwd_messages":[{"id":9,"peer_id":7,"from_id":2,"date":1699999998,"text":"forward"}]}}]}}
        """);
        using var client = CreateClient(handler);

        var page = await client.Messages.GetConversationsAsync();

        var item = Assert.Single(page.Items);
        Assert.Equal("Ada Lovelace", item.Conversation.Title);
        Assert.Equal(VkPeerType.User, item.Conversation.Peer.Type);
        Assert.Equal(2, item.Conversation.UnreadCount);
        Assert.Equal("reply", item.LastMessage!.ReplyMessage!.Text);
        Assert.Equal("forward", Assert.Single(item.LastMessage.ForwardedMessages).Text);
        var photo = Assert.IsType<VkPhotoAttachment>(item.LastMessage.Attachments[0]).Photo;
        Assert.Equal("photo7_2_key", photo.AttachmentKey);
        Assert.IsType<VkUnknownAttachment>(item.LastMessage.Attachments[1]);
    }

    [Fact]
    public async Task History_enumerator_pages_until_total_count()
    {
        var handler = new QueueHttpMessageHandler();
        handler.EnqueueJson("""{"response":{"count":2,"items":[{"id":1,"peer_id":7,"from_id":7,"date":1,"text":"one"}]}}""");
        handler.EnqueueJson("""{"response":{"count":2,"items":[{"id":2,"peer_id":7,"from_id":7,"date":2,"text":"two"}]}}""");
        using var client = CreateClient(handler);
        var messages = new List<VkMessage>();

        await foreach (var message in client.Messages.EnumerateHistoryAsync(new VkGetHistoryRequest(7, Count: 1)))
        {
            messages.Add(message);
        }

        Assert.Equal([1L, 2L], messages.Select(message => message.Id));
        Assert.Contains("offset=1", Uri.UnescapeDataString(handler.Requests[1].Body));
    }

    [Fact]
    public async Task Sending_photo_runs_upload_save_and_send_pipeline_without_closing_stream()
    {
        var handler = new QueueHttpMessageHandler();
        handler.EnqueueJson("""{"response":{"upload_url":"https://upload.vk.test/message"}}""");
        handler.EnqueueJson("""{"photo":"opaque","server":5,"hash":"hash"}""");
        handler.EnqueueJson("""{"response":[{"id":12,"owner_id":7,"album_id":-3,"access_key":"ak","sizes":[]}]}""");
        handler.EnqueueJson("""{"response":101}""");
        using var client = CreateClient(handler);
        var stream = new MemoryStream(Encoding.UTF8.GetBytes("image"));

        var result = await client.Messages.SendAsync(new VkSendMessageRequest
        {
            PeerId = 7,
            Message = "caption",
            Photos = [new VkUploadFile(stream, "photo.jpg", "image/jpeg")],
            RandomId = 55
        });

        Assert.Equal(101, result.MessageId);
        Assert.True(stream.CanRead);
        Assert.Equal(4, handler.Requests.Count);
        Assert.Contains("multipart/form-data", handler.Requests[1].ContentType);
        var sendBody = Uri.UnescapeDataString(handler.Requests[3].Body);
        Assert.Contains("attachment=photo7_12_ak", sendBody);
        Assert.Contains("random_id=55", sendBody);
    }

    [Fact]
    public async Task Publishing_to_community_uses_user_wall_upload_and_idempotency_guid()
    {
        var handler = new QueueHttpMessageHandler();
        handler.EnqueueJson("""{"response":{"upload_url":"https://upload.vk.test/wall"}}""");
        handler.EnqueueJson("""{"photo":"opaque","server":6,"hash":"hash"}""");
        handler.EnqueueJson("""{"response":[{"id":20,"owner_id":-42,"album_id":-7,"sizes":[]}]}""");
        handler.EnqueueJson("""{"response":{"post_id":77}}""");
        using var client = CreateClient(handler);
        var guid = Guid.Parse("11111111-2222-3333-4444-555555555555");

        var result = await client.Wall.PublishAsync(new VkPublishPostRequest
        {
            Target = VkWallTarget.Community(42),
            Message = "release",
            Photos = [new VkUploadFile(new MemoryStream([1, 2, 3]), "wall.png", "image/png")],
            IdempotencyKey = guid
        });

        Assert.Equal(77, result.PostId);
        Assert.Equal(-42, result.OwnerId);
        Assert.Contains("group_id=42", Uri.UnescapeDataString(handler.Requests[0].Body));
        var postBody = Uri.UnescapeDataString(handler.Requests[3].Body);
        Assert.Contains("owner_id=-42", postBody);
        Assert.Contains("from_group=1", postBody);
        Assert.Contains("guid=11111111222233334444555555555555", postBody);
    }

    [Fact]
    public async Task Api_errors_redact_sensitive_request_parameters()
    {
        var handler = new QueueHttpMessageHandler();
        handler.EnqueueJson("""
        {"error":{"error_code":100,"error_msg":"bad","request_params":[{"key":"access_token","value":"secret"},{"key":"peer_id","value":"7"}]}}
        """);
        using var client = CreateClient(handler);

        var error = await Assert.ThrowsAsync<VkApiException>(() => client.CallAsync<object>("messages.send"));

        Assert.Equal("[REDACTED]", error.RequestParameters["access_token"]);
        Assert.Equal("7", error.RequestParameters["peer_id"]);
        Assert.DoesNotContain("secret", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Auth_error_refreshes_once_and_retries()
    {
        var handler = new QueueHttpMessageHandler();
        handler.EnqueueJson("""{"error":{"error_code":5,"error_msg":"expired"}}""");
        handler.EnqueueJson("""{"response":{"ok":true}}""");
        var provider = new RefreshingProvider();
        using var client = new VkClient(provider, httpClient: new HttpClient(handler));

        var result = await client.CallAsync<Dictionary<string, bool>>("test.method");

        Assert.True(result["ok"]);
        Assert.Equal(1, provider.RefreshCount);
        Assert.Contains("access_token=new", Uri.UnescapeDataString(handler.Requests[1].Body));
    }

    [Fact]
    public async Task Cancellation_is_not_wrapped()
    {
        var handler = new QueueHttpMessageHandler();
        handler.Enqueue(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var client = CreateClient(handler);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.CallAsync<object>("users.get", cancellationToken: cancellation.Token));
    }

    [Fact]
    public void Photo_selects_smallest_sufficient_non_cropped_size()
    {
        var photo = new VkPhoto(1, 2, 3, null, null, null, null,
        [
            new VkPhotoSize("q", new Uri("https://img/q"), 320, 200),
            new VkPhotoSize("x", new Uri("https://img/x"), 604, 400),
            new VkPhotoSize("z", new Uri("https://img/z"), 1080, 800)
        ]);

        Assert.Equal("x", photo.GetBestSize(500)!.Type);
        Assert.Equal("x", photo.GetBestSize(300, avoidCropped: true)!.Type);
        Assert.Equal("z", photo.GetBestSize(2000)!.Type);
    }

    [Fact]
    public async Task More_than_ten_photos_is_rejected_before_network_io()
    {
        var handler = new QueueHttpMessageHandler();
        using var client = CreateClient(handler);
        var files = Enumerable.Range(0, 11)
            .Select(index => new VkUploadFile(new MemoryStream([1]), $"{index}.jpg", "image/jpeg"))
            .ToArray();

        await Assert.ThrowsAsync<VkValidationException>(() => client.Messages.SendAsync(new VkSendMessageRequest
        {
            PeerId = 1,
            Photos = files
        }));
        Assert.Empty(handler.Requests);
    }

    private static VkClient CreateClient(QueueHttpMessageHandler handler) =>
        new("token", httpClient: new HttpClient(handler));

    private sealed class RefreshingProvider : IVkTokenProvider
    {
        public int RefreshCount { get; private set; }

        public ValueTask<VkAccessToken> GetTokenAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new VkAccessToken("old"));

        public ValueTask<VkAccessToken?> RefreshTokenAsync(VkAccessToken currentToken, CancellationToken cancellationToken = default)
        {
            RefreshCount++;
            return ValueTask.FromResult<VkAccessToken?>(new VkAccessToken("new"));
        }
    }
}
