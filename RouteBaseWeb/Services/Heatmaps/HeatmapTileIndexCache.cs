using Microsoft.Extensions.Caching.Memory;

namespace RouteBaseWeb.Services.Heatmaps;

/// <summary>
/// Caches the parsed <c>.htiles</c> lookup index (tile → file offset/length) so a pan/scrub burst of
/// tile GETs parses the footer+index once, not per request. Keyed by (heatmapId, generatedAtTicks): a
/// regenerate bumps the timestamp → a fresh entry, so a stale index is never served. Holds only offsets
/// (~tens of KB per heatmap), never PNG bytes; a short TTL bounds idle memory on a small server.
/// </summary>
public sealed class HeatmapTileIndexCache(IMemoryCache cache, ILogger<HeatmapTileIndexCache>? logger = null)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);

    /// <summary>Tile → (offset, length) within the packed file; null when there is no servable file.</summary>
    public async Task<IReadOnlyDictionary<ulong, (long Offset, int Len)>?> GetOrLoadAsync(
        int heatmapId, long generatedAtTicks, string path, CancellationToken ct)
    {
        var key = $"hm-index:{heatmapId}:{generatedAtTicks}";
        if (cache.TryGetValue(key, out Dictionary<ulong, (long, int)>? cached))
            return cached;

        var index = await HtilesFormat.ReadIndexAsync(path, ct, logger);
        if (index is null)
            return null; // missing/corrupt — don't cache, so it re-checks once the file lands

        cache.Set(key, index, Ttl);
        return index;
    }
}
