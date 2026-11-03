using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace BytexDigital.Steam.ContentDelivery;

/// <summary>
///     Remembers CDN servers that failed, for a limited time, so the pool does not hand them out again straight away.
/// </summary>
internal sealed class DroppedServerTracker
{
    private readonly ConcurrentDictionary<string, DateTime> _dropped = new();
    private readonly TimeSpan _cooldown;
    private readonly Func<DateTime> _utcNow;

    internal DroppedServerTracker(TimeSpan cooldown, Func<DateTime>? utcNow = null)
    {
        _cooldown = cooldown;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    // Dropping a server that is already on cooldown starts the cooldown again
    internal void Drop(string key) => _dropped[key] = _utcNow() + _cooldown;

    internal bool IsDropped(string key)
    {
        if (!_dropped.TryGetValue(key, out var until))
        {
            return false;
        }

        if (until > _utcNow())
        {
            return true;
        }

        // Cooldown over. Only removes the entry we just read, never a newer one.
        _dropped.TryRemove(new KeyValuePair<string, DateTime>(key, until));
        return false;
    }

    /// <summary>
    ///     The items that are not on cooldown. If that would leave nothing, the cooldowns are forgotten and every
    ///     item is returned, so dropped servers can never leave the pool empty.
    /// </summary>
    internal IReadOnlyList<T> WithoutDropped<T>(IReadOnlyList<T> items, Func<T, string> keyOf)
    {
        var kept = items.Where(item => !IsDropped(keyOf(item))).ToList();

        if (kept.Count > 0 || items.Count == 0)
        {
            return kept;
        }

        _dropped.Clear();
        return items;
    }
}