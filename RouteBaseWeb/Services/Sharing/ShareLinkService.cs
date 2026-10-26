using RouteBaseWeb.Helpers;
using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Sharing;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Services.Races;
using Microsoft.EntityFrameworkCore;

namespace RouteBaseWeb.Services.Sharing;

/// <inheritdoc cref="IShareLinkService" />
public sealed class ShareLinkService(
    RouteBaseDbContext db,
    IVisibilityService visibility,
    IRecordingService recording,
    IRaceAccess races,
    TimeProvider clock) : IShareLinkService
{
    /// <summary>
    /// Cap on LIVE links per resource. Not a security boundary — it stops mint being used as a cheap write
    /// amplifier against SQLite, and keeps the owner's management list comprehensible. Revoked and expired
    /// rows do not count, so an owner can always re-share.
    /// </summary>
    private const int MaxLiveLinksPerResource = 10;

    /// <summary>How many DEAD links the management list shows per resource. Not a data limit — the rows
    /// stay and still count toward the lifetime total; this only stops the dialog turning into a log.</summary>
    private const int MaxEndedLinksListed = 5;

    public async Task<ShareMintResult> MintAsync(
        string kind, int resourceId, string? ttlName, string userId, CancellationToken ct)
    {
        if (!TryParseKind(kind, out var parsedKind))
            return ShareMintResult.Fail(ShareMintOutcome.BadRequest);

        var ttl = ShareTtl.Parse(ttlName);
        if (ttl is null)
            return ShareMintResult.Fail(ShareMintOutcome.BadRequest);

        // OWNERSHIP, via Owned* rather than Visible*: seeing a friend's ride must not let you publish it.
        // Not-yours and not-there collapse to the same answer so this cannot enumerate other people's ids.
        //
        // Filtered on the userId PARAMETER, not just on Owned*'s ambient identity. Owned* resolves
        // ICurrentUserService.GetUserId() internally, so authorizing on that while persisting `userId`
        // below would be authorizing one identity and writing another — a confused deputy waiting for the
        // day someone calls this under an impersonation scope. The parameter pins WHO.
        //
        // Owned* carries NO lifecycle clause (the trash/review slices depend on that), so a link CAN be
        // minted for a trashed or not-yet-released activity. That is accepted: every share READ gates on
        // lifecycle (the page via VisibleActivities, the photo subresources explicitly, the segment reader
        // in OwnerEfforts), so such a link serves nothing until the activity is Released.
        var owns = parsedKind switch
        {
            ShareKind.Activity => await visibility.OwnedActivities(db.Activities)
                .AnyAsync(a => a.TrackId == new TrackId(resourceId) && a.UserId == userId, ct),
            ShareKind.Segment => await visibility.OwnedSegments(db.Segments)
                .AnyAsync(s => s.SegmentId == new SegmentId(resourceId) && s.UserId == userId, ct),
            // A live-share is keyed by the Activity shell's track_id, but only where a RecordingSession
            // actually exists for it — so "live" can be minted for a recording, never for a plain activity
            // that has the same id space. Owned + has-session are ANDed under the userId parameter for the
            // same confused-deputy reason the other branches pin WHO. The has-session half routes through
            // the recording context's own seam (Finding-1), not a raw db read from the sharing slice.
            ShareKind.LiveRecording =>
                await visibility.OwnedActivities(db.Activities)
                    .AnyAsync(a => a.TrackId == new TrackId(resourceId) && a.UserId == userId, ct)
                && await recording.HasSessionForTrackAsync(new TrackId(resourceId), userId, ct),
            // Through the race context's own seam (the live arm's Finding-1 precedent), not a raw db read
            // from the sharing slice — and on the userId PARAMETER, so this cannot authorize one identity
            // and persist another. Ownership, not the race's edit policy: a collaborator who may edit a
            // race must not be able to publish it.
            ShareKind.Race => await races.OwnsRaceAsync(new RaceId(resourceId), userId, ct),
            _ => false,
        };
        if (!owns)
            return ShareMintResult.Fail(ShareMintOutcome.NotFound);

        var now = clock.GetUtcNow().UtcDateTime;
        var live = await LinksFor(parsedKind, resourceId)
            .Where(s => s.UserId == userId && s.RevokedAt == null && s.ExpiresAt > now)
            .CountAsync(ct);
        if (live >= MaxLiveLinksPerResource)
            return ShareMintResult.Fail(ShareMintOutcome.TooMany);

        // Generated here and returned once. Only the hash is persisted.
        var token = ShareTokens.New();
        var row = new ShareLink
        {
            TokenHash = ShareTokens.Hash(token),
            Kind = KindName(parsedKind),
            // Activity and live both key on track_id (a live session is 1:1 with its Activity shell).
            // Exactly one of the three is set, matching Kind — the invariant ShareLink.cs states and
            // ShareLinkServiceTests pins per kind.
            TrackId = parsedKind is ShareKind.Activity or ShareKind.LiveRecording ? new TrackId(resourceId) : (TrackId?)null,
            SegmentId = parsedKind == ShareKind.Segment ? new SegmentId(resourceId) : (SegmentId?)null,
            RaceId = parsedKind == ShareKind.Race ? new RaceId(resourceId) : (RaceId?)null,
            UserId = userId,
            CreatedAt = now,
            ExpiresAt = now + ttl.Duration,
        };

        db.ShareLinks.Add(row);
        await db.SaveChangesAsync(ct);

        return new ShareMintResult(ShareMintOutcome.Ok, token, row.ShareLinkId, row.ExpiresAt);
    }

    public async Task<ShareLinkListResponse> ListAsync(
        string kind, int resourceId, string userId, CancellationToken ct)
    {
        if (!TryParseKind(kind, out var parsedKind)) return new ShareLinkListResponse([], 0);

        var rows = await LinksFor(parsedKind, resourceId)
            .Where(s => s.UserId == userId)
            .OrderByDescending(s => s.CreatedAt)
            .AsNoTracking()
            .ToListAsync(ct);

        // Every row counts toward the total, including the ended ones the cap below hides, AND — for a
        // track — the sibling kind's rows: the badge merges activity+live opens (ShareOpensAsync), and
        // TotalOpens is defined as the badge's number, so it sums the same merged bucket.
        var totalOpens = await BadgeLinksFor(parsedKind, resourceId)
            .Where(s => s.UserId == userId)
            .SumAsync(s => (int?)s.OpenCount, ct) ?? 0;

        // ToUtcOffset on every timestamp: the DB round-trip strips DateTimeKind, and the implicit
        // DateTime→DateTimeOffset conversion would read Unspecified as server-LOCAL time (CC2-04/AR-02).
        var now = clock.GetUtcNow().UtcDateTime;
        var listed = rows
            .Select(r => new ShareLinkSummaryDto(
                r.ShareLinkId,
                r.Kind,
                resourceId,
                r.CreatedAt.ToUtcOffset(),
                r.ExpiresAt.ToUtcOffset(),
                r.RevokedAt.ToUtcOffset(),
                IsActive: r.RevokedAt is null && r.ExpiresAt > now,
                r.OpenCount,
                r.LastOpenedAt.ToUtcOffset()))
            .ToList();

        // Active links are never hidden — one of them is the thing the owner came here to revoke. Ended
        // ones are capped: opened links are kept forever now, so a long-shared ride accumulates more dead
        // rows than a dialog can usefully show. Already newest-first, so Take() keeps the recent ones.
        var capped = listed.Where(l => l.IsActive)
            .Concat(listed.Where(l => !l.IsActive).Take(MaxEndedLinksListed))
            .OrderByDescending(l => l.CreatedAt)
            .ToList();

        return new ShareLinkListResponse(capped, totalOpens);
    }

    public async Task<IReadOnlyDictionary<int, ShareOpens>> ShareOpensAsync(
        string kind, string userId, CancellationToken ct)
    {
        if (!TryParseKind(kind, out var parsedKind)) return new Dictionary<int, ShareOpens>();

        // Ownership once, then each surface projects its OWN key column — the same column-per-kind
        // discipline LinksFor uses. Every branch is constant-shaped SQL with one parameter, so the
        // statement cache hits no matter how big the caller's page is.
        //
        // NO liveness filter: opens are lifetime, so a revoked or expired link still contributes its count
        // (the sweep keeps opened rows for exactly this). Liveness is carried per row instead and folded
        // into ShareOpens.Active below.
        var mine = db.ShareLinks.Where(s => s.UserId == userId);

        // EF projections behind explicit null filters keep `!` deliberately (H2): .Value and
        // (int) casts both warn CS8629 inside expression trees, and ?? default would hide a broken
        // DB invariant as id 0. The per-directory pin documents these as floor.
        var rows = parsedKind switch
        {
            ShareKind.Segment => mine
                .Where(s => s.SegmentId != null && s.Kind == ShareKindNames.Segment)
                .Select(s => new ResourceOpens(s.SegmentId!.Value.Value, s.OpenCount, s.RevokedAt, s.ExpiresAt)),
            ShareKind.Race => mine
                .Where(s => s.RaceId != null && s.Kind == ShareKindNames.Race)
                .Select(s => new ResourceOpens(s.RaceId!.Value.Value, s.OpenCount, s.RevokedAt, s.ExpiresAt)),
            // Activity and live share one column AND one meaning here: either kind publishes the same
            // ride's map, so either badges the activity — and their opens ADD UP for the same reason. This
            // is the one place the two are deliberately NOT held apart, unlike the mint cap, where per-kind
            // scoping stops one crowding out the other. It is also what carries a finished live share's
            // views onto the ride: the live link dies at its 24h TTL, its row stays, and the count remains
            // on the same track_id bucket.
            ShareKind.Activity or ShareKind.LiveRecording => mine
                .Where(s => s.TrackId != null
                         && (s.Kind == ShareKindNames.Activity || s.Kind == ShareKindNames.Live))
                .Select(s => new ResourceOpens(s.TrackId!.Value.Value, s.OpenCount, s.RevokedAt, s.ExpiresAt)),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        // Summed in memory rather than as a GROUP BY: the row set is one row per link the user has ever
        // minted for this surface (live ones bounded by MaxLiveLinksPerResource, dead ones only kept once
        // opened), so the aggregate is over tens of rows, and the emitted SQL stays the flat
        // constant-shaped SELECT the no-id-list design exists to keep.
        var now = clock.GetUtcNow().UtcDateTime;
        return (await rows.ToListAsync(ct))
            .GroupBy(r => r.ResourceId)
            .ToDictionary(
                g => g.Key,
                g => new ShareOpens(g.Sum(r => r.Opens), g.Any(r => r.IsLive(now))));
    }

    /// <summary>One link's key column, its open count and the two fields liveness is derived from, before
    /// the per-resource fold.</summary>
    private sealed record ResourceOpens(int ResourceId, int Opens, DateTime? RevokedAt, DateTime ExpiresAt)
    {
        public bool IsLive(DateTime now) => RevokedAt is null && ExpiresAt > now;
    }

    public async Task<ShareLinkInventoryResponse> ActiveLinksAsync(string userId, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        // Liveness and ownership once, then each kind projects its OWN key column and joins its OWN table —
        // the column-per-kind discipline LinksFor and ShareOpensAsync already use. Three constant-shaped
        // queries beat one id-list pass: EF binds a parameter per element, so a name lookup over
        // `ids.Contains(...)` would emit a different SQL string for every distinct link count.
        var mine = db.ShareLinks
            .Where(s => s.UserId == userId && s.RevokedAt == null && s.ExpiresAt > now);

        // LEFT joins, not inner: all three FKs cascade, so a missing row cannot normally happen — but an
        // inventory that silently drops a link because its name lookup missed would be lying about the one
        // thing it exists to report. A nameless row still lists, under its id.
        var activities =
            from s in mine
            where s.TrackId != null
               && (s.Kind == ShareKindNames.Activity || s.Kind == ShareKindNames.Live)
            join a in db.Activities on s.TrackId equals a.TrackId into found
            from a in found.DefaultIfEmpty()
            select new InventoryRow(
                s.ShareLinkId, s.Kind, s.TrackId!.Value.Value, a.TrackName,
                s.CreatedAt, s.ExpiresAt, s.OpenCount, s.LastOpenedAt);

        var segments =
            from s in mine
            where s.SegmentId != null && s.Kind == ShareKindNames.Segment
            join g in db.Segments on s.SegmentId equals g.SegmentId into found
            from g in found.DefaultIfEmpty()
            select new InventoryRow(
                s.ShareLinkId, s.Kind, s.SegmentId!.Value.Value, g.SegmentName,
                s.CreatedAt, s.ExpiresAt, s.OpenCount, s.LastOpenedAt);

        var races =
            from s in mine
            where s.RaceId != null && s.Kind == ShareKindNames.Race
            join r in db.Races on s.RaceId equals r.RaceId into found
            from r in found.DefaultIfEmpty()
            select new InventoryRow(
                s.ShareLinkId, s.Kind, s.RaceId!.Value.Value, r.RaceName,
                s.CreatedAt, s.ExpiresAt, s.OpenCount, s.LastOpenedAt);

        var rows = new List<InventoryRow>();
        rows.AddRange(await activities.ToListAsync(ct));
        rows.AddRange(await segments.ToListAsync(ct));
        rows.AddRange(await races.ToListAsync(ct));

        // Ordered in memory: the three kind queries cannot be UNIONed without giving up their own joins,
        // and the set is bounded by the caller's live links (tens), so the sort is free.
        // ToUtcOffset: see ListAsync. The countdown this page renders ("Expires in 21 hours") is computed
        // in the browser from ExpiresAt, so a mis-offset timestamp does not just display wrong — it makes
        // the remaining lifetime wrong by the viewer's UTC offset, the one number here that matters.
        var links = rows
            .OrderByDescending(r => r.CreatedAt)
            .Select(r => new ShareLinkInventoryItemDto(
                r.Id, r.Kind, r.ResourceId, DisplayName(r), r.CreatedAt.ToUtcOffset(), r.ExpiresAt.ToUtcOffset(),
                r.OpenCount, r.LastOpenedAt.ToUtcOffset()))
            .ToList();

        return new ShareLinkInventoryResponse(links);
    }

    /// <summary>The resource's own name, or its id when it has none — an unnamed row must still be
    /// identifiable enough to decide whether to revoke it.</summary>
    private static string DisplayName(InventoryRow row) =>
        string.IsNullOrWhiteSpace(row.ResourceName) ? $"#{row.ResourceId}" : row.ResourceName;

    /// <summary>One live link joined to its resource's name, before the kinds are merged and ordered.</summary>
    private sealed record InventoryRow(
        int Id, string Kind, int ResourceId, string? ResourceName,
        DateTime CreatedAt, DateTime ExpiresAt, int OpenCount, DateTime? LastOpenedAt);

    public async Task<bool> RevokeAsync(int id, string userId, CancellationToken ct)
    {
        // Scoped to the caller in the WHERE clause, not checked after loading: a link that is not yours is
        // indistinguishable from one that does not exist.
        var row = await db.ShareLinks
            .FirstOrDefaultAsync(s => s.ShareLinkId == id && s.UserId == userId, ct);
        if (row is null) return false;

        // Idempotent: re-revoking keeps the original timestamp rather than moving the audit trail.
        if (row.RevokedAt is null)
        {
            row.RevokedAt = clock.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(ct);
        }

        // No cache eviction needed: revocation is enforced in ShareLinkAccess.ResolveAsync during
        // AUTHENTICATION, before any endpoint runs and therefore before any cache is read. Nothing is
        // keyed by token. This holds only while there is no grant cache — do not add one.
        return true;
    }

    // Each kind is looked up on ITS OWN column, and a resource's live count is scoped to its own kind — an
    // activity's share links never crowd out its live-share links, and vice versa. Live and activity are
    // the one pair that shares a column (track_id), so only they need the Kind clause to tell them apart.
    // Kinds listed explicitly (closed vocabulary, like the mint switch): a future kind must choose its
    // bucket here, not silently inherit the track one.
    private IQueryable<ShareLink> LinksFor(ShareKind kind, int resourceId) => kind switch
    {
        ShareKind.Segment => db.ShareLinks.Where(s => s.SegmentId == new SegmentId(resourceId) && s.Kind == ShareKindNames.Segment),
        ShareKind.Race => db.ShareLinks.Where(s => s.RaceId == new RaceId(resourceId) && s.Kind == ShareKindNames.Race),
        ShareKind.Activity or ShareKind.LiveRecording =>
            db.ShareLinks.Where(s => s.TrackId == new TrackId(resourceId) && s.Kind == KindName(kind)),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    // The rows a resource's BADGE counts — LinksFor's buckets, except activity and live merge: their opens
    // add up on the track (ShareOpensAsync's one deliberate un-separation), so either kind's dialog must
    // total both or it would contradict the very badge TotalOpens exists to match.
    private IQueryable<ShareLink> BadgeLinksFor(ShareKind kind, int resourceId) => kind switch
    {
        ShareKind.Activity or ShareKind.LiveRecording => db.ShareLinks.Where(s =>
            s.TrackId == new TrackId(resourceId)
            && (s.Kind == ShareKindNames.Activity || s.Kind == ShareKindNames.Live)),
        _ => LinksFor(kind, resourceId),
    };

    private static string KindName(ShareKind kind) => kind switch
    {
        ShareKind.Activity => ShareKindNames.Activity,
        ShareKind.Segment => ShareKindNames.Segment,
        ShareKind.LiveRecording => ShareKindNames.Live,
        ShareKind.Race => ShareKindNames.Race,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static bool TryParseKind(string? kind, out ShareKind parsed)
    {
        switch (kind)
        {
            case ShareKindNames.Activity: parsed = ShareKind.Activity; return true;
            case ShareKindNames.Segment: parsed = ShareKind.Segment; return true;
            case ShareKindNames.Live: parsed = ShareKind.LiveRecording; return true;
            case ShareKindNames.Race: parsed = ShareKind.Race; return true;
            default: parsed = default; return false;
        }
    }
}
