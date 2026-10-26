using RouteBaseWeb.Models;

namespace RouteBaseWeb.Services.Matching;

public sealed partial class SegmentGeometry
{
    /// <summary>
    /// Builds a <see cref="SegmentGeometry"/> from a segment's ascending seg_GPS_IDs and
    /// coordinates. Implements Stage 2's zone math (S2.1–S2.4) verbatim in seg_GPS_ID space
    /// and the Stage 3 cell index (S3.1).
    ///
    /// PARITY: the zone expressions keep the exact SQL shape — `(long)(minId + n * f)` with
    /// f promoting to double — never integer division. trunc = CAST AS INT = toward zero.
    /// </summary>
    public static SegmentGeometry Create(SegmentId segmentId, int[] segGpsIds, double[] lat, double[] lng)
    {
        int n = segGpsIds.Length;                 // COUNT(*) ≡ COUNT(DISTINCT seg_GPS_ID)
        long minId = segGpsIds[0];                // ascending → MIN
        long maxId = segGpsIds[n - 1];            //            → MAX
        double f = n > 250 ? 0.05 : 0.20;         // S2.2: fraction switches at > 250

        // S2.2: start/end zones = n-fraction clamped to the 40/60 hard stops.
        long startZoneEnd = Math.Min(
            (long)(minId + n * f),
            (long)(minId + (maxId - minId) * 0.40));
        long endZoneStart = Math.Max(
            (long)(maxId - n * f),
            (long)(minId + (maxId - minId) * 0.60));

        // S5.1: gap threshold in raw GPS_ID units, integer arithmetic.
        int gapThreshold = Math.Max(300, Math.Min(n * 10, 1500));

        // S3.1: index each segment point under its 9 neighbour cells.
        var cellIndex = new Dictionary<long, List<int>>(n * 9);
        Span<long> neighbors = stackalloc long[9];
        for (int j = 0; j < n; j++)
        {
            if (double.IsNaN(lat[j]) || double.IsNaN(lng[j])) continue;
            GeoCellHash.WriteNeighbors(GeoCellHash.Pack(lat[j], lng[j]), neighbors);
            for (int k = 0; k < 9; k++)
            {
                if (!cellIndex.TryGetValue(neighbors[k], out var list))
                    cellIndex[neighbors[k]] = list = new List<int>(1);
                list.Add(j);
            }
        }

        return new SegmentGeometry
        {
            SegmentId    = segmentId,
            SegGpsIds    = segGpsIds,
            Lat          = lat,
            Lng          = lng,
            TotalPoints  = n,
            SegStartId   = minId,
            SegEndId     = maxId,
            StartZoneEnd = startZoneEnd,
            EndZoneStart = endZoneStart,
            GapThreshold = gapThreshold,
            CellIndex    = cellIndex,
        };
    }
}
