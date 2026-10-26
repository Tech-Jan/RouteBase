using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Lifecycle;
using RouteBaseWeb.Services.Segments;
using RouteBaseWeb.Services.Tracks;

namespace RouteBaseWeb.Services.Sharing;

/// <summary>
/// Owner-only segment reads for the share path. See <see cref="IShareSegmentReader"/> for why this exists
/// rather than reusing <see cref="SegmentEfforts"/> under a widened identity.
///
/// Two structural properties, both pinned by tests:
///
/// 1. It injects NEITHER <c>IVisibilityService</c> NOR <c>ICurrentUserService</c>. There is no ambient
///    identity here to get wrong; the authority is the <see cref="ShareGrant"/>, passed in as data. That
///    absence is the invariant — reaching for <c>VisibleActivities</c> to "fix a missing effort" would
///    require adding a constructor dependency, which fails the build.
///
/// 2. It runs as <c>share:anon</c>, OUTSIDE <c>WidenAsync</c>, and caches under its own
///    <c>CacheKeys.ShareSegment*</c> family. Running it widened would make it read the owner's own cache
///    namespace — which their normal page filled with their friends' efforts, and which every
///    SegmentEfforts read consults BEFORE its visibility gate.
/// </summary>
public sealed class ShareSegmentReader(
    RouteBaseDbContext db,
    IMemoryCache cache,
    SegmentCache segCache,
    ISegmentEffortPerformanceBuilder perfBuilder,
    ITrackReader trackReader) : IShareSegmentReader
{
    /// <summary>Hard ceiling on one tracks page, regardless of what the caller asks for.</summary>
    public const int MaxTracksPerRequest = 150;

    /// <summary>Hard ceiling on how many tracks a share view will ever serve for one segment.</summary>
    public const int MaxTracks = 200;

    /// <summary>Per-track point cap. 0 from the caller means this, never full resolution.</summary>
    public const int DefaultMaxPointsPerTrack = 2000;

    // ---- the three predicates, defined once ------------------------------------------------------

    /// <summary>
    /// The granted segment. Ownership is re-asserted at READ time: MintAsync guarantees owner ==
    /// segment.UserId at mint, but nothing re-checks it afterwards and Segment.UserId is nullable, so a
    /// transferred or orphaned segment must fall out of every live link.
    ///
    /// NOT VisibleSegments — that predicate answers "what may the current user see", the wrong question
    /// here twice over: under share:anon it would admit every PUBLIC segment (segments can be public now
    /// that SegmentCrud writes the visibility column), and under an owner scope it would admit the owner's
    /// friends' and public segments too. The share authority is the grant's owner+segment pair, nothing else.
    /// </summary>
    private IQueryable<Segment> GrantedSegment(ShareGrant g) => db.Segments.AsNoTracking()
        .Where(s => s.SegmentId == new SegmentId(g.ResourceId) && s.UserId == g.OwnerUserId);

    /// <summary>
    /// The owner's own efforts. The friends/public disjunction does not appear here AT ALL — this is the
    /// line a future contributor must not "fix" into VisibleActivities.
    ///
    /// Lifecycle == released is restated explicitly because we are deliberately not going through
    /// VisibleActivities, which carries it: an owner's trashed / parked / awaiting-review activity must
    /// not surface on a public link.
    /// </summary>
    private IQueryable<GpsInSegment> OwnerEfforts(ShareGrant g) => db.GpsInSegments.AsNoTracking()
        .Where(e => e.SegmentId == new SegmentId(g.ResourceId))
        .Where(e => db.Activities.Any(a => a.TrackId == e.TrackId
                                        && a.UserId == g.OwnerUserId
                                        && a.Lifecycle == LifecycleStates.Released));

    /// <summary>
    /// THE permitted set. One definition drives the leaderboard, the totals, the charts, the replay and
    /// the map, so the five can never disagree.
    ///
    /// Excluded efforts are anti-joined OUT rather than marked (as the authenticated grid does): a share
    /// view has no Restore action, so a marked-excluded row would be dead UI.
    /// </summary>
    private IQueryable<GpsInSegment> PermittedEfforts(ShareGrant g) =>
        OwnerEfforts(g).Where(SegmentEffortProjection.NotExcluded(db));

    /// <summary>
    /// The permitted TRACK ids in a deterministic order — first (min) effort time, ties by id — shared by
    /// every paging call so slices agree across requests. GroupBy-min rather than OrderBy+Distinct: DISTINCT
    /// cannot preserve an ordering whose columns it erases from the projection, so that shape pages over an
    /// UNDEFINED order and slices can duplicate or miss tracks between requests.
    /// </summary>
    private IQueryable<TrackId> OrderedPermittedTrackIds(ShareGrant g) =>
        PermittedEfforts(g)
            .GroupBy(e => e.TrackId)
            .Select(grp => new { TrackId = grp.Key, FirstEffort = grp.Min(e => e.EffortTime) })
            .OrderBy(x => x.FirstEffort).ThenBy(x => x.TrackId)
            .Select(x => x.TrackId);

    private static bool WrongKind(ShareGrant g) => g.Kind != ShareKind.Segment;

    // ---- reads -----------------------------------------------------------------------------------

    public async Task<Segment?> GetSegmentAsync(ShareGrant g, CancellationToken ct = default)
    {
        if (WrongKind(g)) return null;
        // Ordered by SegGpsId, matching SegmentCrud.GetSegmentWithGpsAsync — the polyline is a SEQUENCE,
        // and an unordered Include would hand the map a scribble on this route only.
        return await GrantedSegment(g)
            .Include(s => s.GpsData.OrderBy(p => p.SegGpsId))
            .FirstOrDefaultAsync(ct);
    }

    public async Task<List<SegmentEffortDetailed>> GetOwnerEffortsAsync(ShareGrant g, CancellationToken ct = default)
    {
        if (WrongKind(g)) return [];

        // GATE BEFORE CACHE, like GetPerformanceAsync and GetTracksAsync. The read-time ownership
        // re-assertion GrantedSegment documents ("a transferred or orphaned segment must fall out of every
        // live link") is only as live as the last thing that runs before the return — consulting the cache
        // first would let a warm entry answer for up to MaxLifetime after the segment stopped being the
        // grant owner's. One indexed EXISTS on a hit is the whole cost; the full row (incl. the shapes
        // match-rules JSON) is loaded only on a miss, where FromAggregates needs it.
        if (!await GrantedSegment(g).AnyAsync(ct)) return [];

        var key = CacheKeys.ShareSegmentEfforts(g.OwnerUserId, new SegmentId(g.ResourceId));
        if (cache.TryGetValue(key, out List<SegmentEffortDetailed>? hit) && hit != null) return hit;

        var segment = await GrantedSegment(g).FirstOrDefaultAsync(ct);
        if (segment == null) return [];

        var rows = await PermittedEfforts(g).ToListAsync(ct);
        if (rows.Count == 0)
        {
            // The empty answer is cached like a non-empty one — a zero-effort share must not re-run the
            // whole permitted-set query per open. Same eviction: owner token + InvalidateSegment + TTL.
            List<SegmentEffortDetailed> none = [];
            cache.Set(key, none, segCache.OwnerTokenOptions(SegmentCache.ShortTtl, g.OwnerUserId));
            return none;
        }

        var trackIds = rows.Select(r => r.TrackId).Distinct().ToList();
        var activities = await db.Activities.AsNoTracking()
            .Where(a => trackIds.Contains(a.TrackId))
            .ToDictionaryAsync(a => a.TrackId, ct);

        var result = new List<SegmentEffortDetailed>(rows.Count);
        foreach (var row in rows)
        {
            if (row.ResolveGpsRange() is not (int entry, int exit)) continue;
            if (!activities.TryGetValue(row.TrackId, out var activity)) continue;
            if (!SegmentEffortProjection.HasStoredAggregates(row)) continue;

            // currentUserId is deliberately a value that matches NOBODY, so IsOwnEffort is false on every
            // row. The share page has no "You" to speak of, and the effort rows carry no display name —
            // the Activity nav property is not loaded at all, so UserDisplayName falls back to the raw
            // UserId, which the projection below strips.
            var effort = SegmentEffortProjection.FromAggregates(
                row, activity, segment, entry, exit,
                currentUserId: string.Empty, excluded: false);

            // Identity is stripped explicitly rather than left to fall out of the projection: these three
            // fields are the AS-05 shape, and "it happens to be the owner anyway" is not a property a
            // future refactor preserves.
            effort.UserId = null;
            effort.UserDisplayName = null;
            effort.IsOwnEffort = false;
            result.Add(effort);
        }

        var ordered = result.OrderBy(e => e.ElapsedTime).ToList();
        cache.Set(key, ordered, segCache.OwnerTokenOptions(SegmentCache.ShortTtl, g.OwnerUserId));
        return ordered;
    }

    public async Task<SegmentEffortTotals?> GetOwnerEffortTotalsAsync(ShareGrant g, CancellationToken ct = default)
    {
        if (WrongKind(g)) return null;

        // Gate before cache — see GetOwnerEffortsAsync.
        if (!await GrantedSegment(g).AnyAsync(ct)) return null;

        var key = CacheKeys.ShareSegmentTotals(g.OwnerUserId, new SegmentId(g.ResourceId));
        if (cache.TryGetValue(key, out SegmentEffortTotals? hit) && hit != null) return hit;

        // Same source as the leaderboard, so the totals box and the row list cannot disagree — and no
        // aggregate about any other rider is computed at any point.
        var totals = await SegmentEffortTotalsProjection.SumAsync(PermittedEfforts(g), ct);
        if (totals != null)
            cache.Set(key, totals, segCache.OwnerTokenOptions(SegmentCache.ShortTtl, g.OwnerUserId));
        return totals;
    }

    public async Task<List<EffortPerformanceData>> GetPerformanceAsync(
        ShareGrant g, IReadOnlySet<GpsInSegmentId> effortIds, int maxPointsPerEffort, CancellationToken ct = default)
    {
        if (WrongKind(g) || effortIds.Count == 0) return [];
        if (!await GrantedSegment(g).AnyAsync(ct)) return [];

        var result = new List<EffortPerformanceData>(effortIds.Count);
        var coldIds = new List<GpsInSegmentId>();
        foreach (var id in effortIds)
        {
            var key = CacheKeys.ShareSegmentPerfEffort(g.OwnerUserId, new SegmentId(g.ResourceId), id, maxPointsPerEffort);
            if (cache.TryGetValue(key, out EffortPerformanceData? hit) && hit != null) result.Add(hit);
            else coldIds.Add(id);
        }
        if (coldIds.Count == 0) return result;

        // THE INTERSECTION: the caller's ids are filtered through the permitted set. Ids outside it simply
        // do not come back — never a 404, which would confirm which ids exist.
        var rows = await PermittedEfforts(g)
            .Where(e => coldIds.Contains(e.GpsInSegmentId))
            .ToListAsync(ct);
        if (rows.Count == 0) return result;

        var trackIds = rows.Select(r => r.TrackId).Distinct().ToList();
        var activities = await db.Activities.AsNoTracking()
            .Where(a => trackIds.Contains(a.TrackId))
            .ToDictionaryAsync(a => a.TrackId, ct);

        var authorized = rows
            .Where(r => activities.ContainsKey(r.TrackId))
            .Select(r => (Row: r, Activity: activities[r.TrackId]))
            .ToList();

        var built = await perfBuilder.BuildAsync(new SegmentId(g.ResourceId), authorized, maxPointsPerEffort, ct);

        var opts = segCache.OwnerTokenOptions(SegmentCache.ShortTtl, g.OwnerUserId);
        foreach (var epd in built)
        {
            // Same identity strip as the leaderboard — this DTO carries UserId/UserDisplayName too.
            epd.UserId = null;
            epd.UserDisplayName = null;
            var scrubbed = epd;
            cache.Set(
                CacheKeys.ShareSegmentPerfEffort(g.OwnerUserId, new SegmentId(g.ResourceId), new GpsInSegmentId(scrubbed.EffortId), maxPointsPerEffort),
                scrubbed, opts);
            result.Add(scrubbed);
        }
        return result;
    }

    // Serves each effort's WHOLE activity track, not just the segment portion — the off-segment lead-in and
    // lead-out (drawn dimmed client-side) are the context that makes a segment map readable. That is a
    // deliberate product decision (decided 2026-07-24), and it is the one place a shared SEGMENT
    // publishes whole ACTIVITIES: sharing a segment therefore consents to publishing where those rides start
    // and end — usually home. Point resolution is capped for the same reason (the caller does not choose how
    // precisely endpoints resolve — see ShareSegmentReadController.ClampBulkPoints). Not an oversight; a
    // future reviewer will find this and want to trim it to the segment slice — this comment is the answer.
    public async Task<ShareSegmentTracksResult> GetTracksAsync(
        ShareGrant g, int skip, int take, int maxPointsPerTrack, CancellationToken ct = default)
    {
        var empty = new ShareSegmentTracksResult([], 0);
        if (WrongKind(g)) return empty;
        if (!await GrantedSegment(g).AnyAsync(ct)) return empty;

        var total = Math.Min(await PermittedEfforts(g).Select(e => e.TrackId).Distinct().CountAsync(ct), MaxTracks);
        if (total == 0) return empty;

        var pageIds = await PermittedPageAsync(g, skip, take, ct);

        var points = maxPointsPerTrack > 0 ? maxPointsPerTrack : DefaultMaxPointsPerTrack;
        var tracks = new List<(int, List<GpsCoordinate>)>(pageIds.Count);
        foreach (var id in pageIds)
        {
            // Undecodable (null) and decoded-but-empty are both skipped here, as before — the page is a
            // best-effort set, not a promise of one entry per permitted id.
            var coords = await DecodeAsync(id, points, ct);
            if (coords is { Count: > 0 }) tracks.Add((id.Value, coords));
        }
        return new ShareSegmentTracksResult(tracks, total);
    }

    public async Task<List<GpsCoordinate>?> GetTrackAsync(
        ShareGrant g, TrackId trackId, int maxPoints, CancellationToken ct = default)
    {
        if (WrongKind(g)) return null;

        var permitted = await PermitAsync(g, trackId, ct);
        if (permitted is null) return null;

        return await DecodeAsync(permitted.Value, maxPoints > 0 ? maxPoints : DefaultMaxPointsPerTrack, ct);
    }

    // ---- the decode boundary ---------------------------------------------------------------------
    //
    // ITrackReader has NO visibility check of its own — its contract is "callers pre-authorize track ids"
    // (ITrackReader.cs), because it is the one shared decode cache serving segment matching, heatmaps,
    // races and photos. On this ANONYMOUS surface that makes the permitted-set check the entire boundary
    // between a link holder and every track in the database, and track ids are sequential.
    //
    // So the check is not a line next to the decode; it is the only way to obtain the ARGUMENT the decode
    // takes. The three members below are the whole boundary: two ways to prove permission, one place that
    // decodes. A future endpoint gets the gate by construction rather than by remembering.

    /// <summary>
    /// A track id that has passed the permitted-set check. Private and nested, so no code outside this
    /// class can mint one — which is what makes <see cref="DecodeAsync"/> unreachable from anywhere else.
    ///
    /// It does NOT make a bypass impossible: an edit inside this class could still write
    /// <c>new PermittedTrackId(callerSuppliedId)</c>, and C# offers no way to restrict construction to
    /// particular methods. That is the design, not a gap in it. The property being bought is REVIEW
    /// VISIBILITY: bypassing becomes a line someone has to write, which shows up in a diff, instead of a
    /// check someone forgets to write, which is an absence and shows up nowhere.
    /// </summary>
    private readonly record struct PermittedTrackId(int Value);

    /// <summary>
    /// One page of permitted ids, proven by DERIVATION — the ids come from
    /// <see cref="OrderedPermittedTrackIds"/> and no caller input reaches this query, so a foreign track
    /// cannot be returned however wrong a future filter becomes.
    ///
    /// Owns the window bounding as well, so the ceiling lives in one place: the cap binds the WINDOW, not
    /// just the reported Total. Without that, skip-paging past <see cref="MaxTracks"/> would walk the
    /// entire permitted set and the "hard ceiling" would bind only well-behaved clients.
    /// </summary>
    private async Task<List<PermittedTrackId>> PermittedPageAsync(
        ShareGrant g, int skip, int take, CancellationToken ct)
    {
        var boundedSkip = Math.Max(0, skip);
        if (boundedSkip >= MaxTracks) return [];
        var boundedTake = Math.Min(
            Math.Clamp(take <= 0 ? MaxTracksPerRequest : take, 1, MaxTracksPerRequest),
            MaxTracks - boundedSkip);

        var ids = await OrderedPermittedTrackIds(g)
            .Skip(boundedSkip)
            .Take(boundedTake)
            .ToListAsync(ct);

        return ids.Select(id => new PermittedTrackId(id.Value)).ToList();
    }

    /// <summary>
    /// One caller-supplied id, proven by INTERSECTION against the SAME <see cref="PermittedEfforts"/>
    /// definition the page derives from, so the two paths cannot drift. Null — never a 404 distinct from
    /// an unknown id — for anything outside the permitted set.
    ///
    /// Proving one id individually is why the page proves its own in BULK rather than looping through
    /// here: a 150-id page would otherwise cost 150 extra round-trips.
    /// </summary>
    private async Task<PermittedTrackId?> PermitAsync(ShareGrant g, TrackId trackId, CancellationToken ct)
        => await PermittedEfforts(g).AnyAsync(e => e.TrackId == trackId, ct)
            ? new PermittedTrackId(trackId.Value)
            : null;

    /// <summary>
    /// THE only call to <c>trackReader</c> in this class — pinned by
    /// <c>ShareSegmentReaderIsolationTests.TrackDecode_HappensInExactlyOnePlace</c>, which is what catches
    /// a SECOND decode path being added later (the failure the behavioural tests cannot see, because they
    /// only know about the endpoints that exist today).
    ///
    /// Takes <see cref="PermittedTrackId"/> rather than an int: there is no way to call it with a raw
    /// caller-supplied id without first minting a token above.
    ///
    /// NULL means "did not decode" and is distinct from an EMPTY list ("decoded, no usable points"). The
    /// single-track path turns the former into a 404 and the latter into an empty 200, so collapsing the
    /// two here would silently change the endpoint's contract.
    /// </summary>
    private async Task<List<GpsCoordinate>?> DecodeAsync(
        PermittedTrackId id, int maxPoints, CancellationToken ct)
    {
        var decoded = await trackReader.GetTrackAsync(new TrackId(id.Value), ct);
        return decoded == null ? null : TrackCoordinates.Downsample(decoded, maxPoints);
    }
}
