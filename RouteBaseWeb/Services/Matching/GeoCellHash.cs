namespace RouteBaseWeb.Services.Matching;

/// <summary>
/// Packs a lat/lng into the integer geo_cell hash used by the spatial pre-filter, and
/// expands a cell into its 9-neighbour set (centre + 8).
///
/// Do NOT "fix" the truncation-toward-zero — negative coords near 0 intentionally collapse
/// to the same cell (e.g. lat −0.0001 and +0.0001 both → cell 0), matching SQLite's CAST.
/// </summary>
public static class GeoCellHash
{
    // 9 packed-long offsets in the exact order the SQL cell-expansion emits them.
    // |lngCell| ≤ 90000 so ±1 never carries into the lat component, but the arithmetic is
    // done on the packed long literally (per S3.1) rather than per-axis.
    private static readonly long[] NeighborOffsets =
    {
        -1000001, -1000000, -999999, -1, 0, 1, 999999, 1000000, 1000001,
    };

    /// <summary>geo_cell = CAST(lat*500 AS INT)*1e6 + CAST(lng*500 AS INT), truncated toward zero.</summary>
    public static long Pack(double lat, double lng) =>
        (long)(lat * 500) * 1_000_000L + (long)(lng * 500);

    /// <summary>Writes the 9 packed neighbour cells (centre + 8) into <paramref name="dest9"/>
    /// in fixed order. Zero-alloc; <paramref name="dest9"/> must have length ≥ 9.</summary>
    public static void WriteNeighbors(long cell, Span<long> dest9)
    {
        for (int i = 0; i < 9; i++)
            dest9[i] = cell + NeighborOffsets[i];
    }
}
