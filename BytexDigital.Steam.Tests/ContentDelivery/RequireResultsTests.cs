using BytexDigital.Steam.ContentDelivery;

namespace BytexDigital.Steam.Tests.ContentDelivery;

public class RequireResultsTests
{
    [Fact]
    public void MissingResultsGiveADescriptiveException()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => SteamContentClient.RequireResults<int>(null, "app"));

        Assert.Contains("app", ex.Message);
    }

    [Fact]
    public void ResultsAreReturnedAsTheyAre()
    {
        var results = new List<int> { 1, 2 };

        Assert.Same(results, SteamContentClient.RequireResults(results, "app"));
    }
}