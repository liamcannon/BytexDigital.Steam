using BytexDigital.Steam.ContentDelivery.Exceptions;
using BytexDigital.Steam.Core;
using BytexDigital.Steam.Core.Exceptions;

namespace BytexDigital.Steam.Tests.Core;

public class SteamClientTests : IDisposable
{
    private readonly List<SteamClient> _clients = new();

    public void Dispose()
    {
        foreach (var client in _clients)
        {
            client.Shutdown();
        }
    }

    private SteamClient Create(SteamCredentials? credentials = null)
    {
        var client = new SteamClient(
            credentials ?? SteamCredentials.Anonymous,
            new ConsoleSteamAuthenticator("tests"));
        _clients.Add(client);
        return client;
    }

    [Fact]
    public void NewClientIsCreatedAndNotConnected()
    {
        var credentials = new SteamCredentials("user", "password");
        var client = Create(credentials);

        Assert.Equal(SteamClient.SteamClientState.Created, client.State);
        Assert.False(client.IsConnected);
        Assert.False(client.IsFaulted);
        Assert.Null(client.FaultException);
        Assert.Same(credentials, client.Credentials);
        Assert.False(client.CancellationToken.IsCancellationRequested);
    }

    [Fact]
    public void MissingCredentialsMeanAnonymous()
    {
        var client = new SteamClient(null!, new ConsoleSteamAuthenticator("tests"));
        _clients.Add(client);

        Assert.True(client.Credentials.IsAnonymous);
    }

    [Fact]
    public void MissingAuthenticatorIsRejected()
    {
        Assert.Throws<SteamAuthenticatorRequiredException>(
            () => new SteamClient(SteamCredentials.Anonymous, null!));
    }

    [Fact]
    public void ShutdownMarksTheClientShutDownAndFaulted()
    {
        var client = Create();

        client.Shutdown();

        Assert.Equal(SteamClient.SteamClientState.ShutDown, client.State);
        Assert.True(client.IsFaulted);
        Assert.IsType<SteamClientDisposedException>(client.FaultException);
        Assert.False(client.IsConnected);
        Assert.True(client.CancellationToken.IsCancellationRequested);
    }

    [Fact]
    public void ShutdownAndDisposeCanBeRepeated()
    {
        var client = Create();

        client.Shutdown();
        var firstFault = client.FaultException;
        client.Dispose();
        client.Shutdown();

        Assert.Same(firstFault, client.FaultException);
    }

    [Fact]
    public async Task ConnectingAfterShutdownReportsAFaultedClient()
    {
        var client = Create();
        client.Shutdown();

        var exception = await Assert.ThrowsAsync<SteamClientFaultedException>(() => client.ConnectAsync());

        Assert.IsType<SteamClientDisposedException>(exception.InnerException);
    }

    [Fact]
    public async Task AwaitingReadyAfterShutdownReportsAFaultedClient()
    {
        var client = Create();
        client.Shutdown();

        await Assert.ThrowsAsync<SteamClientFaultedException>(() => client.AwaitReadyAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AwaitingReadyWithACancelledTokenThrowsCancellation()
    {
        var client = Create();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.AwaitReadyAsync(cancellation.Token));
    }
}