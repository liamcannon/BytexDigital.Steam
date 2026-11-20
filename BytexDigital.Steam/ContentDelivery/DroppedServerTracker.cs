using System;
using System.Collections.Generic;
using System.Linq;

namespace BytexDigital.Steam.ContentDelivery;

/// <summary>
///     Tracks CDN servers that have failed. A server remains dropped until a successful CDN operation
///     (either a recovery probe or a normal/fallback request) proves that it can serve content again.
/// </summary>
internal sealed class DroppedServerTracker
{
    private readonly object _sync = new();
    private readonly Dictionary<string, DroppedServer> _dropped = new(StringComparer.Ordinal);
    private readonly TimeSpan _initialProbeDelay;
    private readonly TimeSpan _maximumProbeDelay;
    private readonly Func<DateTime> _utcNow;
    private long _nextProbeId;

    internal DroppedServerTracker(
        TimeSpan initialProbeDelay,
        TimeSpan maximumProbeDelay,
        Func<DateTime>? utcNow = null)
    {
        if (initialProbeDelay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(initialProbeDelay), "The initial probe delay must be positive.");
        }

        if (maximumProbeDelay < initialProbeDelay)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumProbeDelay),
                "The maximum probe delay must be greater than or equal to the initial probe delay.");
        }

        _initialProbeDelay = initialProbeDelay;
        _maximumProbeDelay = maximumProbeDelay;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>
    ///     Marks a server as unhealthy. Repeated notifications for an already dropped server do not
    ///     reset its recovery backoff.
    /// </summary>
    internal void Drop(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        lock (_sync)
        {
            if (_dropped.ContainsKey(key))
            {
                return;
            }

            _dropped.Add(key, new DroppedServer(_utcNow() + _initialProbeDelay));
        }
    }

    /// <summary>
    ///     Returns whether a server is currently excluded from normal pool selection.
    ///     A dropped server stays dropped until a successful CDN operation explicitly restores it.
    /// </summary>
    internal bool IsDropped(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        lock (_sync)
        {
            return _dropped.ContainsKey(key);
        }
    }

    /// <summary>
    ///     Marks a server healthy after a successful real CDN operation. This is also used when a server
    ///     succeeds after the pool has entered its exhaustion fallback. Removing the entry invalidates
    ///     any outstanding recovery-probe lease for that server.
    /// </summary>
    /// <returns>True when a dropped entry was removed; otherwise false.</returns>
    internal bool MarkHealthy(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        lock (_sync)
        {
            return _dropped.Remove(key);
        }
    }

    /// <summary>
    ///     Returns only servers that are not currently dropped, preserving their original order.
    ///     An empty result is valid when every candidate is unhealthy. The caller (the pool) decides
    ///     whether to enter exhaustion fallback; this tracker never clears health state to refill a pool.
    /// </summary>
    internal IReadOnlyList<T> WithoutDropped<T>(IReadOnlyList<T> items, Func<T, string> keyOf)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(keyOf);

        lock (_sync)
        {
            return items.Where(item => !_dropped.ContainsKey(keyOf(item))).ToList();
        }
    }

    /// <summary>
    ///     Attempts to acquire the single recovery-probe lease for a dropped server.
    ///     The caller must complete or cancel the returned lease.
    /// </summary>
    internal bool TryBeginRecoveryProbe(string key, out RecoveryProbeLease lease)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        lock (_sync)
        {
            if (!_dropped.TryGetValue(key, out var droppedServer)
                || droppedServer.ActiveProbeId.HasValue
                || _utcNow() < droppedServer.NextProbeAt)
            {
                lease = default;
                return false;
            }

            var probeId = ++_nextProbeId;
            droppedServer.ActiveProbeId = probeId;
            lease = new RecoveryProbeLease(key, probeId);
            return true;
        }
    }

    /// <summary>
    ///     Completes a recovery probe. A successful probe removes the dropped entry; a failed probe keeps it
    ///     excluded and schedules another attempt using exponential backoff.
    ///     Returns false when the lease is stale or has already been completed/cancelled.
    /// </summary>
    internal bool CompleteRecoveryProbe(RecoveryProbeLease lease, bool succeeded)
    {
        if (lease.ProbeId == 0 || string.IsNullOrWhiteSpace(lease.Key))
        {
            return false;
        }

        lock (_sync)
        {
            if (!_dropped.TryGetValue(lease.Key, out var droppedServer)
                || droppedServer.ActiveProbeId != lease.ProbeId)
            {
                return false;
            }

            if (succeeded)
            {
                return _dropped.Remove(lease.Key);
            }

            droppedServer.ActiveProbeId = null;

            if (droppedServer.FailedProbeCount < int.MaxValue)
            {
                droppedServer.FailedProbeCount++;
            }

            droppedServer.NextProbeAt = _utcNow() + GetProbeDelay(droppedServer.FailedProbeCount);
            return true;
        }
    }

    /// <summary>
    ///     Releases a recovery-probe lease without changing health state or increasing the failure count.
    ///     This is intended for caller cancellation or an operation abandoned before it could test the server.
    /// </summary>
    internal bool CancelRecoveryProbe(RecoveryProbeLease lease)
    {
        if (lease.ProbeId == 0 || string.IsNullOrWhiteSpace(lease.Key))
        {
            return false;
        }

        lock (_sync)
        {
            if (!_dropped.TryGetValue(lease.Key, out var droppedServer)
                || droppedServer.ActiveProbeId != lease.ProbeId)
            {
                return false;
            }

            droppedServer.ActiveProbeId = null;
            return true;
        }
    }

    private TimeSpan GetProbeDelay(int failedProbeCount)
    {
        var delay = _initialProbeDelay;

        for (var attempt = 0; attempt < failedProbeCount && delay < _maximumProbeDelay; attempt++)
        {
            delay = delay.Ticks > _maximumProbeDelay.Ticks / 2
                ? _maximumProbeDelay
                : TimeSpan.FromTicks(delay.Ticks * 2);
        }

        return delay;
    }

    private sealed class DroppedServer
    {
        internal DroppedServer(DateTime nextProbeAt)
        {
            NextProbeAt = nextProbeAt;
        }

        internal DateTime NextProbeAt { get; set; }
        internal int FailedProbeCount { get; set; }
        internal long? ActiveProbeId { get; set; }
    }
}

/// <summary>
///     Identifies one in-flight recovery probe. The ID prevents a stale completion from affecting a newer probe.
/// </summary>
internal readonly record struct RecoveryProbeLease(string Key, long ProbeId);
