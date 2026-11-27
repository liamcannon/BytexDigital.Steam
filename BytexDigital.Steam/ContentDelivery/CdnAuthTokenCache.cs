using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace BytexDigital.Steam.ContentDelivery;

internal sealed class CdnAuthTokenCache
{
    private static readonly TimeSpan ExpirySafetyWindow =
    TimeSpan.FromMinutes(1);

    private static readonly TimeSpan InitialRefreshFailureBackoff =
        TimeSpan.FromSeconds(5);

    private static readonly TimeSpan MaximumRefreshFailureBackoff =
        TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<string, Entry> _tokens = new();

    // One lock per key (depot + host), so a slow request for one CDN never holds up another.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _refreshLocks = new();

    // Tracks failed refresh attempts independently for each depot and host.
    private readonly ConcurrentDictionary<string, RefreshFailure> _refreshFailures = new();

    private readonly Func<DateTime> _utcNow;

    internal CdnAuthTokenCache(Func<DateTime>? utcNow = null)
    {
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    private SemaphoreSlim LockFor(string key) =>
        _refreshLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));

    /// <summary>
    /// Gets a usable token for the specified cache key.
    /// Returns null if no usable token is cached.
    /// </summary>
    internal string? GetToken(string key)
    {
        return _tokens.TryGetValue(key, out var entry) && IsUsable(entry)
            ? entry.Token
            : null;
    }

    internal async Task<bool> InvalidateIfTokenMatchesAsync(
        string key,
        string? rejectedToken,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (string.IsNullOrWhiteSpace(rejectedToken))
        {
            return false;
        }

        var refreshLock = LockFor(key);
        await refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_tokens.TryGetValue(key, out var existing) &&
                string.Equals(
                    existing.Token,
                    rejectedToken,
                    StringComparison.Ordinal))
            {
                return _tokens.TryRemove(key, out _);
            }

            // A newer token may have replaced the rejected one.
            return false;
        }
        finally
        {
            refreshLock.Release();
        }
    }

    /// <summary>
    /// Attempts to refresh a CDN authentication token after a failed request.
    /// Returns true when a usable replacement token is available.
    /// Returns false when the server should not be retried with this cache state.
    /// </summary>
    internal async Task<bool> RefreshAsync(
        string key,
        string? rejectedToken,
        Func<CancellationToken, Task<Entry?>> requestToken,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(requestToken);

        var refreshLock = LockFor(key);

        await refreshLock
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        try
        {
            if (_tokens.TryGetValue(key, out var existing))
            {
                // An empty token is still a successful response from Steam.
                // Keep it cached until expiration rather than requesting it again.
                if (IsUsable(existing) &&
                (string.IsNullOrWhiteSpace(existing.Token) ||
                    !string.Equals(
                        existing.Token,
                        rejectedToken,
                        StringComparison.Ordinal)))
                {
                    return true;
                }

                // Remove an expired token or the token that was rejected.
                _tokens.TryRemove(key, out _);
            }

            // Do not repeatedly contact Steam while this depot/host is in its failure cooldown.
            if (_refreshFailures.TryGetValue(key, out var refreshFailure) &&
                _utcNow() < refreshFailure.RetryAfter)
            {
                return false;
            }

            Entry? replacement;

            try
            {
                replacement = await requestToken(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Refresh failed; apply backoff before requesting another token.
                RecordRefreshFailure(key);
                return false;
            }

            // An empty token is a valid cached response from Steam.
            // A non-empty replacement must still differ from the rejected token.
            if (replacement is null)
            {
                RecordRefreshFailure(key);
                return false;
            }

            replacement = replacement with
            {
                Token = replacement.Token ?? string.Empty
            };

            if (!IsUsable(replacement) ||
                (!string.IsNullOrWhiteSpace(replacement.Token) &&
                string.Equals(
                    replacement.Token,
                    rejectedToken,
                    StringComparison.Ordinal)))
            {
                RecordRefreshFailure(key);
                return false;
            }

            _tokens[key] = replacement;
            _refreshFailures.TryRemove(key, out _);
            return true;
        }
        finally
        {
            refreshLock.Release();
        }
    }

    private bool IsUsable(Entry entry)
    {
        return entry.Expiration > _utcNow().Add(ExpirySafetyWindow);
    }

    private void RecordRefreshFailure(string key)
    {
        var now = _utcNow();

        _refreshFailures.AddOrUpdate(
            key,
            _ => new RefreshFailure(
                1,
                now.Add(InitialRefreshFailureBackoff)),
            (_, previous) =>
            {
                var failureCount = Math.Min(
                    previous.ConsecutiveFailures + 1,
                    5);

                return new RefreshFailure(
                    failureCount,
                    now.Add(GetRefreshFailureBackoff(failureCount)));
            });
    }

    private static TimeSpan GetRefreshFailureBackoff(int consecutiveFailures)
    {
        var delay = InitialRefreshFailureBackoff;

        for (var attempt = 1;
            attempt < consecutiveFailures && delay < MaximumRefreshFailureBackoff;
            attempt++)
        {
            delay = delay.Ticks > MaximumRefreshFailureBackoff.Ticks / 2
                ? MaximumRefreshFailureBackoff
                : TimeSpan.FromTicks(delay.Ticks * 2);
        }

        return delay;
    }

    private sealed record RefreshFailure(
        int ConsecutiveFailures,
        DateTime RetryAfter);

    internal sealed record Entry(string? Token, DateTime Expiration);
}
