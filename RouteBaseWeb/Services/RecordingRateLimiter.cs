using System.Collections.Concurrent;
using RouteBaseWeb.Services.Interfaces;

namespace RouteBaseWeb.Services;

/// <summary>
/// In-memory (single-instance) per-session rate limiter using the server's receipt clock. See
/// <see cref="IRecordingRateLimiter"/> for why the client's GPS timestamps are deliberately not trusted.
/// </summary>
public sealed class RecordingRateLimiter : IRecordingRateLimiter
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(1);
    // Self-eviction bounds: an entry idle past StaleAfter is useless (far beyond the 1s window). Clear()
    // covers API stop/abandon/delete, but sessions ended elsewhere (cleanup service, website) never call
    // it — this sweep reclaims those so the map can't grow for the process lifetime.
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, DateTime> _lastAccepted = new(StringComparer.Ordinal);
    private readonly Func<DateTime> _utcNow;
    private long _lastSweepTicks;

    public RecordingRateLimiter(Func<DateTime> utcNow) => _utcNow = utcNow;

    public bool TryAcquire(string sessionId)
    {
        var now = _utcNow();
        while (true)
        {
            if (_lastAccepted.TryGetValue(sessionId, out var t))
            {
                if (now - t < Window) return false;
                if (_lastAccepted.TryUpdate(sessionId, now, t)) { MaybeSweep(now); return true; }
            }
            else if (_lastAccepted.TryAdd(sessionId, now)) { MaybeSweep(now); return true; }
        }
    }

    public void Clear(string sessionId) => _lastAccepted.TryRemove(sessionId, out _);

    // Amortized cleanup on the accept path (no background timer needed). One thread wins each interval;
    // the rest skip. Concurrent-safe: ConcurrentDictionary tolerates removal during enumeration.
    private void MaybeSweep(DateTime now)
    {
        long last = Interlocked.Read(ref _lastSweepTicks);
        if (now.Ticks - last < SweepInterval.Ticks) return;
        if (Interlocked.CompareExchange(ref _lastSweepTicks, now.Ticks, last) != last) return;

        var cutoff = now - StaleAfter;
        foreach (var kv in _lastAccepted)
            if (kv.Value < cutoff)
                _lastAccepted.TryRemove(new KeyValuePair<string, DateTime>(kv.Key, kv.Value));
    }
}
