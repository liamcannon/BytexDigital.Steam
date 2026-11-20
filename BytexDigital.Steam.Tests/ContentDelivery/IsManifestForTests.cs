using BytexDigital.Steam.ContentDelivery;
using BytexDigital.Steam.ContentDelivery.Models;

namespace BytexDigital.Steam.Tests.ContentDelivery;

public class IsManifestForTests
{
    private static Manifest NewManifest(uint depotId, ulong manifestId) =>
        new(new List<ManifestFile>(), depotId, manifestId, DateTime.UtcNow, 0, 0);

    [Fact]
    public void MatchingDepotAndManifestAreReused()
    {
        Assert.True(SteamContentClient.IsManifestFor(NewManifest(107410u, 5ul), 107410u, 5ul));
    }

    [Fact]
    public void DifferentDepotIsNotReused()
    {
        Assert.False(SteamContentClient.IsManifestFor(NewManifest(107410u, 5ul), 233781u, 5ul));
    }

    [Fact]
    public void DifferentManifestIsNotReused()
    {
        Assert.False(SteamContentClient.IsManifestFor(NewManifest(107410u, 5ul), 107410u, 6ul));
    }

    [Fact]
    public void NoManifestIsNotReused()
    {
        Assert.False(SteamContentClient.IsManifestFor(null, 107410u, 5ul));
    }
}