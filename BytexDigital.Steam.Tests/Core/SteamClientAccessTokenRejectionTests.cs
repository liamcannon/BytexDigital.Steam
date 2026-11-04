using SteamKit2;

namespace BytexDigital.Steam.Tests.Core;

public class SteamClientAccessTokenRejectionTests
{
    [Theory]
    [InlineData(EResult.InvalidSignature)]
    [InlineData(EResult.AccessDenied)]
    [InlineData(EResult.Expired)]
    [InlineData(EResult.Revoked)]
    public void RejectedAccessTokenResultsAreRecognized(EResult result)
    {
        Assert.True(BytexDigital.Steam.Core.SteamClient.ShouldClearAccessToken(true, result));
    }

    [Theory]
    [InlineData(EResult.InvalidPassword)]
    [InlineData(EResult.NoConnection)]
    [InlineData(EResult.Timeout)]
    [InlineData(EResult.ServiceUnavailable)]
    [InlineData(EResult.TryAnotherCM)]
    [InlineData(EResult.OK)]
    public void UnrelatedResultsAreNotRecognizedAsTokenRejections(EResult result)
    {
        Assert.False(BytexDigital.Steam.Core.SteamClient.ShouldClearAccessToken(true, result));
    }

    [Theory]
    [InlineData(EResult.InvalidSignature)]
    [InlineData(EResult.AccessDenied)]
    [InlineData(EResult.Expired)]
    [InlineData(EResult.Revoked)]
    public void RejectedResultsDoNotClearWhenNoAccessTokenWasUsed(EResult result)
    {
        Assert.False(BytexDigital.Steam.Core.SteamClient.ShouldClearAccessToken(false, result));
    }
}