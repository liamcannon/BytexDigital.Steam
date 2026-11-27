using System.Net;
using BytexDigital.Steam.ContentDelivery;
using BytexDigital.Steam.Core;
using BytexDigital.Steam.Core.Structs;
using SteamKit2.CDN;

namespace BytexDigital.Steam.Tests.ContentDelivery;

public class SteamContentClientTests
{
    private static readonly AppId App = new(480);

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

    [Fact]
    public async Task TheSameAppGetsTheSamePool()
    {
        await using var fixture = new ClientFixture();

        var first = await fixture.Client.GetSteamCdnServerPoolAsync(App);
        var second = await fixture.Client.GetSteamCdnServerPoolAsync(App);

        Assert.Same(first, second);
        Assert.Equal(1, fixture.PoolsCreated);
    }

    [Fact]
    public async Task DifferentAppsGetDifferentPools()
    {
        await using var fixture = new ClientFixture();

        var first = await fixture.Client.GetSteamCdnServerPoolAsync(new AppId(480));
        var second = await fixture.Client.GetSteamCdnServerPoolAsync(new AppId(107410));

        Assert.NotSame(first, second);
        Assert.Equal(2, fixture.PoolsCreated);
    }

    [Fact]
    public async Task ConcurrentCallersGetOnePool()
    {
        await using var fixture = new ClientFixture();

        var pools = await Task.WhenAll(
            Enumerable.Range(0, 20)
                .Select(_ => Task.Run(() => fixture.Client.GetSteamCdnServerPoolAsync(App))));

        Assert.All(pools, pool => Assert.Same(pools[0], pool));
        Assert.Equal(1, fixture.PoolsCreated);
    }

    [Fact]
    public async Task ManifestAndChunkLookupsUseTheSamePoolWithIndependentLeases()
    {
        await using var fixture = new ClientFixture();

        // Manifest retrieval and chunk downloads both call BeginPoolOperationAsync
        var (manifestPool, manifestLease) =
            await fixture.Client.BeginPoolOperationAsync(App, CancellationToken.None);
        var (chunkPool, chunkLease) =
            await fixture.Client.BeginPoolOperationAsync(App, CancellationToken.None);

        Assert.Same(manifestPool, chunkPool);
        Assert.Equal(1, fixture.PoolsCreated);

        // Finishing the manifest leaves the pool open for the chunk that is still admitted
        manifestLease.Dispose();

        Assert.Equal(7, await chunkPool.ExecuteCdnOperationAsync(chunkLease, _ => Task.FromResult(7)));

        chunkLease.Dispose();
    }

    [Fact]
    public async Task ADroppedServerStaysExcludedForEveryoneSharingThePool()
    {
        await using var fixture = new ClientFixture();

        var first = await fixture.Client.GetSteamCdnServerPoolAsync(App);
        var dropped = first.GetServerForDownload(allowRecoveryProbe: false);
        first.ReturnServer(dropped, CdnServerDisposition.Failed);

        var second = await fixture.Client.GetSteamCdnServerPoolAsync(App);
        var next = second.GetServerForDownload(allowRecoveryProbe: false);

        Assert.Same(first, second);
        Assert.NotEqual(dropped.Server.Host, next.Server.Host);
    }

    [Fact]
    public async Task DisposingTheClientDisposesItsSharedPools()
    {
        var fixture = new ClientFixture();
        var pool = await fixture.Client.GetSteamCdnServerPoolAsync(App);

        await fixture.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() => pool.BeginCdnOperation());
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => fixture.Client.GetSteamCdnServerPoolAsync(App));
    }

    [Fact]
    public async Task AnExhaustedPoolIsReplacedAndTheOldOneIsDisposed()
    {
        await using var fixture = new ClientFixture();

        var first = await fixture.Client.GetSteamCdnServerPoolAsync(App);
        first.IsExhausted = true;

        var second = await fixture.Client.GetSteamCdnServerPoolAsync(App);

        Assert.NotSame(first, second);
        Assert.Equal(2, fixture.PoolsCreated);

        // The old pool is disposed in the background, so wait for it to start
        await WaitUntilAsync(() => DisposalHasStarted(first));

        // Later lookups keep returning the replacement
        Assert.Same(second, await fixture.Client.GetSteamCdnServerPoolAsync(App));
    }

    [Fact]
    public async Task ReplacingAPoolDoesNotHoldTheLookupLockWhileItDisposes()
    {
        await using var fixture = new ClientFixture();

        var first = await fixture.Client.GetSteamCdnServerPoolAsync(App);

        // An admitted operation keeps the old pool's disposal waiting
        using var lease = first.BeginCdnOperation();
        first.IsExhausted = true;

        // If the lookup waited for the old pool to finish disposing, this would time out
        var second = await fixture.Client.GetSteamCdnServerPoolAsync(App)
            .WaitAsync(TimeSpan.FromSeconds(5));

        // A lookup for a different app must not wait either
        var other = await fixture.Client.GetSteamCdnServerPoolAsync(new AppId(107410))
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotSame(first, second);
        Assert.NotNull(other);

        // The old pool's disposal is still waiting for its admitted operation
        Assert.False(first.DisposeAsync().AsTask().IsCompleted);

        lease.Dispose();

        await first.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static bool DisposalHasStarted(SteamCdnServerPool pool)
    {
        try
        {
            // Admitting an operation fails once disposal has started. Release it straight away if it works.
            pool.BeginCdnOperation().Dispose();
            return false;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);

        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition was not met within 5 seconds.");
            await Task.Delay(10);
        }
    }

    private sealed class ClientFixture : IAsyncDisposable
    {
        private readonly SteamClient _steamClient = new(
            SteamCredentials.Anonymous,
            new ConsoleSteamAuthenticator("tests"));

        private int _poolsCreated;

        public SteamContentClient Client { get; }

        public int PoolsCreated => Volatile.Read(ref _poolsCreated);

        public ClientFixture()
        {
            Client = new SteamContentClient(_steamClient)
            {
                // Pools that never talk to Steam, with two servers each
                CdnPoolFactory = appId =>
                {
                    Interlocked.Increment(ref _poolsCreated);

                    return new SteamCdnServerPool(
                        appId,
                        new[] { CreateServer("cdn-a.example.test"), CreateServer("cdn-b.example.test") },
                        () => DateTime.UtcNow);
                }
            };
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            _steamClient.Dispose();
        }

        private static Server CreateServer(string host) => new DnsEndPoint(host, 443);
    }
}