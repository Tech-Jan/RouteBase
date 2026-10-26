namespace RouteBaseWeb.Models.Sharing;

/// <summary>
/// Wire shapes for the share-mounted segment read endpoints. TS mirrors carry the SAME names in
/// src/js/segment-stats/segment-stats-api.ts — edit both in the same commit.
///
/// Every response body is a NAMED type. Note the authenticated equivalent, <c>bulk-tracks</c>, returns a
/// bare <c>Dictionary&lt;string, List&lt;GpsCoordinate&gt;&gt;</c> — one of the untyped contracts the
/// repo's named-DTO rule exists to prevent. It is deliberately NOT mirrored here.
/// </summary>
public sealed record ShareSegmentTrackDto(int TrackId, IReadOnlyList<GpsCoordinate> Points);

/// <param name="Total">
/// How many tracks the grant permits in total, so the client knows when to stop paging. Bounded
/// server-side; it is not a count of anything the caller supplied.
/// </param>
public sealed record ShareSegmentTracksResponse(IReadOnlyList<ShareSegmentTrackDto> Tracks, int Total);

/// <summary>Per-point performance data for the requested efforts, intersected with the permitted set.</summary>
public sealed record ShareEffortPerformanceResponse(IReadOnlyList<EffortPerformanceData> Efforts);
