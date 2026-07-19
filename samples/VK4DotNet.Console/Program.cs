using VK4DotNet;
using VK4DotNet.Auth;
using VK4DotNet.LegacyAuth;

if (args.Length == 0)
{
    PrintUsage();
    return;
}

using var cancellation = new CancellationTokenSource();
System.Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

switch (args[0])
{
    case "vkid-url":
        using (var auth = new VkIdAuthClient(new VkIdAuthOptions
        {
            ClientId = long.Parse(RequireEnvironment("VK_CLIENT_ID")),
            RedirectUri = new Uri(RequireEnvironment("VK_REDIRECT_URI"))
        }))
        {
            var session = auth.CreateAuthorizationSession();
            System.Console.WriteLine(session.AuthorizationUri);
            System.Console.WriteLine("Keep CodeVerifier and State only for this authorization attempt; never log them in production.");
        }
        break;

    case "chats":
        using (var vk = CreateTokenClient())
        {
            var page = await vk.Messages.GetConversationsAsync(cancellationToken: cancellation.Token);
            foreach (var item in page.Items)
            {
                System.Console.WriteLine($"{item.Conversation.Peer.Id}: {item.Conversation.Title} ({item.Conversation.UnreadCount} unread)");
            }
        }
        break;

    case "send-photo" when args.Length >= 3:
        using (var vk = CreateTokenClient())
        await using (var stream = File.OpenRead(args[2]))
        {
            var result = await vk.Messages.SendAsync(new VkSendMessageRequest
            {
                PeerId = long.Parse(args[1]),
                Message = args.Length >= 4 ? args[3] : null,
                Photos = [new VkUploadFile(stream, Path.GetFileName(args[2]), GuessContentType(args[2]))]
            }, cancellation.Token);
            System.Console.WriteLine($"Message {result.MessageId} sent.");
        }
        break;

    case "post-photo" when args.Length >= 3:
        using (var vk = CreateTokenClient())
        await using (var stream = File.OpenRead(args[2]))
        {
            var groupId = long.Parse(args[1]);
            var result = await vk.Wall.PublishAsync(new VkPublishPostRequest
            {
                Target = groupId == 0 ? VkWallTarget.Self : VkWallTarget.Community(groupId),
                Message = args.Length >= 4 ? args[3] : null,
                Photos = [new VkUploadFile(stream, Path.GetFileName(args[2]), GuessContentType(args[2]))]
            }, cancellation.Token);
            System.Console.WriteLine($"Post {result.PostId} published.");
        }
        break;

    case "legacy-browser":
        using (var legacy = new LegacyVkBrowserAuthenticator(new LegacyVkBrowserAuthOptions
        {
            ClientId = long.Parse(RequireEnvironment("VK_CLIENT_ID")),
            ClientSecret = RequireEnvironment("VK_CLIENT_SECRET"),
            RedirectUri = new Uri(RequireEnvironment("VK_REDIRECT_URI")),
            UserAgent = RequireEnvironment("VK_USER_AGENT")
        }))
        {
            var session = legacy.CreateAuthorizationSession();
            System.Console.WriteLine("Open this URL in a system browser:");
            System.Console.WriteLine(session.AuthorizationUri);
            System.Console.WriteLine("Paste the complete callback URI. It is handled in memory and is not printed:");
            var callback = System.Console.ReadLine();
            if (string.IsNullOrWhiteSpace(callback))
            {
                throw new InvalidOperationException("A callback URI is required.");
            }

            var token = await legacy.CompleteAsync(new Uri(callback), session, cancellation.Token);
            System.Console.WriteLine($"Authorized user {token.UserId}. Token intentionally not printed.");
        }
        break;

    case "legacy-password":
    case "legacy":
        using (var legacy = new LegacyVkPasswordAuthenticator(new LegacyVkAuthOptions
        {
            ClientId = long.Parse(RequireEnvironment("VK_CLIENT_ID")),
            ClientSecret = RequireEnvironment("VK_CLIENT_SECRET"),
            ClientName = RequireEnvironment("VK_CLIENT_NAME"),
            UserAgent = RequireEnvironment("VK_USER_AGENT")
        }))
        {
            var result = await legacy.AuthenticateAsync(new LegacyVkAuthRequest
            {
                Username = RequireEnvironment("VK_USERNAME"),
                Password = RequireEnvironment("VK_PASSWORD"),
                TwoFactorCode = Environment.GetEnvironmentVariable("VK_2FA_CODE"),
                CaptchaSid = Environment.GetEnvironmentVariable("VK_CAPTCHA_SID"),
                CaptchaKey = Environment.GetEnvironmentVariable("VK_CAPTCHA_KEY")
            }, cancellation.Token);
            System.Console.WriteLine(result switch
            {
                LegacyVkAuthSuccess success => $"Authorized user {success.AccessToken.UserId}. Token intentionally not printed.",
                LegacyVkAuthChallenge challenge => $"Challenge {challenge.Kind}: {challenge.Description}; SID={challenge.Sid}",
                LegacyVkAuthFailure failure => $"Failure {failure.Error}: {failure.Description}",
                _ => "Unknown result."
            });
        }
        break;

    default:
        PrintUsage();
        break;
}

static VkClient CreateTokenClient() => new(RequireEnvironment("VK_USER_TOKEN"));

static string RequireEnvironment(string name) => Environment.GetEnvironmentVariable(name)
    ?? throw new InvalidOperationException($"Set the {name} environment variable.");

static string GuessContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
{
    ".png" => "image/png",
    ".webp" => "image/webp",
    _ => "image/jpeg"
};

static void PrintUsage()
{
    System.Console.WriteLine("Commands:");
    System.Console.WriteLine("  vkid-url");
    System.Console.WriteLine("  chats");
    System.Console.WriteLine("  send-photo <peer-id> <path> [message]");
    System.Console.WriteLine("  post-photo <group-id|0-for-self> <path> [message]");
    System.Console.WriteLine("  legacy-browser");
    System.Console.WriteLine("  legacy-password");
}
