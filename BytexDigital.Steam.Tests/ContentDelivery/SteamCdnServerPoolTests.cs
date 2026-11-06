using System.Net;
using BytexDigital.Steam.ContentDelivery;
using BytexDigital.Steam.Core;
using BytexDigital.Steam.Core.Structs;
using Microsoft.Extensions.Logging;
using SteamKit2.CDN;


namespace BytexDigital.Steam.Tests.ContentDelivery;

public class SteamCdnServerPoolTests
{
    private static readonly DateTime Start =
        new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static readonly TimeSpan InitialRecoveryProbeDelay =
        TimeSpan.FromSeconds(5);

    [Fact]
    public void RequestsPopulationWhenPoolIsLowAndAlreadyPopulated()
    {
        using var fixture = new PoolFixture();
        var logger = new RecordingLogger();

        fixture.Pool.Logger = logger;

        fixture.Pool.GetServerForDownload(allowRecoveryProbe: false);

        Assert.Contains(
            logger.Messages,
            message => message.Contains("Set populatePoolEvent"));
    }

    [Fact]
    public void DoesNotRequestPopulationWhilePopulationIsInProgress()
    {
        using var fixture = new PoolFixture(isPopulated: false);
        var logger = new RecordingLogger();

        fixture.Pool.Logger = logger;

        fixture.Pool.GetServerForDownload(allowRecoveryProbe: false);

        Assert.DoesNotContain(
            logger.Messages,
            message => message.Contains("Set populatePoolEvent"));
    }

    [Fact]
    public void DoesNotRequestPopulationWhenPoolIsAtMinimumSize()
    {
        using var fixture = new PoolFixture(serverCount: 10);
        var logger = new RecordingLogger();

        fixture.Pool.Logger = logger;

        fixture.Pool.GetServerForDownload(allowRecoveryProbe: false);

        Assert.DoesNotContain(
            logger.Messages,
            message => message.Contains("Set populatePoolEvent"));
    }

    [Fact]
    public async Task DisposeDisposesOwnedCdnHttpClient()
    {
        var handler = new TrackingHttpMessageHandler();

        using var steamClient = new SteamClient(
            SteamCredentials.Anonymous,
            new ConsoleSteamAuthenticator("tests"),
            builder => builder.WithHttpClientFactory(
                _ => new HttpClient(handler)));

        await using var contentClient = new SteamContentClient(steamClient);

        var pool = new SteamCdnServerPool(contentClient, new AppId(480));

        Assert.False(handler.IsDisposed);

        pool.Dispose();

        Assert.True(handler.IsDisposed);
    }

    [Fact]
    public async Task DisposeWaitsForActiveCdnOperations()
    {
        using var fixture = new PoolFixture();

        var operationStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var completeOperation = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var operation = fixture.Pool.ExecuteCdnOperationAsync(
            async _ =>
            {
                operationStarted.TrySetResult(true);
                return await completeOperation.Task;
            });

        await operationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var disposal = fixture.Pool.DisposeAsync().AsTask();

        Assert.False(disposal.IsCompleted);

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => fixture.Pool.ExecuteCdnOperationAsync<int>(
                _ => Task.FromResult(1)));

        completeOperation.SetResult(42);

        Assert.Equal(42, await operation);
        await disposal;
    }

    [Fact]
    public async Task CallerCancellationDoesNotReleaseActiveCdnOperation()
    {
        using var fixture = new PoolFixture();
        using var cancellation = new CancellationTokenSource();

        var operationStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var completeOperation = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var operation = fixture.Pool.ExecuteCdnOperationAsync(
            async _ =>
            {
                operationStarted.TrySetResult(true);
                return await completeOperation.Task;
            });

        await operationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var callerWait = operation.WaitAsync(cancellation.Token);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => callerWait);

        var disposal = fixture.Pool.DisposeAsync().AsTask();

        Assert.False(disposal.IsCompleted);

        completeOperation.SetResult(42);

        await operation;
        await disposal;
    }

    [Fact]
    public void ReturnsInitiallyHealthyServers()
    {
        using var fixture = new PoolFixture();

        var first = fixture.Pool.GetServerForDownload(allowRecoveryProbe: false);
        var second = fixture.Pool.GetServerForDownload(allowRecoveryProbe: false);

        Assert.Equal(FirstHost, first.Server.Host);
        Assert.Equal(SecondHost, second.Server.Host);
    }

    [Fact]
    public void DoesNotSelectDroppedServerDuringNormalSelection()
    {
        using var fixture = new PoolFixture();

        var first = fixture.Pool.GetServerForDownload(allowRecoveryProbe: false);
        fixture.Pool.ReturnServer(first, CdnServerDisposition.Failed);

        var next = fixture.Pool.GetServerForDownload(allowRecoveryProbe: false);

        Assert.Equal(SecondHost, next.Server.Host);
    }

    [Fact]
    public void SelectsDroppedServerForRecoveryAfterInitialDelay()
    {
        using var fixture = new PoolFixture();

        var first = fixture.Pool.GetServerForDownload(allowRecoveryProbe: false);
        fixture.Pool.ReturnServer(first, CdnServerDisposition.Failed);

        fixture.Advance(InitialRecoveryProbeDelay - TimeSpan.FromTicks(1));

        var healthy = fixture.Pool.GetServerForDownload(allowRecoveryProbe: false);
        Assert.Equal(SecondHost, healthy.Server.Host);

        fixture.Pool.ReturnServer(healthy, CdnServerDisposition.Abandoned);

        fixture.Advance(TimeSpan.FromTicks(1));

        var recovery = fixture.Pool.GetServerForDownload();

        Assert.Equal(FirstHost, recovery.Server.Host);
        Assert.True(recovery.RecoveryProbe.HasValue);
    }

    [Fact]
    public void AllowsOnlyOneActiveRecoveryProbePerServer()
    {
        using var fixture = new PoolFixture();

        var first = fixture.Pool.GetServerForDownload(allowRecoveryProbe: false);
        fixture.Pool.ReturnServer(first, CdnServerDisposition.Failed);

        fixture.Advance(InitialRecoveryProbeDelay);

        var firstProbe = fixture.Pool.GetServerForDownload();
        var secondSelection = fixture.Pool.GetServerForDownload();

        Assert.Equal(FirstHost, firstProbe.Server.Host);
        Assert.True(firstProbe.RecoveryProbe.HasValue);

        Assert.Equal(SecondHost, secondSelection.Server.Host);
        Assert.False(secondSelection.RecoveryProbe.HasValue);
    }

    [Fact]
    public void SuccessfulRecoveryMakesServerAvailableAgain()
    {
        using var fixture = new PoolFixture();

        var first = fixture.Pool.GetServerForDownload(allowRecoveryProbe: false);
        fixture.Pool.ReturnServer(first, CdnServerDisposition.Failed);

        fixture.Advance(InitialRecoveryProbeDelay);

        var recovery = fixture.Pool.GetServerForDownload();

        Assert.True(recovery.RecoveryProbe.HasValue);

        fixture.Pool.ReturnServer(recovery, CdnServerDisposition.Succeeded);

        var available = fixture.Pool.GetServerForDownload(allowRecoveryProbe: false);

        Assert.Equal(FirstHost, available.Server.Host);
        Assert.False(available.RecoveryProbe.HasValue);
    }

    [Fact]
    public void FailedRecoveryKeepsServerExcluded()
    {
        using var fixture = new PoolFixture();

        var first = fixture.Pool.GetServerForDownload(allowRecoveryProbe: false);
        fixture.Pool.ReturnServer(first, CdnServerDisposition.Failed);

        fixture.Advance(InitialRecoveryProbeDelay);

        var recovery = fixture.Pool.GetServerForDownload();
        fixture.Pool.ReturnServer(recovery, CdnServerDisposition.Failed);

        // The failed recovery probe must not immediately make the server selectable again.
        var next = fixture.Pool.GetServerForDownload();

        Assert.Equal(SecondHost, next.Server.Host);
        Assert.False(next.RecoveryProbe.HasValue);
    }

    [Fact]
    public void AbandonedRecoveryProbeCanBeRetriedWithoutBackoff()
    {
        using var fixture = new PoolFixture();

        var first = fixture.Pool.GetServerForDownload(allowRecoveryProbe: false);
        fixture.Pool.ReturnServer(first, CdnServerDisposition.Failed);

        fixture.Advance(InitialRecoveryProbeDelay);

        var firstProbe = fixture.Pool.GetServerForDownload();
        Assert.True(firstProbe.RecoveryProbe.HasValue);

        fixture.Pool.ReturnServer(firstProbe, CdnServerDisposition.Abandoned);

        var secondProbe = fixture.Pool.GetServerForDownload();

        Assert.Equal(FirstHost, secondProbe.Server.Host);
        Assert.True(secondProbe.RecoveryProbe.HasValue);
    }

    [Fact]
    public void EntersExhaustionFallbackWhenEveryKnownServerIsDropped()
    {
        using var fixture = new PoolFixture();

        var first = fixture.Pool.GetServerForDownload(allowRecoveryProbe: false);
        fixture.Pool.ReturnServer(first, CdnServerDisposition.Failed);

        var second = fixture.Pool.GetServerForDownload(allowRecoveryProbe: false);
        fixture.Pool.ReturnServer(second, CdnServerDisposition.Failed);

        Assert.True(fixture.Pool.IsInExhaustionFallback);

        var fallbackFirst = fixture.Pool.GetServerForDownload(allowRecoveryProbe: false);
        var fallbackSecond = fixture.Pool.GetServerForDownload(allowRecoveryProbe: false);

        var selectedHosts = new[]
        {
            fallbackFirst.Server.Host,
            fallbackSecond.Server.Host
        };

        Assert.Contains(FirstHost, selectedHosts);
        Assert.Contains(SecondHost, selectedHosts);
    }

    [Fact]
    public void SuccessfulFallbackRequestReturnsProvenServerAndDisablesFallback()
    {
        using var fixture = new PoolFixture();

        var first = fixture.Pool.GetServerForDownload(allowRecoveryProbe: false);
        fixture.Pool.ReturnServer(first, CdnServerDisposition.Failed);

        var second = fixture.Pool.GetServerForDownload(allowRecoveryProbe: false);
        fixture.Pool.ReturnServer(second, CdnServerDisposition.Failed);

        Assert.True(fixture.Pool.IsInExhaustionFallback);

        var fallback = fixture.Pool.GetServerForDownload(allowRecoveryProbe: false);

        fixture.Pool.ReturnServer(fallback, CdnServerDisposition.Succeeded);

        Assert.False(fixture.Pool.IsInExhaustionFallback);

        var recovered = fixture.Pool.GetServerForDownload(allowRecoveryProbe: false);

        Assert.Equal(fallback.Server.Host, recovered.Server.Host);
        Assert.False(recovered.RecoveryProbe.HasValue);
    }

    private sealed class TrackingHttpMessageHandler : HttpMessageHandler
    {
        public bool IsDisposed { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException(
                "This test does not send HTTP requests.");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                IsDisposed = true;
            }

            base.Dispose(disposing);
        }
    }

    private const string FirstHost = "cdn-first.example.test";
    private const string SecondHost = "cdn-second.example.test";

    private sealed class PoolFixture : IDisposable
    {
        private DateTime _utcNow = Start;

        public Server FirstServer { get; } = CreateServer(FirstHost);
        public Server SecondServer { get; } = CreateServer(SecondHost);

        public SteamCdnServerPool Pool { get; }

        public PoolFixture(bool isPopulated = true, int serverCount = 2)
        {
            var servers = new List<Server>
            {
                FirstServer,
                SecondServer
            };

            for (var i = servers.Count; i < serverCount; i++)
            {
                servers.Add(CreateServer($"cdn-extra-{i}.example.test"));
            }

            Pool = new SteamCdnServerPool(
                new AppId(480),
                servers,
                () => _utcNow,
                isPopulated);
        }

        public void Advance(TimeSpan duration)
        {
            _utcNow = _utcNow.Add(duration);
        }

        public void Dispose()
        {
            Pool.Dispose();
        }

        private static Server CreateServer(string host)
        {
            // SteamKit exposes a public DnsEndPoint conversion for creating test servers.
            return new DnsEndPoint(host, 443);
        }
    }
    private sealed class RecordingLogger : ILogger
    {
        public List<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }
    }
}