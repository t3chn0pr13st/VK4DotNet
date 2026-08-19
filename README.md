# VK4DotNet

[Русская версия](README.ru.md)

VK4DotNet is an asynchronous .NET 10 client for the VK API. Version 1.1 focuses on personal conversations, photo messages, photo posts, VK ID, and hosted legacy browser authorization.

The API and model design are based on the GPL-licensed [VK4ME/client](https://github.com/VK4ME/client) and its J2VK library, updated for the official VK API 5.199 schema. VK4DotNet does not contain the embedded credentials, refresh token, or impersonated official-client User-Agent found in the historical J2ME code.

> [!IMPORTANT]
> The current official VK ID scope list does not include `messages`. Reading and sending personal messages therefore requires a compatible externally supplied user token or the explicitly opt-in `VK4DotNet.LegacyAuth` package. Legacy browser and password flows may be unavailable for your application or account and can stop working without notice. Requesting `messages` succeeds only for applications to which VK has granted that right.

## Packages

- `VK4DotNet` — API client, immutable models, photo uploads, wall publishing, and VK ID OAuth 2.1 with PKCE.
- `VK4DotNet.LegacyAuth` — hosted legacy browser OAuth plus an isolated password fallback with CAPTCHA, 2FA, and SMS challenge results.

Packages are attached to [GitHub Releases](https://github.com/t3chn0pr13st/VK4DotNet/releases), not published to NuGet.org. Download the `.nupkg` files, put them in a local package source, and install them:

```sh
dotnet nuget add source /path/to/packages --name VK4DotNetLocal
dotnet add package VK4DotNet --version 1.1.0
dotnet add package VK4DotNet.LegacyAuth --version 1.1.0
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
    Message = "Version 1.1 is available",
    Photos = [new VkUploadFile(image, "release.png", "image/png")]
}, cancellationToken);
```

Use `VkWallTarget.Self` for the current user's wall. Wall photo upload and `wall.post` require a user token even when the target is a managed community.

## Hosted legacy browser authorization

The browser authenticator is preferred over direct password authorization. VK owns every phone, confirmation-code, password, CAPTCHA, passkey, and consent screen; the library sees only the final OAuth callback. The consuming application must open the URL and capture its registered redirect URI.

```csharp
using VK4DotNet.LegacyAuth;

using var legacy = new LegacyVkBrowserAuthenticator(new LegacyVkBrowserAuthOptions
{
    ClientId = yourOwnApplicationId,
    ClientSecret = yourOwnApplicationSecret,
    RedirectUri = new Uri("https://example.test/vk/legacy-callback"),
    UserAgent = "YourApplication/1.0"
});

var session = legacy.CreateAuthorizationSession();
// Open session.AuthorizationUri in the system browser. Keep the session only
// for this attempt, then pass the complete callback URI back to the library.
var token = await legacy.CompleteAsync(callbackUri, session, cancellationToken);
using var vk = new VkClient(token.Value);
```

`AuthorizationCode` is the default and keeps the access token out of the callback URI. It requires the secret belonging to your own VK application. Legacy native applications that cannot use code exchange may explicitly select `LegacyVkBrowserFlow.AccessToken`; in that mode the token is returned in the URI fragment, which an HTTP callback server cannot receive. The host must capture the complete fragment locally and must never log it.

Both modes generate and validate a cryptographically random `state`, verify the callback location, and request `messages,photos,wall,groups,offline` by default. Browser authorization does not bypass VK's application-level restriction on the `messages` scope.

## Direct legacy password fallback

Direct password authorization is retained only as a compatibility fallback. It exposes the account password to the consuming process and is not designed for every modern multi-step VK login path. Prefer `LegacyVkBrowserAuthenticator` whenever the application can receive a browser callback.

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

## Выпуск пакета — локально, не через CI

**GitHub Actions у аккаунта не выполняются: биллинг выключен и включать его не планируется.**
Workflow здесь падает, не начав работу, поэтому пакет собирается и выпускается с машины
разработчика:

```bash
dotnet test VK4DotNet.slnx -c Release
dotnet pack VK4DotNet.slnx -c Release -o artifacts/packages -p:Version=X.Y.Z
gh release create vX.Y.Z artifacts/packages/*.nupkg --generate-notes
```

Packages are attached to GitHub Releases, so a release is the delivery: consumers download the
`.nupkg` and add it to a local package source. `dotnet pack` is not deterministic — repacking the
same version yields a different SHA-256 — so publish exactly the file you built, and never repack
a version that someone already pinned.
