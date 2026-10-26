using System.Globalization;
using RouteBaseWeb.Services.Tracks;
using RouteBaseWeb.Helpers.Grid;
using RouteBaseWeb.Models;
using RouteBaseWeb.Models.GridFilter;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Services.Segments;
using RouteBaseWeb.Services.Segments.Rules;
using Microsoft.AspNetCore.Mvc;
using RouteBaseWeb.Models.Requests;

namespace RouteBaseWeb.Api;

/// <summary>Bodies for the activities grid's Select-mode bulk actions (owner-scoped, capped).</summary>
public sealed record BulkUpdateActivitiesRequest(List<int>? TrackIds, string? ActivityType, string? Visibility);
public sealed record BulkDeleteActivitiesRequest(List<int>? TrackIds);

[ApiController]
[Route("api/activities")]
public class ActivitiesApiController : ControllerBase
{
    private readonly IActivityQueryService _activityQuery;
    private readonly IActivityGpsService _activityGps;
    private readonly IActivityCommandService _activityCommand;
    private readonly IElevationRefreshService _elevationRefresh;
    private readonly IVisibilityService _visibility;
    private readonly IUserService _userService;
    private readonly ICurrentUserService _currentUser;
    private readonly UserCacheInvalidator _invalidator;
    private readonly ShapeSegmentMatcher _shapeMatcher;
    private readonly IOwnerScopeService _ownerScope;
    private readonly ILogger<ActivitiesApiController> _logger;
    private readonly Microsoft.Extensions.Options.IOptions<JsonOptions> _jsonOptions;

    public ActivitiesApiController(
        IActivityQueryService activityQuery,
        IActivityGpsService activityGps,
        IActivityCommandService activityCommand,
        IElevationRefreshService elevationRefresh,
        IVisibilityService visibility,
        IUserService userService,
        ICurrentUserService currentUser,
        UserCacheInvalidator invalidator,
        ShapeSegmentMatcher shapeMatcher,
        IOwnerScopeService ownerScope,
        ILogger<ActivitiesApiController> logger,
        Microsoft.Extensions.Options.IOptions<JsonOptions> jsonOptions)
    {
        _activityQuery   = activityQuery;
        _activityGps     = activityGps;
        _activityCommand = activityCommand;
        _elevationRefresh = elevationRefresh;
        _visibility      = visibility;
        _userService     = userService;
        _currentUser     = currentUser;
        _invalidator     = invalidator;
        _shapeMatcher    = shapeMatcher;
        _ownerScope      = ownerScope;
        _logger = logger;
        _jsonOptions = jsonOptions;
    }

    /// <summary>
    /// Data source for the client-rendered activities grid. Applies the same filter object that drives
    /// the map and returns: the current page as <see cref="ActivityRowDto"/>s, the full filtered ID set
    /// (drives the map scope + count + future replay/chart-from-set), and the total match count.
    /// POST with a JSON body (<see cref="ActivityGridRequest"/>): the map-drawn shape restriction is
    /// rule-document JSON that outgrows a GET query string (~30 zones exceed the 8 KB request line).
    /// </summary>
    [HttpPost("grid")]
    [ProducesResponseType<ActivityGridResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> QueryGrid([FromBody] ActivityGridRequest request, CancellationToken ct = default)
    {
        var pageSize = request.PageSize <= 0 ? 10000 : Math.Clamp(request.PageSize, 10, 10000);
        var page = Math.Max(1, request.Page);

        // Per-column filter-row values (display units, e.g. km) — the column registry applies the predicates
        // and the display→storage unit scale.
        var filters = request.Filters;

        // Visible-user identity context, built once and shared by the owner filter and the row labels.
        var owners = await BuildOwnerContextAsync();

        // Owner column lives in identity space (UserId ⇄ "Me"/display name), so it can't go through the
        // static registry — resolve its combo (Me/Others/Everyone tokens + name search) to a UserId set.
        var ownerFilter = filters.Items.TryGetValue("owner", out var of) ? of : null;
        var owner = OwnerFilterResolver.Resolve(ownerFilter, owners.CurrentUserId, owners.Names);

        // Map-drawn shape ("Merge table ↔ shapes"): the set of tracks the shape matches, via the SAME matcher
        // the live map preview uses (identical set → no table/map drift). AND-restricts the rows + filteredIds
        // so the table shows shape ∩ columns. Null when no shape was sent (the common case).
        var shapeRestrictIds = await ResolveShapeRestrictionAsync(request.Shape, ownerFilter, ct);

        // Sequential on purpose: these services share the request-scoped DbContext, and EF Core
        // forbids concurrent operations on one context.
        // One filter → two projections (full ID set + page of rows), both via the same predicates. The id
        // set is materialized first so the page query reuses its .Count as the total match count instead of
        // re-running a byte-identical COUNT — one aggregate per grid interaction, not two (perf-5).
        var filteredIds = await _activityQuery.GetFilteredActivityIdsAsync(
            activityType: null, request.StartDate, request.EndDate,
            request.OwnerUserId, activityTypes: request.Types, filters: filters,
            ownerUserIds: owner.NoConstraint ? null : owner.UserIds, ownerMatchNone: owner.MatchNone,
            restrictIds: shapeRestrictIds?.Select(i => new TrackId(i)).ToList());

        var (items, total) = await _activityQuery.GetPagedActivitiesAsync(
            activityType: null, request.StartDate, request.EndDate,
            request.SortColumn, request.SortDirection, page, pageSize,
            request.OwnerUserId, activityTypes: request.Types, filters: filters,
            ownerUserIds: owner.NoConstraint ? null : owner.UserIds, ownerMatchNone: owner.MatchNone,
            restrictIds: shapeRestrictIds?.Select(i => new TrackId(i)).ToList(), knownCount: filteredIds.Count);

        // Grand-total denominator for the grid's row count — all visible rows, unfiltered (cheap COUNT).
        var unfilteredTotal = await _activityQuery.GetVisibleActivityCountAsync();

        // Owner-scoped ID set (owner filter only — no date/type/column predicates): the map's display
        // universe. The overview map draws this set and dims it down to filteredIds, so the client never
        // has to guess the owner scope from filtered results. Equal to filteredIds when no column filter
        // is active, so skip the extra query then.
        // A shape restriction narrows filteredIds below the owner universe just like a column filter, so it
        // must force the separate unrestricted ownerIds query (never restricted) — otherwise ownerIds would
        // short-circuit to the shape-restricted filteredIds and the map would lose its full display universe.
        var hasColumnFilters = request.StartDate.HasValue || request.EndDate.HasValue
            || request.Types.Count > 0
            || shapeRestrictIds is not null
            || filters.Items.Any(kv => kv.Key != "owner" && !kv.Value.IsEmpty);
        var ownerIds = hasColumnFilters
            ? await _activityQuery.GetFilteredActivityIdsAsync(
                activityType: null, startDate: null, endDate: null,
                request.OwnerUserId, activityTypes: null, filters: null,
                ownerUserIds: owner.NoConstraint ? null : owner.UserIds, ownerMatchNone: owner.MatchNone)
            : filteredIds;

        // Resolve display names for any owners on this page that aren't already known (non-friends whose
        // public activities are visible) so the Owner column shows real names instead of blanks.
        await EnrichOwnerNamesAsync(owners, items);

        // Photo counts for this page only — one batched query (see GetPhotoCountsAsync).
        var photoCounts = await _activityQuery.GetPhotoCountsAsync(items.Select(a => a.TrackId).ToList());

        var rows = items.Select(a => ActivityRowDto.From(
            a, owners.CurrentUserId, owners.ResolveLabel(a), photoCounts.GetValueOrDefault(a.TrackId))).ToList();

        // How many of the filtered rides the caller OWNS — the count in the "Export GPX (N)" button, which
        // never exports anyone else's. Same predicates, owner forced to the caller; skipped entirely when
        // the owner filter already resolves to exactly the caller (the page's default), where the answer is
        // filteredIds itself.
        var ownedFilteredCount = owner.UserIds is [var only] && string.Equals(only, owners.CurrentUserId, StringComparison.Ordinal)
            ? filteredIds.Count
            : owner.MatchNone
                ? 0
                : (await _activityQuery.GetFilteredActivityIdsAsync(
                    activityType: null, request.StartDate, request.EndDate,
                    ownerUserId: null, activityTypes: request.Types, filters: filters,
                    ownerUserIds: [owners.CurrentUserId],
                    restrictIds: shapeRestrictIds?.Select(i => new TrackId(i)).ToList())).Count;

        return Ok(new ActivityGridResponse(
            rows, filteredIds.Select(t => t.Value).ToList(), ownerIds.Select(t => t.Value).ToList(),
            total, unfilteredTotal, ownedFilteredCount));
    }

    /// <summary>
    /// Resolves the map-drawn shape filter (rule-document JSON, spatial only) to the track ids it matches, so
    /// the grid can AND-restrict rows + filteredIds to them. Mirrors the shape-preview endpoints exactly —
    /// same owner-scope resolution, same <see cref="ShapeSegmentMatcher.PreviewExactTrackIdsAsync"/> — so the
    /// table and the map's live highlight always show the identical set. Returns null (no restriction) when no
    /// shape was sent or the document is absent/invalid/empty.
    /// </summary>
    private async Task<List<int>?> ResolveShapeRestrictionAsync(
        string? shapeJson, ColumnFilter? ownerFilter, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(shapeJson)) return null;
        if (RuleDocument.TryParse(shapeJson) is not { } doc || doc.Shapes is not { Count: > 0 }) return null;
        if (doc.Validate() is { Count: > 0 }) return null; // malformed shape → no restriction, never a 500

        // Owner is server-authored: resolve the grid's own owner filter, then fold the resolved ids into the
        // doc (strip any incoming OwnerFilter, add the scope) — same invariant as SegmentsApiController.FoldOwner.
        var scope = await _ownerScope.ResolveAsync(ownerFilter);
        var trackFilters = doc.TrackFilters.Where(f => f is not OwnerFilter).ToList();
        if (scope.OwnerUserIds != null)
            trackFilters.Add(new OwnerFilter { UserIds = scope.OwnerUserIds.ToList() });
        var folded = doc with { TrackFilters = trackFilters };

        // ct threaded to the stage-2 GPS-decode confirm for the same reason the preview endpoint wires it:
        // dragging filters with a shape active fires a grid request per change, and each superseded
        // request must stop scanning GPS instead of running the matcher to completion (OCE → 499 via
        // ApiExceptionFilter).
        var matched = await _shapeMatcher.PreviewExactTrackIdsAsync(folded, scope.CurrentUserId, scope.FriendIds, ct);
        return matched.TrackIds;
    }

    // Visible-user identity context: the current user id, the id→display-name map (for owner-filter name
    // search), and the per-row owner-label resolver. Built once per request and shared by the owner filter
    // and the row DTO labels. The Owner column is always shown, so the map is always built (self + friends);
    // page owners that aren't friends are merged in later via EnrichOwnerNamesAsync.
    private async Task<OwnerContext> BuildOwnerContextAsync()
    {
        var currentUserId = _currentUser.GetUserId();
        var visibleUserIds = await _visibility.GetVisibleUserIdsAsync();
        var users = await _userService.GetUsersByIdsAsync(visibleUserIds);
        var names = OwnerLabels.ToNameMap(users);

        return new OwnerContext(currentUserId, names, a =>
        {
            if (string.IsNullOrEmpty(a.UserId)) return "";
            if (a.UserId == currentUserId) return "Me";
            return names.TryGetValue(a.UserId, out var dn) ? dn : "";
        });
    }

    // Looks up display names for any owners on the loaded page that aren't already known (non-friends whose
    // public activities are visible) and merges them into the context's name map for label resolution.
    private async Task EnrichOwnerNamesAsync(OwnerContext owners, IEnumerable<Activity> items)
    {
        var unknownIds = items
            .Select(a => a.UserId)
            .OfType<string>()
            .Where(id => id.Length > 0 && id != owners.CurrentUserId && !owners.Names.ContainsKey(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (unknownIds.Count == 0) return;

        var extra = await _userService.GetUsersByIdsAsync(unknownIds);
        foreach (var u in extra)
            owners.Names[u.UserId] = OwnerLabels.DisplayName(u);
    }

    private sealed record OwnerContext(
        string CurrentUserId,
        Dictionary<string, string> Names,
        Func<Activity, string?> ResolveLabel);

    [HttpPost("gps-data")]
    [ProducesResponseType<List<OverviewTrackDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetGpsData([FromBody] GpsDataRequest request, CancellationToken ct)
    {
        try
        {
            if (request.ActivityIds == null || !request.ActivityIds.Any())
            {
                return BadRequest(ApiError.BadRequest("No activity IDs provided"));
            }

            // Resource caps (the activities twin of the segments SA-05 rule). The overview client
            // always chunks at 150 ids/request (activities-gps-loader.ts), so these bounds are pure
            // headroom for legitimate traffic — but without them one request could full-res-decode
            // every visible track into a single response (hundreds of MB on the memory-budgeted server).
            if (request.ActivityIds.Count > 1000)
                return BadRequest(ApiError.BadRequest("Too many activity IDs (max 1000)"));
            if (request.MaxPointsPerTrack <= 0 && request.ActivityIds.Count > 300)
                return BadRequest(ApiError.BadRequest("Full resolution (maxPointsPerTrack <= 0) allows max 300 activity IDs"));

            // 0 = full resolution (no downsampling) — legitimate for the zoom ≥ 11 quality upgrade,
            // bounded by the 300-id cap above.
            var maxPointsPerTrack = request.MaxPointsPerTrack <= 0
                ? 0
                : Math.Clamp(request.MaxPointsPerTrack, 10, 50000);
            var requestedIds = request.ActivityIds;

            // Sequential on purpose: both services share the request-scoped DbContext, and EF Core
            // forbids concurrent operations on one context.
            var activitySlices = await _activityQuery.GetActivitySlicesByIdsAsync(requestedIds, ct);
            var bulkCoords     = await _activityGps.GetBulkTrackCoordinatesAsync(requestedIds, maxPointsPerTrack, ct);

            _logger.LogInformation(
                "Bulk loaded GPS data for {Count} activities (maxPoints={MaxPoints})",
                bulkCoords.Count, maxPointsPerTrack);

            var tracksData = new List<OverviewTrackDto>(bulkCoords.Count);

            foreach (var (trackId, coords) in bulkCoords)
            {
                if (coords.Count == 0) continue;
                if (!activitySlices.TryGetValue(trackId, out var activity)) continue;

                tracksData.Add(new OverviewTrackDto(
                    Id:          activity.TrackId,
                    Name:        activity.TrackName,
                    Date:        activity.StartDateTimeLocal?.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture) ?? "",
                    Type:        activity.ActivityType,
                    Distance:    activity.TotalDistance ?? 0,
                    Ascent:      activity.TotalAscent ?? 0,
                    // Rounded HERE, not in ActivityGpsService: those GpsCoordinate lists are cached and
                    // shared across requests, so rounding upstream would mutate shared state and degrade
                    // a domain value for every future consumer. Wire boundary only.
                    Points:      coords.Select(c => new[]
                                     { GpsPrecision.Coord(c.Lat), GpsPrecision.Coord(c.Lng) }).ToList()));
            }

            _logger.LogInformation("Returning {Count} tracks", tracksData.Count);
            return Ok(tracksData);
        }
        catch (OperationCanceledException)
        {
            // Client aborted (a newer zoom/pan superseded this request) — stop here.
            return StatusCode(499);
        }
    }

    /// <summary>
    /// Start/end coordinates for the filtered activity set, read from the Activity table
    /// (no GPS join). The client uses these to frame the map viewport once up front — before
    /// the (chunked) GPS load — so the camera never jumps after tracks finish loading.
    /// </summary>
    [HttpPost("bounds")]
    [ProducesResponseType<List<ActivityEndpoint>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetActivityBounds([FromBody] ActivityEndpointsRequest request, CancellationToken ct)
    {
        try
        {
            if (request.ActivityIds == null || request.ActivityIds.Count == 0)
                return Ok(new List<ActivityEndpoint>());

            // Same id-list cap as every sibling (gps-data/bulk-tracks): the list feeds a SQL IN(...), and
            // without a bound one hand-rolled request builds a multi-MB clause (API-09).
            if (request.ActivityIds.Count > 1000)
                return BadRequest(ApiError.BadRequest("Too many activity IDs (max 1000)"));

            // ActivityEndpoint carries exactly the four wire fields — serialized as-is.
            return Ok(await _activityQuery.GetActivityEndpointsAsync(request.ActivityIds, ct));
        }
        catch (OperationCanceledException)
        {
            return StatusCode(499);
        }
    }

    [HttpGet("{id}/gps")]
    [Produces("application/json")] // ConditionalJson.ETagged (FileResult); schema declared via metadata (D14 closure)
    [ProducesResponseType<List<ActivityGpsPointDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetActivityGps(int id, CancellationToken ct)
    {
        try
        {
            _logger.LogInformation("Fetching GPS data for activity {ActivityId}", id);

            // Existence + visibility guard only — a cheap EXISTS, no columns/join loaded.
            if (!await _activityQuery.IsActivityVisibleAsync(new TrackId(id)))
            {
                return NotFound(ApiError.NotFound("Activity not found"));
            }

            var gpsData = await _activityGps.GetGpsDataForTrackAsync(new TrackId(id), ct);

            var result = gpsData.Select(g => new ActivityGpsPointDto(
                GpsLat:  GpsPrecision.Coord(g.GpsLat ?? 0),
                GpsLong: GpsPrecision.Coord(g.GpsLong ?? 0))).ToList();

            _logger.LogInformation("Loaded {Count} GPS points for activity {ActivityId}", result.Count, id);
            // Immutable per track content: ETag over the body + short max-age → a revisit re-uses the cached
            // response (or gets a 304) instead of re-decoding & re-sending the whole track.
            return ConditionalJson.ETagged(this, result, _jsonOptions.Value.JsonSerializerOptions);
        }
        catch (OperationCanceledException)
        {
            return StatusCode(499);
        }
    }

    [HttpGet("{id}/gps-summary")]
    [Produces("application/json")] // ConditionalJson.ETagged (FileResult); schema declared via metadata (D14 closure)
    [ProducesResponseType<List<LatLngDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetActivityGpsSummary(int id, CancellationToken ct)
    {
        try
        {
            // Existence + visibility guard only — a cheap EXISTS, no columns/join loaded.
            if (!await _activityQuery.IsActivityVisibleAsync(new TrackId(id)))
                return NotFound(ApiError.NotFound("Activity not found"));

            const int maxPoints = 50;
            var result = await _activityGps.GetTrackCoordinatesAsync(new TrackId(id), maxPoints, ct);

            // Minimap thumbnail — the dashboard/races grids fire one of these per card on every revisit.
            // ETag + short max-age lets the browser skip the round-trip within the window and 304 after.
            return ConditionalJson.ETagged(this,
                result.Select(c => new LatLngDto(GpsPrecision.Coord(c.Lat), GpsPrecision.Coord(c.Lng))).ToList(),
                _jsonOptions.Value.JsonSerializerOptions);
        }
        catch (OperationCanceledException)
        {
            return StatusCode(499);
        }
    }

    [HttpPatch("{id}")]
    [ProducesResponseType<UpdateActivityResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiError>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateActivity(int id, [FromBody] UpdateActivityRequest request)
    {
        var name = request.Name?.Trim();
        var activityType = request.ActivityType?.Trim();
        // CA1308: visibility normalizes to the canonical lowercase token ("public"/"friends"/"private", see
        // Visibilities) that is validated and stored; ToUpperInvariant would fail IsValid and corrupt storage.
#pragma warning disable CA1308
        var visibility = request.Visibility?.Trim().ToLowerInvariant();
#pragma warning restore CA1308

        // Same bounds as the recording write path (RecordingRequestValidator): without them a
        // multi-MB name reaches the DB and every downstream projection (grid rows, gps-data, exports).
        if (name != null && (string.IsNullOrWhiteSpace(name) || name.Length > 200))
            return BadRequest(ApiError.BadRequest("Name must be 1-200 characters"));
        if (activityType != null && (string.IsNullOrWhiteSpace(activityType) || activityType.Length > 50))
            return BadRequest(ApiError.BadRequest("ActivityType must be 1-50 characters"));
        if (visibility != null && !Visibilities.IsValid(visibility))
            return BadRequest(ApiError.BadRequest("Visibility must be 'private', 'friends', or 'public'"));

        if (name == null && activityType == null && visibility == null)
            return BadRequest(ApiError.BadRequest("At least one field must be provided"));

        var result = await _activityCommand.UpdateActivityAsync(new TrackId(id), name, activityType, visibility);
        if (!result)
            return NotFound(ApiError.NotFound("Activity not found or not owned by you"));

        return Ok(new UpdateActivityResponse(name, activityType, visibility));
    }

    /// <summary>Moves the activity to the trash. Reversible — the permanent purge lives behind /api/trash.</summary>
    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ApiError>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteActivity(int id)
    {
        var result = await _activityCommand.DeleteActivityAsync(new TrackId(id));
        if (!result)
            return NotFound(ApiError.NotFound("Activity not found"));

        return NoContent();
    }

    // Upper bound on a single bulk selection — matches the grid's own max page size (GetGrid clamps
    // PageSize to 10000), so "select all filtered" on a full page can't exceed it. The cap is what stops a
    // hand-rolled request from turning into an unbounded IN (...) over the DB. A `bulk/` subpath keeps these
    // off the `{id}` templates (this route prefix is shared with ActivityLifecycleApiController).
    private const int MaxTrackIds = 10000;

    /// <summary>Bulk visibility/type change from the grid's Select mode. Owner-scoped: ids the caller
    /// doesn't own are silently skipped and not counted (so the response count is authoritative).</summary>
    [HttpPost("bulk/update")]
    [ProducesResponseType<BulkUpdateActivitiesResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> BulkUpdate([FromBody] BulkUpdateActivitiesRequest request)
    {
        if (request is null || Validate(request.TrackIds) is not { } ids)
            return BadRequest(ApiError.BadRequest($"TrackIds must be 1-{MaxTrackIds} ids"));

        var activityType = request.ActivityType?.Trim();
        // CA1308: visibility normalizes to the canonical lowercase token ("public"/"friends"/"private", see
        // Visibilities) that is validated and stored; ToUpperInvariant would fail IsValid and corrupt storage.
#pragma warning disable CA1308
        var visibility = request.Visibility?.Trim().ToLowerInvariant();
#pragma warning restore CA1308

        // Same field bounds as the single-item PATCH (UpdateActivity) — no Name here: bulk-renaming many
        // rows to one string is a footgun, not a feature.
        if (activityType != null && (string.IsNullOrWhiteSpace(activityType) || activityType.Length > 50))
            return BadRequest(ApiError.BadRequest("ActivityType must be 1-50 characters"));
        if (visibility != null && !Visibilities.IsValid(visibility))
            return BadRequest(ApiError.BadRequest("Visibility must be 'private', 'friends', or 'public'"));
        if (activityType == null && visibility == null)
            return BadRequest(ApiError.BadRequest("At least one field must be provided"));

        var updated = await _activityCommand.UpdateManyAsync(ids.Select(i => new TrackId(i)).ToList(), activityType, visibility);
        return Ok(new BulkUpdateActivitiesResponse(updated));
    }

    /// <summary>Bulk move-to-trash from the grid's Select mode. Reversible; the permanent purge lives
    /// behind /api/trash. Owner-scoped; unowned ids are silently skipped.</summary>
    [HttpPost("bulk/delete")]
    [ProducesResponseType<BulkDeleteActivitiesResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> BulkDelete([FromBody] BulkDeleteActivitiesRequest request)
    {
        var ids = Validate(request?.TrackIds);
        if (ids == null)
            return BadRequest(ApiError.BadRequest($"TrackIds must be 1-{MaxTrackIds} ids"));

        var deleted = await _activityCommand.DeleteManyAsync(ids.Select(i => new TrackId(i)).ToList());
        return Ok(new BulkDeleteActivitiesResponse(deleted));
    }

    private static List<int>? Validate(List<int>? ids) =>
        ids is null || ids.Count == 0 || ids.Count > MaxTrackIds ? null : ids.Distinct().ToList();

    [HttpPost("{id}/refresh-elevation")]
    [RejectCrossSiteBrowserRequests] // route-only POST — cross-site form/fetch-able without preflight (PI-01)
    [ProducesResponseType<RefreshElevationResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiError>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiError>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RefreshElevation(int id)
    {
        try
        {
            // Ownership, not visibility: refresh mutates the owner's data (DEM overwrite, smoothing,
            // stat recompute), so a friend/public viewer must not trigger it. 404 = no existence leak.
            if (!await _activityQuery.IsActivityOwnedAsync(new TrackId(id)))
                return NotFound(ApiError.NotFound("Activity not found"));

            var result = await _elevationRefresh.RefreshElevationAsync(new TrackId(id));
            if (result is null)
                return BadRequest(ApiError.BadRequest("Activity has no GPS points")); // EL-08: nothing was refreshed

            // Recomputed totals live in cached slices/coords — evict so the refresh is visible immediately.
            _invalidator.InvalidateUser(_currentUser.GetUserId());
            // pointsReplaced == 0 → the DEM source yielded nothing (endpoint down / no coverage) — the
            // client surfaces that instead of pretending success. EL-08.
            return Ok(new RefreshElevationResponse(
                Message: "Elevation refreshed successfully",
                PointsTotal: result.PointsTotal,
                PointsReplaced: result.PointsReplaced));
        }
        catch (InvalidOperationException ex)
        {
            // EL-10: a non-terminal recording session still owns the staging tail — refresh is refused
            // until the session finishes. TS already alerts any non-OK ApiError body.
            return Conflict(ApiError.Conflict(ex.Message));
        }
    }
}
