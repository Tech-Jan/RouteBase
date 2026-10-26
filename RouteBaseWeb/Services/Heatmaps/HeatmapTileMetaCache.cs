using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Interfaces;
using Microsoft.Extensions.Caching.Memory;

namespace RouteBaseWeb.Services.Heatmaps;

/// <summary>
/// Short-TTL cache of the visibility + heatmap-row lookup that the tile endpoint needs on every
/// request. A pan/scrub burst fires dozens of tile GETs for one segment; without this each one
/// re-runs three EF queries (friendship, segment EXISTS, heatmap row). Only servable (done/stale)
/// results are cached, so a finishing or regenerating render is never masked.
/// </summary>
public sealed class HeatmapTileMetaCache(
    IMemoryCache cache, IHeatmapLifecycle heatmaps, ICurrentUserService currentUser)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    /// <summary>Just what building an ETag and reading a tile needs.</summary>
    public readonly record struct Meta(int HeatmapId, long GeneratedAtTicks);

    /// <summary>
    /// Servable heatmap metadata for the tile endpoint, cached ~30s per (user, segment). Null when the
    /// segment isn't visible or the heatmap has no imagery yet (pending/running/failed) — those states
    /// are never cached, so a render becoming done (or a regenerate) is reflected within one poll.
    /// </summary>
    public async Task<Meta?> GetServableAsync(SegmentId segmentId, CancellationToken ct)
    {
        var key = $"hm-meta:{currentUser.GetUserId()}:{segmentId}";
        if (cache.TryGetValue(key, out Meta cached))
            return cached;

        var heatmap = await heatmaps.GetVisibleAsync(segmentId, ct);
        if (heatmap is null || heatmap.Status is not (HeatmapStatuses.Done or HeatmapStatuses.Stale))
            return null;

        var meta = new Meta(heatmap.HeatmapId, heatmap.GeneratedAt?.Ticks ?? 0);
        cache.Set(key, meta, Ttl);
        return meta;
    }
}
