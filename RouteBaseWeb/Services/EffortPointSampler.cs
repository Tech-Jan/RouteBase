namespace RouteBaseWeb.Services;

/// <summary>
/// Pure uniform-stride index sampler for per-effort chart downsampling. Uniform (not LTTB) because
/// effort points are multi-channel (speed/altitude/cost/lat/lng/progress) and the hover marker needs
/// spatially even coverage — a single-channel extremum picker would distort the other lines. O(n),
/// deterministic, monotonic-ascending, first and last index always pinned.
/// </summary>
public static class EffortPointSampler
{
    /// <summary>
    /// Indices to keep when downsampling <paramref name="count"/> points to at most
    /// <paramref name="maxPoints"/>. Returns every index (0..count-1) when <paramref name="maxPoints"/>
    /// is ≤ 0 (full resolution) or the effort already fits the budget. Otherwise returns evenly spaced
    /// indices with endpoints pinned, de-duplicated ascending.
    /// </summary>
    public static int[] SampleIndices(int count, int maxPoints)
    {
        if (count <= 0) return [];
        if (maxPoints <= 0 || count <= maxPoints) return AllIndices(count);
        if (maxPoints == 1) return [0];

        var result = new List<int>(maxPoints);
        int last = -1;
        for (int k = 0; k < maxPoints; k++)
        {
            // Round evenly across [0, count-1]; k=0 → 0, k=maxPoints-1 → count-1 (endpoints pinned).
            int i = (int)Math.Round((double)k * (count - 1) / (maxPoints - 1));
            if (i > last) { result.Add(i); last = i; } // monotone formula → dedup by strict increase
        }
        return result.ToArray();
    }

    private static int[] AllIndices(int count)
    {
        var all = new int[count];
        for (int i = 0; i < count; i++) all[i] = i;
        return all;
    }
}
