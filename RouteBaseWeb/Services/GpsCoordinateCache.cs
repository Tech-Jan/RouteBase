using Microsoft.Extensions.Caching.Memory;

namespace RouteBaseWeb.Services;

/// <summary>
/// Dedicated bounded cache for GPS coordinate payloads (single- and bulk-track fetches). Kept
/// separate from the shared <see cref="IMemoryCache"/> because a SizeLimit-ed cache rejects entries
/// that carry no Size — the app's other cache users don't set one. The size unit is GPS points
/// (~50 bytes each incl. object overhead), so the limit bounds coordinate RAM no matter how many
/// distinct id-set/downsample combinations get requested. Entries past the limit are simply not
/// cached (compaction evicts LRU); callers always still get their query result — a miss costs a
/// query, never a wrong answer.
///
/// The budget is NOT a const here — it comes from <see cref="MemoryBudget"/>, the one file that owns
/// every memory number. It used to be 4,000,000 points (~200 MB, standing), a ceiling chosen for a
/// bigger machine than the box this runs on. The cost of sizing it too small is a re-query, never a
/// wrong answer, so this cache does not get an opinion about its own size.
/// </summary>
public sealed class GpsCoordinateCache : IDisposable
{
    private readonly MemoryCache _cache;

    public GpsCoordinateCache(MemoryBudget budget) : this(budget.CoordinateCachePoints) { }

    /// <summary>Direct-size overload — for tests that want a tiny cache without a whole budget.</summary>
    public GpsCoordinateCache(long maxPoints) =>
        _cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = maxPoints });

    public bool TryGetValue<T>(object key, out T? value) where T : class
    {
        if (_cache.TryGetValue(key, out T? v) && v != null)
        {
            value = v;
            return true;
        }
        value = null;
        return false;
    }

    /// <summary>Stores a payload; <paramref name="options"/> must carry a Size (in points).</summary>
    public void Set<T>(object key, T value, MemoryCacheEntryOptions options) where T : class =>
        _cache.Set(key, value, options);

    public void Dispose() => _cache.Dispose();
}
