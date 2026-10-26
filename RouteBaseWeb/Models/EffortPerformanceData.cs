namespace RouteBaseWeb.Models;

/// <summary>
/// Performance data for a single effort on a segment, including per-point metrics.
/// </summary>
public class EffortPerformanceData
{
    public int EffortId { get; set; }
    public int ActivityId { get; set; }
    public string? ActivityName { get; set; }
    public string? ActivityDate { get; set; }
    public double ElapsedTime { get; set; }
    public string? UserId { get; set; }
    public string? UserDisplayName { get; set; }

    /// <summary>
    /// True when <see cref="EffortDataPoint.CumulativeCostKj"/> was estimated from a
    /// physics / metabolic model rather than measured with a power meter. Drives the
    /// dashed-vs-solid line style on the effort-cost chart.
    /// </summary>
    public bool CostEstimated { get; set; }

    /// <summary>"measured" (power meter) or "estimated" (model). Mirrors <see cref="CostEstimated"/> —
    /// the bool stays on the wire deliberately (3 TS consumers read it; see <see cref="CostKinds"/>).</summary>
    [WireValues(typeof(CostKinds))]
    public string? CostKind { get; set; }

    /// <summary>
    /// True when <see cref="Points"/> was stride-downsampled below the effort's full resolution
    /// (server honored a <c>MaxPointsPerEffort</c> budget). Drives the replay lazy full-res refetch:
    /// false means these points are already full-res (small effort or "full" detail) and can be
    /// replayed as-is.
    /// </summary>
    public bool PointsTruncated { get; set; }

    /// <summary>
    /// Columnar (parallel-array) point payload. Homogeneous columns compress far better than the old
    /// per-point object stream (no repeated property names, no per-point boxing), and whole-null
    /// channels collapse to a single <c>null</c> property. The client re-hydrates into
    /// <see cref="EffortDataPoint"/>s at the fetch boundary, so chart/hover/replay consumers are unchanged.
    /// </summary>
    public EffortPointsWire Points { get; set; } = new();
}

/// <summary>
/// Columnar wire form of an effort's per-point channels. Distance/Time/Speed are always present;
/// optional channels are null when every kept point lacks the value (e.g. no power meter).
/// Built once from the full-resolution <see cref="EffortDataPoint"/> list at the kept-index set.
/// </summary>
public sealed class EffortPointsWire
{
    public double[] Distance { get; set; } = [];
    public double[] Time { get; set; } = [];
    public double[] Speed { get; set; } = [];
    public double?[]? Altitude { get; set; }
    public double?[]? Lat { get; set; }
    public double?[]? Lng { get; set; }
    public double?[]? CumulativeCostKj { get; set; }
    public double?[]? SegmentProgress { get; set; }

    /// <summary>
    /// Downsample-selects <paramref name="keptIndices"/> out of <paramref name="pts"/> and columnarizes
    /// in one pass. Values are copied verbatim (rounding already applied in BuildPerformancePoints), so a
    /// re-hydrated point is byte-identical to the source. A channel that is null at every kept index
    /// collapses to a null property.
    /// </summary>
    public static EffortPointsWire FromPoints(List<EffortDataPoint> pts, int[] keptIndices)
    {
        int n = keptIndices.Length;
        var distance = new double[n];
        var time     = new double[n];
        var speed    = new double[n];
        var altitude = new double?[n];
        var lat      = new double?[n];
        var lng      = new double?[n];
        var cost     = new double?[n];
        var progress = new double?[n];
        bool anyAlt = false, anyLat = false, anyLng = false, anyCost = false, anyProg = false;

        for (int k = 0; k < n; k++)
        {
            var p = pts[keptIndices[k]];
            distance[k] = p.Distance;
            time[k]     = p.Time;
            speed[k]    = p.Speed;
            altitude[k] = p.Altitude;         anyAlt  |= p.Altitude.HasValue;
            lat[k]      = p.Lat;              anyLat  |= p.Lat.HasValue;
            lng[k]      = p.Lng;              anyLng  |= p.Lng.HasValue;
            cost[k]     = p.CumulativeCostKj; anyCost |= p.CumulativeCostKj.HasValue;
            progress[k] = p.SegmentProgress;  anyProg |= p.SegmentProgress.HasValue;
        }

        return new EffortPointsWire
        {
            Distance         = distance,
            Time             = time,
            Speed            = speed,
            Altitude         = anyAlt  ? altitude : null,
            Lat              = anyLat  ? lat      : null,
            Lng              = anyLng  ? lng      : null,
            CumulativeCostKj = anyCost ? cost     : null,
            SegmentProgress  = anyProg ? progress : null,
        };
    }
}

/// <summary>
/// Internal build-time representation of a single effort point — the type
/// <see cref="SegmentEfforts"/> assembles before columnarizing into <see cref="EffortPointsWire"/>.
/// Not serialized on the wire.
/// </summary>
public class EffortDataPoint
{
    public double Distance { get; set; }
    public double Time { get; set; }
    public double Speed { get; set; }
    public double? Altitude { get; set; }
    public double? Lat { get; set; }
    public double? Lng { get; set; }

    /// <summary>
    /// Cumulative effort cost (kilojoules) from the segment start to this point. Difficulty-weighted
    /// energy: measured mechanical work (∫P·dt) when power is available, otherwise an estimated
    /// physics/metabolic model. Monotonically non-decreasing. Null when it cannot be computed.
    /// </summary>
    public double? CumulativeCostKj { get; set; }

    /// <summary>
    /// Fraction (0–1) of the segment's reference polyline reached at this point
    /// ("chainage" / linear referencing). Computed by projecting the effort onto the
    /// segment, with off-segment excursions interpolated. Null when it cannot be
    /// computed (degenerate segment geometry or missing lat/lng), in which case the
    /// frontend falls back to per-effort distance normalization.
    /// </summary>
    public double? SegmentProgress { get; set; }
}
