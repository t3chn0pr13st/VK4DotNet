# VK4DotNet

[Русская версия](README.ru.md)

VK4DotNet is an asynchronous .NET 10 client for the VK API. Version 1.0 focuses on personal conversations, photo messages, and publishing photo posts to a user's own wall or a managed community.

The API and model design are based on the GPL-licensed [VK4ME/client](https://github.com/VK4ME/client) and its J2VK library, updated for the official VK API 5.199 schema. VK4DotNet does not contain the embedded credentials, refresh token, or impersonated official-client User-Agent found in the historical J2ME code.

> [!IMPORTANT]
> The current official VK ID scope list does not include `messages`. Reading and sending personal messages therefore requires a compatible externally supplied user token or the explicitly opt-in `VK4DotNet.LegacyAuth` package. The legacy password flow is deprecated, may be unavailable for your application or account, and can stop working without notice.

## Packages

- `VK4DotNet` — API client, immutable models, photo uploads, wall publishing, and VK ID OAuth 2.1 with PKCE.
- `VK4DotNet.LegacyAuth` — isolated resource-owner password flow with CAPTCHA, 2FA, and SMS challenge results.

Packages are attached to [GitHub Releases](https://github.com/t3chn0pr13st/VK4DotNet/releases), not published to NuGet.org. Download the `.nupkg` files, put them in a local package source, and install them:

```sh
dotnet nuget add source /path/to/packages --name VK4DotNetLocal
dotnet add package VK4DotNet --version 1.0.0
dotnet add package VK4DotNet.LegacyAuth --version 1.0.0
```

## Use an existing token

```csharp
using VK4DotNet;

using var vk = new VkClient(Environment.GetEnvironmentVariable("VK_USER_TOKEN")!);

var page = await vk.Messages.GetConversationsAsync(
    new VkGetConversationsRequest(Count: 20),
    cancellationToken);

await foreach (var message in vk.Messages.EnumerateHistoryAsync(
    new VkGetHistoryRequest(page.Items[0].Conversation.Peer.Id, Count: 100),
    cancellationToken))
{
    foreach (var attachment in message.Attachments.OfType<VkPhotoAttachment>())
    {
        Console.WriteLine(attachment.Photo.GetBestSize(1080)?.Url);
    }
}
```

`VkClient` also accepts an `IVkTokenProvider`, an injected `HttpClient`, and configurable API base URI/version. It performs one token refresh and one retry only after VK API error 5; ordinary requests and photo uploads are not automatically retried.

## VK ID with host-managed browser and callback

```csharp
using VK4DotNet.Auth;

using var auth = new VkIdAuthClient(new VkIdAuthOptions
{
    ClientId = 123456,
    RedirectUri = new Uri("https://example.test/vk/callback")
});

var session = auth.CreateAuthorizationSession();
// Open session.AuthorizationUri in the host application.
// Persist session only for the duration of this authorization attempt.

var callbackUri = new Uri(callbackFromYourWebDesktopOrMobileHost);
var tokens = await auth.ExchangeCodeAsync(callbackUri, session, cancellationToken);

using var provider = new VkIdTokenProvider(
    auth,
    tokens,
    (updated, ct) => SaveTokensSecurelyAsync(updated, ct));
using var vk = new VkClient(provider);
```

The library never opens a browser and never stores tokens. The host is responsible for secure session and token storage.

## Send a message with photos

```csharp
await using var image = File.OpenRead("photo.jpg");
var result = await vk.Messages.SendAsync(new VkSendMessageRequest
{
    PeerId = 123456,
    Message = "Hello from VK4DotNet",
    Photos = [new VkUploadFile(image, "photo.jpg", "image/jpeg")]
}, cancellationToken);
```

Up to ten photos are uploaded sequentially through VK's `get upload server` → multipart upload → `save photo` → `send` workflow. Streams remain open by default.

## Publish a post with photos

```csharp
await using var image = File.OpenRead("release.png");
var result = await vk.Wall.PublishAsync(new VkPublishPostRequest
{
    Target = VkWallTarget.Community(groupId: 123456, publishAsCommunity: true),
    Message = "Version 1.0 is available",
    Photos = [new VkUploadFile(image, "release.png", "image/png")]
}, cancellationToken);
```

Use `VkWallTarget.Self` for the current user's wall. Wall photo upload and `wall.post` require a user token even when the target is a managed community.

## Legacy authorization

```csharp
using VK4DotNet.LegacyAuth;

using var legacy = new LegacyVkPasswordAuthenticator(new LegacyVkAuthOptions
{
    ClientId = yourOwnApplicationId,
    ClientSecret = yourOwnApplicationSecret,
    ClientName = "YourApplication",
    UserAgent = "YourApplication/1.0"
});

var result = await legacy.AuthenticateAsync(new LegacyVkAuthRequest
{
    Username = username,
    Password = password
}, cancellationToken);

switch (result)
{
    case LegacyVkAuthSuccess success:
        using (var vk = new VkClient(success.AccessToken.Value)) { /* ... */ }
        break;
    case LegacyVkAuthChallenge challenge:
        Console.WriteLine($"{challenge.Kind}: {challenge.Description}");
        break;
    case LegacyVkAuthFailure failure:
        Console.WriteLine($"{failure.Error}: {failure.Description}");
        break;
}
```

Never log or persist the password. Never reuse another application's client secret or impersonate an official VK client.

## Raw method escape hatch

```csharp
var response = await vk.CallAsync<JsonElement>(
    "users.get",
    new Dictionary<string, string?> { ["user_ids"] = "1" },
    cancellationToken);
```

## Build and test

```sh
dotnet restore VK4DotNet.slnx
dotnet build VK4DotNet.slnx -c Release --no-restore
dotnet test VK4DotNet.slnx -c Release --no-build
dotnet pack VK4DotNet.slnx -c Release --no-build -o artifacts/packages
```

Set `VK4DOTNET_LIVE_USER_TOKEN` to opt into the live read-only integration test. CI and normal local runs do not require real VK credentials.

## License

VK4DotNet is licensed under **GPL-3.0-only**. Applications that distribute or convey a combined work using this library must comply with the GPL, including corresponding-source obligations. See [LICENSE](LICENSE) and [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
