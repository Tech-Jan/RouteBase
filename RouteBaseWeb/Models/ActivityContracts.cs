using RouteBaseWeb.Services.Tracks;

namespace RouteBaseWeb.Models;

// Response contracts for ActivitiesApiController. Serialized camelCase (web defaults);
// wire shapes pinned by ActivitiesApiWireShapeTests. Each mirrors a TS type — edit both
// in the same commit.

/// <summary>
/// Response of POST /api/activities/grid: the current page of rows, the full filtered ID set
/// (map scope + count), the owner-scoped map universe, and the count denominators.
/// <c>OwnedFilteredCount</c> is how many of <c>FilteredIds</c> the caller owns — the number in the
/// "Export GPX (N)" button, since an export only ever contains the caller's own rides. It equals
/// <c>FilteredIds.Count</c> whenever the owner filter is already scoped to just the caller (the
/// default), so the common case costs no extra query.
/// Mirrors TS <c>ActivityGridResponse</c> (overview/activities-grid.ts). Also serves as
/// <c>ActivitiesGridBootstrap.InitialData</c> (Pages/Activities.cshtml.cs) — the Razor
/// first-page bootstrap reuses this record rather than defining its own twin.
/// </summary>
public sealed record ActivityGridResponse(
    IReadOnlyList<ActivityRowDto> Rows,
    IReadOnlyList<int> FilteredIds,
    IReadOnlyList<int> OwnerIds,
    int Total,
    int UnfilteredTotal,
    int OwnedFilteredCount);

/// <summary>
/// One track of POST /api/activities/gps-data (overview map overlay). Mirrors TS
/// <c>OverviewTrack</c> (overview/overview-types.ts). Raw numbers, not culture-formatted
/// strings — the client formats for display. Points are [lat,lng] pairs, not {lat,lng}
/// objects: kills the per-point property names on the wire and the per-point object alloc
/// client-side.
/// </summary>
public sealed record OverviewTrackDto(
    int Id,
    string? Name,
    string Date,
    string? Type,
    double Distance,
    double Ascent,
    IReadOnlyList<double[]> Points);

/// <summary>
/// One point of GET /api/activities/{id}/gps. Mirrors TS <c>ActivityGpsPointDto</c>
/// (core/core-types.ts). Field names keep the legacy GpsData casing (gpsLat/gpsLong).
/// Altitude/Distance dropped 2026-07-17: no consumer ever read them (segment-create.ts and
/// photos.ts only use gpsLat/gpsLong).
/// </summary>
public sealed record ActivityGpsPointDto(
    double GpsLat,
    double GpsLong);

/// <summary>
/// One point of GET /api/activities/{id}/gps-summary — the downsampled minimap polyline.
/// Consumed as the lat/lng subset of TS <c>GpsPoint</c> (core/track-thumbnail.ts fetchGps).
/// </summary>
public sealed record LatLngDto(double Lat, double Lng);

/// <summary>
/// One track point as the segment map consumes it — <c>/api/SegmentsApi/activities/{id}/track</c> and
/// <c>/activities/bulk-tracks</c>. Serializes to the same <c>{lat,lng,gpsId}</c> those endpoints already
/// emitted when they returned the <c>GpsCoordinate</c> entity directly; this names the contract without
/// changing it (convention: every response body is a named DTO).
///
/// It also exists for a correctness reason. Those endpoints hand back lists straight out of
/// <c>GpsCoordinateCache</c>, which are SHARED between requests — rounding them in place would corrupt
/// the cache for every later reader. Projecting into this record keeps the cached values untouched.
///
/// <c>GpsId</c> is not dead weight here: segment-stats-map-data.ts matches effort entry/exit ids onto
/// points with it.
/// </summary>
public sealed record TrackPointDto(double Lat, double Lng, int GpsId)
{
    public static TrackPointDto From(GpsCoordinate c) =>
        new(GpsPrecision.Coord(c.Lat), GpsPrecision.Coord(c.Lng), c.GpsId);
}

/// <summary>
/// Response of PATCH /api/activities/{id}: echoes the applied (trimmed/normalised) values;
/// null = field not part of this update. The TS caller (activity/activity-edit.ts) currently
/// ignores the success body.
/// </summary>
public sealed record UpdateActivityResponse(
    string? Name,
    string? ActivityType,
    [property: WireValues(typeof(Visibilities))] string? Visibility);

/// <summary>
/// Response of POST /api/activities/bulk/update — the count of activities actually changed. Owner-scoped:
/// ids the caller doesn't own are silently skipped and not counted, so the client reports this, not the
/// selected count. Mirrors the inline type in overview/activities-bulk-controller.ts.
/// </summary>
public sealed record BulkUpdateActivitiesResponse(int Updated);

/// <summary>
/// Response of POST /api/activities/bulk/delete — the count moved to trash (reversible via /api/trash).
/// Owner-scoped; foreign/missing ids are skipped. Mirrors overview/activities-bulk-controller.ts.
/// </summary>
public sealed record BulkDeleteActivitiesResponse(int Deleted);

/// <summary>
/// Response of POST /api/activities/{id}/refresh-elevation. PointsReplaced == 0 means the
/// DEM source yielded nothing (endpoint down / no coverage) — the client surfaces that
/// instead of pretending success (EL-08). Mirrors the inline type in activity/activity-actions.ts.
/// </summary>
public sealed record RefreshElevationResponse(
    string Message,
    int PointsTotal,
    int PointsReplaced);
