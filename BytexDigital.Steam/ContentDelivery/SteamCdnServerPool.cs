using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using BytexDigital.Steam.ContentDelivery.Exceptions;
using BytexDigital.Steam.Core.Structs;
using Microsoft.Extensions.Logging;
using Nito.AsyncEx;
using SteamKit2;
using SteamKit2.CDN;

namespace BytexDigital.Steam.ContentDelivery
{
    public class SteamCdnServerPool : IDisposable, IAsyncDisposable
    {
        private const int MinimumPoolSize = 10;
        private const int MaxThrottleDelaySeconds = 60;
        private static readonly TimeSpan PopulateCheckInterval = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan InitialRecoveryProbeDelay = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan MaximumRecoveryProbeDelay = TimeSpan.FromSeconds(60);

        private readonly ConcurrentStack<Server> _activeServerEndpoints = new();
        private readonly DroppedServerTracker _droppedServers;
        private readonly AppId _appId;
        private readonly BlockingCollection<Server> _availableServerEndpoints = new();
        private readonly CancellationTokenSource _cancellationTokenSource;
        private readonly CancellationToken _token;
        private readonly AsyncManualResetEvent _populatedEvent = new(false);
        private readonly AsyncAutoResetEvent _populatePoolEvent = new(true);
        private readonly SteamContentClient _steamContentClient;
        private readonly Task? _monitorTask;

        private readonly object _cdnOperationLock = new();
        private int _activeCdnOperations;
        private bool _disposeStarted;
        private TaskCompletionSource<bool>? _cdnOperationsDrained;
        private Task? _disposeTask;

        // The latest server-directory snapshot is also used as the source of recovery candidates.
        private Server[] _knownServerEndpoints = Array.Empty<Server>();
        private long _recoveryProbeCursor;

        // 1 means the pool has no known healthy server and must allow dropped servers to be selected.
        private int _exhaustionFallback;

        public ILogger? Logger { get; set; }
        public Client CdnClient { get; }
        public Server? DesignatedProxyServer { get; private set; }
        public bool IsExhausted { get; private set; }
        internal bool IsInExhaustionFallback => Volatile.Read(ref _exhaustionFallback) != 0;

        public SteamCdnServerPool(
            SteamContentClient steamContentClient,
            AppId appId,
            CancellationToken cancellationToken = default)
        {
            _steamContentClient = steamContentClient;
            _appId = appId;
            _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _token = _cancellationTokenSource.Token;

            _droppedServers = new DroppedServerTracker(
                InitialRecoveryProbeDelay,
                MaximumRecoveryProbeDelay);

            CdnClient = new Client(_steamContentClient.SteamClient.InternalClient);

            // MonitorAsync handles its own errors, so it is safe to leave running on its own.
            _monitorTask = Task.Run(MonitorAsync);
        }

        // <summary>
        //   Creates a pre-populated pool for deterministic server-selection tests.
        //   Does not start the background monitor or connect to Steam.
        // </summary>
        internal SteamCdnServerPool(
            AppId appId,
            IEnumerable<Server> initialServers,
            Func<DateTime> utcNow,
            bool isPopulated = true)
        {
            ArgumentNullException.ThrowIfNull(initialServers);
            ArgumentNullException.ThrowIfNull(utcNow);

            // These are not used because the test pool does not start MonitorAsync
            // or issue actual CDN requests.
            _steamContentClient = null!;
            CdnClient = null!;

            _appId = appId;
            _cancellationTokenSource = new CancellationTokenSource();
            _token = _cancellationTokenSource.Token;

            _droppedServers = new DroppedServerTracker(
                InitialRecoveryProbeDelay,
                MaximumRecoveryProbeDelay,
                utcNow);

            _knownServerEndpoints = initialServers.ToArray();

            foreach (var server in _knownServerEndpoints)
            {
                _availableServerEndpoints.Add(server);
            }

            // The supplied snapshot is already populated unless a test specifies otherwise.
            if (isPopulated)
            {
                _populatedEvent.Set();
            }
        }

        public void Dispose()
        {
            DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        public ValueTask DisposeAsync()
        {
            TaskCompletionSource<bool> completion;
            Task activeOperationsDrained;

            lock (_cdnOperationLock)
            {
                if (_disposeTask != null)
                {
                    return new ValueTask(_disposeTask);
                }

                _disposeStarted = true;

                activeOperationsDrained = _activeCdnOperations == 0
                    ? Task.CompletedTask
                    : (_cdnOperationsDrained ??= new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously)).Task;

                completion = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);

                _disposeTask = completion.Task;
            }

            _ = DisposeCoreAsync(activeOperationsDrained, completion);

            return new ValueTask(completion.Task);
        }

        private async Task DisposeCoreAsync(
            Task activeOperationsDrained,
            TaskCompletionSource<bool> completion)
        {
            try
            {
                Close();

                // Wait for active CDN requests and the background monitor to finish
                // before disposing resources used by the pool.
                await Task.WhenAll(
                    activeOperationsDrained,
                    _monitorTask ?? Task.CompletedTask)
                    .ConfigureAwait(false);

                try
                {
                    CdnClient?.Dispose();
                }
                finally
                {
                    _cancellationTokenSource.Dispose();
                }

                completion.TrySetResult(true);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        }

        public void Close()
        {
            _cancellationTokenSource.Cancel();
        }

        /// <summary>
        ///      Executes a CDN operation while tracking its active lifetime.
        /// </summary>
        /// <remarks>
        ///      Tracking remains active until the underlying operation completes,
        ///      even if the caller stops awaiting its result.
        /// </remarks>
        internal async Task<TResult> ExecuteCdnOperationAsync<TResult>(
            Func<Client, Task<TResult>> operation)
        {
            ArgumentNullException.ThrowIfNull(operation);

            lock (_cdnOperationLock)
            {
                ObjectDisposedException.ThrowIf(_disposeStarted, this);
                _activeCdnOperations++;
            }

            try
            {
                return await operation(CdnClient).ConfigureAwait(false);
            }
            finally
            {
                TaskCompletionSource<bool>? operationsDrained = null;

                lock (_cdnOperationLock)
                {
                    _activeCdnOperations--;

                    if (_disposeStarted && _activeCdnOperations == 0)
                    {
                        operationsDrained = _cdnOperationsDrained;
                    }
                }

                operationsDrained?.TrySetResult(true);
            }
        }

        /// <summary>
        ///     Gets a server for callers using the public pool API. Internal download paths should use
        ///     GetServerForDownload so a dropped server selected for recovery can carry its probe lease.
        /// </summary>
        public Server GetServer(CancellationToken cancellationToken = default)
        {
            return GetServerCore(cancellationToken, allowRecoveryProbe: false).Server;
        }

        /// <summary>
        ///     Gets a server for an actual CDN operation. When healthy servers exist, an eligible dropped
        ///     server may be selected as a recovery probe. During exhaustion fallback, all known servers
        ///     are eligible for ordinary attempts regardless of their dropped state.
        /// </summary>
        internal CdnServerLease GetServerForDownload(
            CancellationToken cancellationToken = default,
            bool allowRecoveryProbe = true)
        {
            return GetServerCore(cancellationToken, allowRecoveryProbe);
        }

        private CdnServerLease GetServerCore(CancellationToken cancellationToken, bool allowRecoveryProbe)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Recovery uses the next real download operation as its probe. This avoids synthetic pings
                // and tests the endpoint with the actual depot/manifest/chunk and authentication context.
                if (allowRecoveryProbe && !IsInExhaustionFallback &&
                    TryGetRecoveryProbe(out var probeServer, out var probeLease))
                {
                    return new CdnServerLease(probeServer, probeLease);
                }

                Server? server;
                if (!_activeServerEndpoints.TryPop(out server))
                {
                    if (_availableServerEndpoints.Count < MinimumPoolSize && _populatedEvent.IsSet)
                    {
                        Logger?.LogTrace("GetServer: Set populatePoolEvent");
                        _populatePoolEvent.Set();
                    }

                    // Stops waiting when the caller cancels OR when the pool is closed or gives up.
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _token);

                    try
                    {
                        server = _availableServerEndpoints.Take(linked.Token);
                    }
                    catch (OperationCanceledException) when (IsExhausted && !cancellationToken.IsCancellationRequested)
                    {
                        throw new SteamNoContentServerFoundException(_appId);
                    }
                }

                if (server == null)
                {
                    continue;
                }

                var key = ServerKey(server);
                var isDropped = _droppedServers.IsDropped(key);

                // Old copies can remain in either collection after a server is dropped. In normal mode
                // they must be skipped. In exhaustion fallback, trying them again is intentional.
                if (isDropped && !IsInExhaustionFallback)
                {
                    // A drop may have raced with the first probe check above. Try to acquire its lease now.
                    if (allowRecoveryProbe && _droppedServers.TryBeginRecoveryProbe(key, out var lateProbeLease))
                    {
                        return new CdnServerLease(server, lateProbeLease);
                    }

                    continue;
                }

                RecoveryProbeLease? fallbackProbeLease = null;
                if (isDropped && allowRecoveryProbe &&
                    _droppedServers.TryBeginRecoveryProbe(key, out var currentProbeLease))
                {
                    // A fallback selection is still allowed when a probe is not due. If it is due, attach
                    // the lease so a failed real attempt advances the probe backoff correctly.
                    fallbackProbeLease = currentProbeLease;
                }

                return new CdnServerLease(server, fallbackProbeLease);
            }
        }

        private bool TryGetRecoveryProbe(out Server server, out RecoveryProbeLease lease)
        {
            var candidates = Volatile.Read(ref _knownServerEndpoints)
                .OrderBy(x => x.WeightedLoad)
                .ToArray();

            if (candidates.Length == 0)
            {
                server = null!;
                lease = default;
                return false;
            }

            // Rotate the first candidate so a repeatedly failing low-load endpoint cannot starve
            // other dropped endpoints that are also due for a recovery attempt.
            var start = (int) ((ulong) Interlocked.Increment(ref _recoveryProbeCursor) % (ulong) candidates.Length);

            for (var offset = 0; offset < candidates.Length; offset++)
            {
                var candidate = candidates[(start + offset) % candidates.Length];
                var key = ServerKey(candidate);
                if (!_droppedServers.TryBeginRecoveryProbe(key, out lease))
                {
                    continue;
                }

                // Exhaustion may have started while the probe lease was being acquired. In that case
                // leave the lease available for normal fallback selection instead.
                if (IsInExhaustionFallback)
                {
                    _droppedServers.CancelRecoveryProbe(lease);
                    continue;
                }

                server = candidate;
                return true;
            }

            server = null!;
            lease = default;
            return false;
        }

        /// <summary>
        ///     Compatibility API for callers that do not use recovery leases.
        /// </summary>
        public void ReturnServer(Server server, bool isFaulty)
        {
            var key = ServerKey(server);

            if (isFaulty)
            {
                _droppedServers.Drop(key);
                Logger?.LogDebug("CDN server {Host} ({Type}) marked unhealthy.", server.Host, server.Type);
                UpdateExhaustionFallback();
                _populatePoolEvent.Set();
                return;
            }

            _droppedServers.MarkHealthy(key);
            DisableExhaustionFallback();
            _activeServerEndpoints.Push(server);
        }

        /// <summary>
        ///     Returns a server selected for a download and records the outcome of the real CDN operation.
        ///     Abandoned operations (for example caller cancellation or an error before the CDN request)
        ///     do not mark a dropped server healthy or increase its failure count.
        /// </summary>
        internal void ReturnServer(CdnServerLease lease, CdnServerDisposition disposition)
        {
            var server = lease.Server;
            var key = ServerKey(server);

            switch (disposition)
            {
                case CdnServerDisposition.Succeeded:
                    if (lease.RecoveryProbe.HasValue)
                    {
                        _droppedServers.CompleteRecoveryProbe(lease.RecoveryProbe.Value, succeeded: true);
                    }
                    else
                    {
                        _droppedServers.MarkHealthy(key);
                    }

                    // Any successful real CDN operation is enough to leave exhaustion fallback.
                    DisableExhaustionFallback();
                    _activeServerEndpoints.Push(server);
                    break;

                case CdnServerDisposition.Failed:
                    if (lease.RecoveryProbe.HasValue)
                    {
                        _droppedServers.CompleteRecoveryProbe(lease.RecoveryProbe.Value, succeeded: false);
                    }
                    else
                    {
                        _droppedServers.Drop(key);
                    }

                    UpdateExhaustionFallback();
                    _populatePoolEvent.Set();
                    break;

                case CdnServerDisposition.Abandoned:
                    if (lease.RecoveryProbe.HasValue)
                    {
                        _droppedServers.CancelRecoveryProbe(lease.RecoveryProbe.Value);
                    }

                    // A healthy server can safely be returned after cancellation. A dropped server is
                    // only requeued when exhaustion fallback explicitly permits trying it again.
                    if (!_droppedServers.IsDropped(key) || IsInExhaustionFallback)
                    {
                        _activeServerEndpoints.Push(server);
                    }

                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(disposition), disposition, null);
            }
        }

        // The same host can appear as more than one Server object, so servers are told apart by type and host.
        private static string ServerKey(Server server) => $"{server.Type}:{server.Host}";

        private void EnableExhaustionFallback(IEnumerable<Server> candidates, bool enqueueCandidates)
        {
            if (Interlocked.CompareExchange(ref _exhaustionFallback, 1, 0) != 0)
            {
                return;
            }

            var candidateSnapshot = candidates.OrderBy(x => x.WeightedLoad).ToArray();
            Logger?.LogWarning(
                "CDN pool for app {AppId} has no healthy servers. Enabling exhaustion fallback with all {Count} known candidates.",
                _appId,
                candidateSnapshot.Length);

            // Reintroduce all known endpoints for another real attempt. Their dropped records are retained;
            // a successful operation restores an endpoint, while another failure leaves it dropped.
            // Use the blocking collection here so callers already waiting in Take() are woken immediately.
            if (enqueueCandidates)
            {
                foreach (var server in candidateSnapshot)
                {
                    _availableServerEndpoints.Add(server);
                }
            }
        }

        private void DisableExhaustionFallback()
        {
            if (Interlocked.Exchange(ref _exhaustionFallback, 0) == 1)
            {
                Logger?.LogInformation(
                    "CDN pool for app {AppId} recovered a healthy server. Returning to normal selection.",
                    _appId);
                _populatePoolEvent.Set();
            }
        }

        private void UpdateExhaustionFallback()
        {
            var candidates = Volatile.Read(ref _knownServerEndpoints);
            if (candidates.Length == 0 || candidates.Any(x => !_droppedServers.IsDropped(ServerKey(x))))
            {
                return;
            }

            EnableExhaustionFallback(candidates, enqueueCandidates: true);
        }

        private async Task WaitForPopulateRequestAsync()
        {
            // Wake up when someone asks for more servers, or after a few seconds to check anyway.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_token);
            timeout.CancelAfter(PopulateCheckInterval);

            try
            {
                await _populatePoolEvent.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!_token.IsCancellationRequested)
            {
                // Only the timer fired, which is expected.
            }
        }

        private async Task MonitorAsync()
        {
            try
            {
                while (!_token.IsCancellationRequested)
                {
                    Logger?.LogTrace("MonitorAsync: Waiting before checking if pool needs population");
                    await WaitForPopulateRequestAsync().ConfigureAwait(false);

                    Logger?.LogTrace("MonitorAsync: Checking");
                    if (_availableServerEndpoints.Count < MinimumPoolSize)
                    {
                        Logger?.LogTrace("MonitorAsync: Pool will be populated");
                        _populatedEvent.Reset();

                        var servers = await GetServerListAsync().ConfigureAwait(false);

                        if (servers.Count == 0)
                        {
                            IsExhausted = true;
                            _populatedEvent.Set();
                            _cancellationTokenSource.Cancel();
                            return;
                        }

                        DesignatedProxyServer = servers.FirstOrDefault(x => x.UseAsProxy);

                        var candidates = servers
                            // Filter out servers that aren't relevant to us or our appid.
                            .Where(
                                server =>
                                {
                                    var isContentServer = server.Type == "SteamCache" || server.Type == "CDN";
                                    var allowsAppId = server.AllowedAppIds.Length == 0 ||
                                                      server.AllowedAppIds.Contains(_appId);

                                    return isContentServer && allowsAppId;
                                })
                            .ToList();

                        var knownCandidates = candidates.ToArray();
                        Volatile.Write(ref _knownServerEndpoints, knownCandidates);

                        var healthy = _droppedServers.WithoutDropped(candidates, ServerKey);
                        var exhaustionFallback = candidates.Count > 0 && healthy.Count == 0;

                        if (exhaustionFallback)
                        {
                            EnableExhaustionFallback(knownCandidates, enqueueCandidates: false);
                        }
                        else
                        {
                            DisableExhaustionFallback();
                        }

                        // In normal mode only healthy candidates enter the ordinary pool. If every
                        // candidate is dropped, exhaustion fallback intentionally refills with all of them.
                        var usable = exhaustionFallback ? candidates : healthy;

                        var added = 0;
                        foreach (var server in usable.OrderBy(x => x.WeightedLoad))
                        {
                            _availableServerEndpoints.Add(server);
                            added++;
                        }

                        Logger?.LogDebug(
                            "CDN pool for app {AppId} populated: {Added} of {Total} candidates added; {Dropped} dropped; exhaustion fallback: {Fallback}.",
                            _appId,
                            added,
                            candidates.Count,
                            candidates.Count - healthy.Count,
                            exhaustionFallback);
                    }

                    Logger?.LogTrace("MonitorAsync: Notifying that pool is populated");
                    _populatedEvent.Set();
                }
            }
            catch (OperationCanceledException)
            {
                // The pool was closed.
            }
            catch (Exception ex)
            {
                // If the monitor dies nobody will ever add servers, so make waiting callers give up instead of hanging.
                Logger?.LogError(ex, "CDN server pool monitor stopped unexpectedly");
                IsExhausted = true;
                _populatedEvent.Set();

                if (!_token.IsCancellationRequested)
                {
                    _cancellationTokenSource.Cancel();
                }
            }
        }

        private async Task<IReadOnlyCollection<Server>> GetServerListAsync()
        {
            const int maxListAttempts = 10;
            var failures = 0;
            var throttleDelay = 0;

            while (true)
            {
                _token.ThrowIfCancellationRequested();

                await Task.Delay(TimeSpan.FromSeconds(1), _token).ConfigureAwait(false);

                try
                {
                    var cdnServers = await ContentServerDirectoryService.LoadAsync(
                        _steamContentClient.SteamClient.InternalClient.Configuration,
                        _steamContentClient.SteamClient.ActiveCellId,
                        _token).ConfigureAwait(false);

                    if (cdnServers == null)
                    {
                        throw new InvalidOperationException(
                            "ContentServerDirectoryService did not return a list of servers.");
                    }

                    return cdnServers;
                }
                catch (OperationCanceledException) when (_token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Logger?.LogWarning(ex, "Could not load the CDN server list, trying again");

                    if (++failures >= maxListAttempts)
                    {
                        // Give up. An empty list marks the pool as exhausted, so callers get an error instead of waiting forever.
                        return Array.Empty<Server>();
                    }

                    if (ex is SteamKitWebRequestException { StatusCode: HttpStatusCode.TooManyRequests })
                    {
                        throttleDelay = Math.Min(throttleDelay + 5, MaxThrottleDelaySeconds);
                        await Task.Delay(TimeSpan.FromSeconds(throttleDelay), _token).ConfigureAwait(false);
                    }
                }
            }
        }
    }

    internal readonly record struct CdnServerLease(Server Server, RecoveryProbeLease? RecoveryProbe);

    internal enum CdnServerDisposition
    {
        Succeeded,
        Failed,
        Abandoned
    }
}