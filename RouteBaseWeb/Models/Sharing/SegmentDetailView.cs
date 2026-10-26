using System.Text.Json.Serialization;
using RouteBaseWeb.Helpers;
using RouteBaseWeb.Services;

namespace RouteBaseWeb.Models.Sharing;

/// <summary>
/// One zone rectangle of a shapes segment, serialized into the segment-stats JSON config island.
/// Mirrors TS <c>ShapeRect</c> (src/js/core/core-types.ts) — edit both in the same commit.
/// JsonPropertyName pins the island keys: the island is serialized with default STJ options
/// (_SegmentStatsScripts.cshtml), so property names would otherwise ship PascalCase.
/// </summary>
public sealed record ShapeRectDto(
    [property: JsonPropertyName("minLat")]  double MinLat,
    [property: JsonPropertyName("maxLat")]  double MaxLat,
    [property: JsonPropertyName("minLong")] double MinLong,
    [property: JsonPropertyName("maxLong")] double MaxLong,
    [property: JsonPropertyName("role")]    string Role);

/// <summary>
/// Everything a segment statistics render needs, independent of WHO is rendering it and under which route.
///
/// Property names mirror the ones <c>StatisticsModel</c> exposed before this extraction, on purpose: it
/// lets the page markup move to partials over this type without touching a single expression, so the
/// extraction diff stays reviewable and cannot quietly change what is rendered.
/// </summary>
public sealed class SegmentDetailView
{
    public required Segment Segment { get; init; }

    /// <summary>Whether the VIEWER owns this segment. Distinct from <see cref="Surfaces"/>: a share view
    /// forces every surface off regardless of this, so the two must not be conflated.</summary>
    public bool IsOwner { get; init; }

    public SegmentViewSurfaces Surfaces { get; init; } = SegmentViewSurfaces.ReadOnlyShare;

    public List<SegmentEffortDetailed> DetailedEfforts { get; init; } = [];

    /// <summary>Precomputed effort totals (same source as the segment list). Null → "-" when efforts are
    /// pending analysis.</summary>
    public SegmentEffortTotals? EffortTotals { get; init; }

    /// <summary>Zone rectangles of a shapes segment (projected from its rule document) for the map config.
    /// Empty for polyline segments and pre-rework shape segments.</summary>
    public IReadOnlyList<ShapeRectDto> ShapeRects { get; init; } = [];

    /// <summary>Optional heatmap attachment on a shapes segment. Always null on a share render — that
    /// absence is what keeps heatmap-layer.ts from polling.</summary>
    public Heatmap? Heatmap { get; init; }

    /// <summary>Empty on a share render: the key would be public to anyone holding the link, so share
    /// pages fall back to the keyless basemaps.</summary>
    public string MapTilerApiKey { get; init; } = string.Empty;

    /// <summary>Base path for this render's segment data endpoints. The canonical page uses the
    /// authenticated API; a share render passes <c>/s/{token}/api/segment</c>, because the credential has
    /// to be in the URL — the slice deliberately issues no share cookie.</summary>
    public string ApiBase { get; init; } = "/api/SegmentsApi";

    /// <summary>The whole #segmentStatisticsConfig island (F2b) — the exact projection the cshtml
    /// dictionary used to build, conditionals included (see <see cref="SegmentStatisticsConfigDto"/>).</summary>
    public SegmentStatisticsConfigDto GetConfigForJavaScript()
    {
        // [lat, lng] — plus [2] = altitude in metres when EVERY point has one. All-or-nothing on
        // purpose: the replay profile builds the segment's terrain line out of these, and a run with
        // holes in it would draw a cliff. Partial data falls back to per-effort lines client-side.
        IReadOnlyList<double?[]>? segmentCoords = null;
        if (Segment.GpsData.Any())
        {
            var segGps = Segment.GpsData.OrderBy(g => g.SegGpsId).ToList();
            segmentCoords = segGps.All(g => g.Altitude.HasValue)
                ? segGps.Select(g => new double?[] { g.GpsLat, g.GpsLong, g.Altitude }).ToList()
                : segGps.Select(g => new double?[] { g.GpsLat, g.GpsLong }).ToList();
        }

        return new SegmentStatisticsConfigDto(
            HasSegment: true, // the page 404s otherwise; kept because the TS reads it
            HasGpsData: Segment.GpsData.Any(),
            HasEfforts: DetailedEfforts.Any(),
            MapTilerApiKey: MapTilerApiKey,
            SegmentId: Segment.SegmentId.Value,
            Kind: Segment.SegmentKind,
            ApiBase: ApiBase,
            CanExcludeEfforts: Surfaces.CanExcludeEfforts,
            ShowRiderIdentity: Surfaces.ShowRiderIdentity,
            CanLinkToActivities: Surfaces.CanLinkToActivities,
            CanManageSegment: Surfaces.CanManageSegment,
            ShowHeatmap: Surfaces.ShowHeatmap,
            // Omitted ENTIRELY when the surface is off — the absence is what keeps the poll dead.
            HasHeatmap: Surfaces.ShowHeatmap ? Heatmap != null : null,
            HeatmapStatus: Surfaces.ShowHeatmap && Heatmap != null ? Heatmap.Status : null,
            SegmentCoords: segmentCoords,
            Shapes: ShapeRects.Any() ? ShapeRects : null,
            EffortRows: DetailedEfforts.Any()
                ? DetailedEfforts
                    .OrderBy(e => e.ElapsedTime)
                    .Select((e, idx) => SegmentEffortRowDto.From(e, idx, Surfaces.ShowRiderIdentity))
                    .ToList()
                : null);
    }

    /// <summary>Segment settings from profile — default match threshold (%) for re-analysis.</summary>
    public int DefaultMatchThreshold { get; init; }
}

/// <summary>
/// One effortRows entry of the segment-stats JSON config island (was an anonymous Razor projection —
/// named so the shape registers as an island component schema, D28). Mirrors TS <c>EffortRow</c>
/// (segment-stats-effort-types.ts) via the generated contracts. JsonPropertyName pins the island keys:
/// the island serializes with default STJ options (see <see cref="ShapeRectDto"/>).
/// </summary>
public sealed record SegmentEffortRowDto(
    [property: JsonPropertyName("effortIndex")] int EffortIndex,
    [property: JsonPropertyName("colorIndex")] int ColorIndex,
    [property: JsonPropertyName("gpsInSegmentId")] int GpsInSegmentId,
    [property: JsonPropertyName("activityId")] int ActivityId,
    [property: JsonPropertyName("activityName")] string? ActivityName,
    [property: JsonPropertyName("activityType")] string? ActivityType,
    [property: JsonPropertyName("isOwn")] bool IsOwn,
    [property: JsonPropertyName("userDisplayName")] string? UserDisplayName,
    [property: JsonPropertyName("dateIso")] string? DateIso,
    [property: JsonPropertyName("matchPct")] int MatchPct,
    [property: JsonPropertyName("elapsedSec")] double ElapsedSec,
    [property: JsonPropertyName("distanceM")] double DistanceM,
    [property: JsonPropertyName("avgSpeedMs")] double? AvgSpeedMs,
    [property: JsonPropertyName("avgHr")] double? AvgHr,
    [property: JsonPropertyName("avgPower")] double? AvgPower,
    [property: JsonPropertyName("elevationM")] double ElevationM,
    [property: JsonPropertyName("entryId")] int EntryId,
    [property: JsonPropertyName("exitId")] int ExitId,
    [property: JsonPropertyName("excluded")] bool Excluded)
{
    /// <summary>
    /// Belt and braces with the TS gate: when rider identity is off, no name is serialized at all and
    /// isOwn is forced false, so hasForeignRiders cannot flip the Rider column back on. Zero-valued
    /// HR/power mean "no sensor" and serialize as null.
    /// </summary>
    public static SegmentEffortRowDto From(SegmentEffortDetailed e, int idx, bool showRiderIdentity) => new(
        EffortIndex: idx,
        ColorIndex: idx % 10,
        GpsInSegmentId: e.GpsInSegmentId,
        ActivityId: e.ActivityId,
        ActivityName: e.ActivityName,
        ActivityType: e.ActivityType,
        IsOwn: showRiderIdentity && e.IsOwnEffort,
        UserDisplayName: showRiderIdentity ? e.UserDisplayName : null,
        // Day-at-midnight wall-clock ISO, not bare yyyy-MM-dd: browsers parse a date-only string as
        // UTC midnight and a UTC-minus viewer rendered the previous day (AR-02 terminal suffix rule).
        DateIso: (e.ActivityDate?.Date).ToWallClockIso(),
        MatchPct: e.MatchPercentage,
        ElapsedSec: e.ElapsedTime,
        DistanceM: e.Distance,
        AvgSpeedMs: e.AverageSpeed,
        AvgHr: e.AverageHeartRate is > 0 ? e.AverageHeartRate : null,
        AvgPower: e.AveragePower is > 0 ? e.AveragePower : null,
        ElevationM: e.Elevation,
        EntryId: e.GpsIdEntry,
        ExitId: e.GpsIdExit,
        Excluded: e.Excluded);
}

/// <summary>
/// The #segmentStatisticsConfig island (F2b — was a Dictionary&lt;string, object?&gt;).
/// [JsonPropertyName] pins carry the casing (the island is serialized with NO naming policy).
/// The five conditional members are nullable + WhenWritingNull because their ABSENCE is
/// load-bearing: heatmap-controls computes status 'none' from a missing hasHeatmap/heatmapStatus
/// and never starts its 2s×300 poll — a present false/null would not be enough, the poll keys off
/// the status. Pinned by SegmentStatsConfigWireShapeTests on BOTH heatmap branches.
/// </summary>
public sealed record SegmentStatisticsConfigDto(
    [property: JsonPropertyName("hasSegment")] bool HasSegment,
    [property: JsonPropertyName("hasGpsData")] bool HasGpsData,
    [property: JsonPropertyName("hasEfforts")] bool HasEfforts,
    [property: JsonPropertyName("mapTilerApiKey")] string MapTilerApiKey,
    [property: JsonPropertyName("segmentId")] int SegmentId,
    [property: JsonPropertyName("kind")]
    [property: WireValues(typeof(SegmentKinds))] string Kind,
    [property: JsonPropertyName("apiBase")] string ApiBase,
    [property: JsonPropertyName("canExcludeEfforts")] bool CanExcludeEfforts,
    [property: JsonPropertyName("showRiderIdentity")] bool ShowRiderIdentity,
    [property: JsonPropertyName("canLinkToActivities")] bool CanLinkToActivities,
    [property: JsonPropertyName("canManageSegment")] bool CanManageSegment,
    [property: JsonPropertyName("showHeatmap")] bool ShowHeatmap,
    [property: JsonPropertyName("hasHeatmap")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? HasHeatmap,
    [property: JsonPropertyName("heatmapStatus")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [property: WireValues(typeof(HeatmapStatuses))] string? HeatmapStatus,
    [property: JsonPropertyName("segmentCoords")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<double?[]>? SegmentCoords,
    [property: JsonPropertyName("shapes")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ShapeRectDto>? Shapes,
    [property: JsonPropertyName("effortRows")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<SegmentEffortRowDto>? EffortRows);
