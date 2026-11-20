using BytexDigital.Steam.ContentDelivery;
using BytexDigital.Steam.Core;

namespace BytexDigital.Steam.Tests.ContentDelivery;

public class SteamContentClientTests
{
    [Fact]
    public async Task CancellationTokenCanBeReadAfterDisposeAndIsCancelled()
    {
        using var steamClient = new SteamClient(
            SteamCredentials.Anonymous,
            new ConsoleSteamAuthenticator("tests"));

        var contentClient = new SteamContentClient(steamClient);

        var before = contentClient.CancellationToken;
        Assert.False(before.IsCancellationRequested);

        await contentClient.DisposeAsync();

        // Reading the property after disposal must not throw.
        var after = contentClient.CancellationToken;

        Assert.True(after.IsCancellationRequested);
        Assert.True(before.IsCancellationRequested);
    }
}