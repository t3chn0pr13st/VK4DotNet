namespace VK4DotNet.Tests;

public sealed class LiveIntegrationTests
{
    [LiveFact]
    [Trait("Category", "Live")]
    public async Task Can_read_conversations_when_token_is_supplied()
    {
        var token = Environment.GetEnvironmentVariable("VK4DOTNET_LIVE_USER_TOKEN")!;

        using var client = new VkClient(token);
        var page = await client.Messages.GetConversationsAsync(new VkGetConversationsRequest(Count: 1));
        Assert.True(page.TotalCount >= page.Items.Count);
    }
}

internal sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VK4DOTNET_LIVE_USER_TOKEN")))
        {
            Skip = "Set VK4DOTNET_LIVE_USER_TOKEN to run live VK integration tests.";
        }
    }
}
