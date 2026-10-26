using System.Collections.Concurrent;
using RouteBaseWeb.Services.Interfaces;

namespace RouteBaseWeb.Services;

/// <summary>
/// In-memory (single-instance) live-viewer registry: session id → the SignalR connections watching it.
/// Singleton — the hub writes it from connection scopes and the API reads it from request scopes.
/// See <see cref="ILiveViewerRegistry"/> for why a leaked connection is a battery bug.
/// </summary>
public sealed class LiveViewerRegistry : ILiveViewerRegistry
{
    // byte is a placeholder value — ConcurrentDictionary is the only concurrent set available.
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _sessions = new(StringComparer.Ordinal);

    // Public (anonymous share-link) viewers hold no SignalR connection — they POLL — so they cannot be
    // counted like a hub connection. Instead each poll stamps a last-seen time here, and a session counts
    // as watched while that stamp is fresh. Self-healing: a public viewer that closes its tab stops
    // polling, the stamp goes stale, and the phone drops back to its idle upload cadence on its own.
    private readonly ConcurrentDictionary<string, DateTime> _publicSeen = new(StringComparer.Ordinal);

    // Several client poll intervals (4 s): survives a few missed polls without pinning the phone fast for
    // long after the viewer leaves.
    private static readonly TimeSpan PublicWatchWindow = TimeSpan.FromSeconds(30);

    private readonly Func<DateTime> _utcNow;

    // Clock always injected (K2): Program.cs registers a factory over the DI TimeProvider;
    // tests pass their own Func<DateTime>.
    public LiveViewerRegistry(Func<DateTime> utcNow) => _utcNow = utcNow;

    public void Join(string sessionId, string connectionId)
    {
        while (true)
        {
            var viewers = _sessions.GetOrAdd(sessionId, _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));
            viewers[connectionId] = 0;
            // A concurrent Leave may have pruned `viewers` out of the map between the GetOrAdd and the write
            // above, which would strand our connection in an orphaned set. Retry until the set we wrote into
            // is the one the map still holds.
            if (_sessions.TryGetValue(sessionId, out var current) && ReferenceEquals(current, viewers)) return;
        }
    }

    public void Leave(string sessionId, string connectionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var viewers)) return;
        viewers.TryRemove(connectionId, out _);
        // Prune the session entry when its last viewer leaves, so the map can't grow for the process
        // lifetime. The pair overload removes only if the value is still this exact set — a concurrent Join
        // that replaced it is not clobbered. Join's retry covers a removal BEFORE its verify; a Join can
        // still slip into this set between our IsEmpty read and the TryRemove, AFTER its verify passed
        // (SVC-03) — that one we recover ourselves: re-Join every member found in the pruned set (Join's
        // retry loop makes the re-insert race-safe; a watcher present in both old and new set is idempotent).
        if (viewers.IsEmpty
            && _sessions.TryRemove(new KeyValuePair<string, ConcurrentDictionary<string, byte>>(sessionId, viewers)))
        {
            foreach (var straggler in viewers.Keys)
                Join(sessionId, straggler);
        }
    }

    public void LeaveAll(string connectionId)
    {
        foreach (var sessionId in _sessions.Keys) Leave(sessionId, connectionId);
    }

    public void TouchPublic(string sessionId)
    {
        // Opportunistic sweep of OTHER sessions' stale stamps. The only other prune site (IsWatched) is
        // called from the phone's append path, which stops the moment a session goes terminal — without
        // this sweep, one stamp per session ever publicly polled would survive for the process lifetime.
        // The dictionary holds one entry per publicly-watched session, so the sweep is cheap; the pair
        // overload removes only if the stamp is still the one we read, keeping concurrent refreshes.
        var cutoff = _utcNow() - PublicWatchWindow;
        foreach (var entry in _publicSeen)
            if (entry.Value < cutoff)
                _publicSeen.TryRemove(entry);

        _publicSeen[sessionId] = _utcNow();
    }

    /// <summary>Public-stamp count, for the leak-sweep pin only (same test seam as the clock ctor).</summary>
    internal int PublicStampCount => _publicSeen.Count;

    public bool IsWatched(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var viewers) && !viewers.IsEmpty) return true;

        if (_publicSeen.TryGetValue(sessionId, out var seen))
        {
            if (_utcNow() - seen <= PublicWatchWindow) return true;
            // Stale — self-prune so the map can't grow for the process lifetime. The pair overload removes
            // only if the stamp is still this one, so a concurrent TouchPublic that just refreshed is kept.
            _publicSeen.TryRemove(new KeyValuePair<string, DateTime>(sessionId, seen));
        }
        return false;
    }
}
