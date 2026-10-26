using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Services.Photos;
using RouteBaseWeb.Services.Sql;
using Microsoft.EntityFrameworkCore;

namespace RouteBaseWeb.Services;

public class DatabaseResetService : IDatabaseResetService
{
    private readonly RouteBaseDbContext _context;
    private readonly ILogger<DatabaseResetService> _logger;
    private readonly ICurrentUserService _currentUser;
    private readonly IPhotoImageStore? _photoStore;
    private readonly Heatmaps.IHeatmapLifecycle? _heatmaps;

    // Photo store / heatmap lifecycle are optional: production DI injects them; test constructions pass
    // none (they assert on rows, not files — same pattern as ActivityCommandService's TrackReaderCache).
    // Tile purge routes through IHeatmapLifecycle.PurgeTiles — the one door SegmentCrud's own segment-delete
    // purge already uses (arch finding D) — rather than talking to HeatmapTilePackStore directly.
    public DatabaseResetService(
        RouteBaseDbContext context,
        ILogger<DatabaseResetService> logger,
        ICurrentUserService currentUser,
        IPhotoImageStore? photoStore = null,
        Heatmaps.IHeatmapLifecycle? heatmaps = null)
    {
        _context     = context;
        _logger      = logger;
        _currentUser = currentUser;
        _photoStore  = photoStore;
        _heatmaps    = heatmaps;
    }

    /// <summary>
    /// Delete exactly the data groups requested for the current user only, in one transaction with
    /// foreign keys enforced — so every DDL <c>ON DELETE CASCADE</c> fires during the reset.
    ///
    /// Explicit deletes are needed for FK-less legacy tables (SQLite can't ALTER-ADD an FK):
    /// GPSinSegment, Segment_GPS — and for the track point stores (Track_Blob, Recording_Staging) and Photos
    /// on the Gps-only / Photos-only paths where the Activity parent is not deleted (all cascade when it is).
    /// Deleting the parents (Activity, Segment) then cascades to RecordingSession, Activity_Bbox, Heatmap,
    /// Heatmap_Tiles and Photos.
    /// A new child table that declares CASCADE in BOTH the EF model and DbSchemaManager DDL needs no
    /// edit here; only a new FK-less or user-keyed *root* table does. Table names + key columns are
    /// owned by ChildTableRegistry (CR5-05) — its parity test forces registration of new keyed tables.
    ///
    /// Multi_Day_Tour: Activity.multi_day_ID is nulled explicitly first (legacy Activity has no
    /// SET NULL FK) so the behaviour is identical across DB vintages.
    ///
    /// Sequence counters are NOT reset because other users' rows may still exist in the same
    /// tables — resetting would corrupt their auto-increment IDs.
    /// </summary>
    public async Task ResetDatabaseAsync(DeleteOptions opts)
    {
        string userId = _currentUser.GetUserId();
        var statements = new List<string>();

        // GPSinSegment (effort) rows are cross-user: own tracks × any segment, any track × own
        // segments. Wipe my efforts everywhere when my tracks go (Activities / SegmentMatches / Gps),
        // and everyone's efforts on my segments when those segments go (Segments). Must stay first —
        // it reads Activity/Segment before either parent is deleted.
        var effortConditions = new List<string>();
        if (opts.Activities || opts.SegmentMatches || opts.Gps)
            effortConditions.Add("track_ID IN (SELECT track_ID FROM Activity WHERE user_id = $userId)");
        if (opts.Segments)
            effortConditions.Add("segment_id IN (SELECT segment_ID FROM Segment WHERE user_id = $userId)");
        if (effortConditions.Count > 0)
        {
            // Special SQL (cross-user OR-conditions) — registry-covered tables (ChildTableRegistry
            // EffortsByTrack / EffortExclusionsByTrack), statement shape deliberately local (CR5-05).
            statements.Add($"DELETE FROM [GPSinSegment] WHERE {string.Join(" OR ", effortConditions)};");
            // Item 25: effort exclusions share the (segment_id, track_id) scoping and have no FK cascade —
            // wipe them wherever their effort rows are wiped (same conditions; columns are case-insensitive).
            statements.Add($"DELETE FROM [SegmentEffortExclusion] WHERE {string.Join(" OR ", effortConditions)};");
        }

        // Photos hang off the user's tracks. Deleting the Activity parent cascades them via FK, but the
        // Gps-only and Photos-only paths keep the activity, so delete by track subquery here (same scoping
        // as the legacy Pictures rule). Must precede the Activity delete (its subquery reads Activity).
        // Rendition files for these ids are gathered below and purged after commit (§1b).
        if (opts.Photos || opts.Activities || opts.Gps)
            statements.Add(ChildTableRegistry.PhotosByTrack.DeleteByUserTracksSql);

        if (opts.Activities || opts.Gps)
        {
            // Before parents — its subquery reads Activity; also needed standalone on the points-only path
            // (Activity survives there, so the track_id FK cascade never fires). Nothing else to reset: the
            // head is a flag ON the version rows, so it goes with them — a surviving Activity cannot be left
            // pointing at content that no longer exists.
            statements.Add(ChildTableRegistry.ActivityVersionsByTrack.DeleteByUserTracksSql);
            statements.Add(ChildTableRegistry.RecordingStagingByTrack.DeleteByUserTracksSql);
        }

        if (opts.MultiDayTours)
        {
            statements.Add("UPDATE [Activity] SET multi_day_ID = NULL WHERE multi_day_ID IN (SELECT multi_day_ID FROM Multi_Day_Tour WHERE user_id = $userId);");
            statements.Add(ChildTableRegistry.MultiDayToursByUser.DeleteByUserSql);
        }

        if (opts.Segments)
            statements.Add(ChildTableRegistry.SegmentGpsBySegment.DeleteByUserSegmentsSql);

        // Parents last — FK cascades clear RecordingSession, Activity_Bbox and Heatmap. (Heatmap tile
        // files live on the filesystem now; their ids are gathered below and purged after commit.)
        if (opts.Activities)
        {
            // Races are user-owned roots whose course may be a FRIEND's activity — the Race.track_id
            // cascade only takes races built on own rides, so wipe by user_id directly (idempotent
            // double-delete; cascades then clear participants/checkpoints/splits).
            statements.Add(ChildTableRegistry.RacesByUser.DeleteByUserSql);
            statements.Add(ChildTableRegistry.ActivitiesByUser.DeleteByUserSql);
            // Version rows are already gone (explicit delete above, or cascade), so the commit_id reference
            // cannot block deleting the commit rows here.
            statements.Add(ChildTableRegistry.EditSessionsByUser.DeleteByUserSql);
            statements.Add(ChildTableRegistry.EditCommitsByUser.DeleteByUserSql);
        }
        if (opts.Segments)
            statements.Add(ChildTableRegistry.SegmentsByUser.DeleteByUserSql);

        // Objects are shared content — reclaimed by refcount, never by user or track key. Runs last, after
        // every version delete above (including the Activity cascade), so it sees the final reference set.
        if (opts.Activities || opts.Gps)
            statements.Add("DELETE FROM [Track_Object] WHERE sha256 NOT IN (SELECT object_sha FROM Activity_Version);");

        var rawCredTypes = new List<string>();
        if (opts.StravaToken)    rawCredTypes.Add(CredentialTypes.StravaToken);
        if (opts.StravaApiKey)   rawCredTypes.Add(CredentialTypes.StravaCredentials);
        if (opts.MapTilerApiKey) rawCredTypes.Add(CredentialTypes.MapTilerCredentials);

        // Special SQL (Type-scoped) — registry-covered table (ChildTableRegistry.AppCredentialsByUser),
        // statement shape deliberately local (CR5-05).
        if (rawCredTypes.Count > 0)
            statements.Add("DELETE FROM [AppCredentials] WHERE [Type] IN (/*{credIn}*/) AND user_id = $userId;");

        if (opts.ProfileSettings)
            statements.Add(ChildTableRegistry.ProfileSettingsByUser.DeleteByUserSql);

        if (statements.Count == 0)
        {
            _logger.LogWarning("ResetDatabaseAsync called but nothing was selected");
            return;
        }

        // B8: reset means reset — every reset that deletes anything also drops the caller's diagnostic
        // error rows (see the ErrorLogByUser registry entry for why there is no checkbox). After the
        // nothing-selected guard on purpose: an empty selection must stay a no-op.
        statements.Add(ChildTableRegistry.ErrorLogByUser.DeleteByUserSql);

        // Gather the photo ids this reset will delete, so their filesystem renditions can be purged
        // after the row delete commits (§1b: row → then files; a leftover file is a harmless swept orphan).
        // Same track scoping as the Photos DELETE above.
        List<int> photoIdsToPurge = new();
        if (_photoStore != null && (opts.Photos || opts.Activities || opts.Gps))
        {
            photoIdsToPurge = await _context.Database
                .SqlQueryRaw<int>(
                    "SELECT photo_id AS Value FROM Photos WHERE track_id IN " +
                    "(SELECT track_ID FROM Activity WHERE user_id = {0})", userId)
                .ToListAsync();
        }

        // Same for heatmap tile files (one packed file per heatmap on the user's segments). Gathered
        // before the Segment delete cascades their rows away; files purged after commit.
        List<int> heatmapIdsToPurge = new();
        if (_heatmaps != null && opts.Segments)
        {
            heatmapIdsToPurge = await _context.Database
                .SqlQueryRaw<int>(
                    "SELECT heatmap_ID AS Value FROM Heatmap WHERE segment_ID IN " +
                    "(SELECT segment_ID FROM Segment WHERE user_id = {0})", userId)
                .ToListAsync();
        }

        // HM-17: a track-scoped reset (Activities/SegmentMatches/Gps) wipes the user's effort rows
        // everywhere — heatmaps painted from them (own AND friends' segments) would keep serving
        // imagery of deleted data while still marked `done`. Gather the affected segments before the
        // delete; their done heatmaps flip to `stale` after commit (SD-01 semantics: still viewable,
        // regenerable). Rows cascaded by an accompanying Segments wipe simply match nothing.
        List<SegmentId> heatmapSegmentsToStale = new();
        if (opts.Activities || opts.SegmentMatches || opts.Gps)
        {
            heatmapSegmentsToStale = (await _context.Database
                .SqlQueryRaw<int>(
                    "SELECT DISTINCT segment_id AS Value FROM GPSinSegment WHERE track_ID IN " +
                    "(SELECT track_ID FROM Activity WHERE user_id = {0})", userId)
                .ToListAsync()).Select(v => new SegmentId(v)).ToList();
        }

        // Pin one physical connection and run every delete in a single transaction, so a mid-batch
        // failure rolls back the whole reset. Foreign keys stay enforced (Microsoft.Data.Sqlite's
        // default) — the DDL cascades do the child cleanup; toggling the FK-enforcement PRAGMA is
        // both a no-op inside a transaction and a leak onto the pooled handle, so it is gone entirely.
        await _context.Database.OpenConnectionAsync();
        try
        {
            await using var tx = await _context.Database.BeginTransactionAsync();
            foreach (var stmt in statements)
            {
                var sb = new SqlBatch(stmt);
                sb.Param("$userId", userId);
                if (rawCredTypes.Count > 0 && stmt.Contains("/*{credIn}*/", StringComparison.Ordinal))
                    sb.Token("credIn", sb.ParamList("$ct", rawCredTypes));
                await sb.ExecuteAsync(_context.Database);
            }
            await tx.CommitAsync();
        }
        finally
        {
            await _context.Database.CloseConnectionAsync();
        }

        _context.ChangeTracker.Clear();

        // Rows are committed — now remove the rendition files (best-effort; a failure just leaves an orphan).
        if (_photoStore is { } photoStore)
            foreach (var id in photoIdsToPurge)
                await photoStore.DeleteAsync(new PhotoId(id));
        if (_heatmaps is { } heatmaps)
            foreach (var id in heatmapIdsToPurge)
                heatmaps.PurgeTiles(id);

        // HM-17 (see the gather above): mark surviving heatmaps whose efforts were just wiped.
        // DELIBERATE seam exception (ruling recorded in the HeatmapLifecycle header): the reset path
        // keeps its own one-statement bulk UPDATE instead of N IHeatmapLifecycle.MarkStaleAsync calls.
        // Audits: don't re-flag this.
        if (heatmapSegmentsToStale.Count > 0)
            await _context.Heatmaps
                .Where(h => heatmapSegmentsToStale.Contains(h.SegmentId) && h.Status == HeatmapStatuses.Done)
                .ExecuteUpdateAsync(s => s.SetProperty(h => h.Status, HeatmapStatuses.Stale));

        // Reclaim freed pages only when a bulk group was deleted. A credentials/settings-only reset
        // frees a handful of rows — not worth a full-file VACUUM (nightly maintenance reclaims those).
        bool deletedBulk = opts.Activities || opts.Gps || opts.Segments
            || opts.SegmentMatches || opts.Photos || opts.MultiDayTours;
        if (deletedBulk)
        {
            try
            {
                await SqliteHousekeeping.VacuumAsync(_context.Database, _logger);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "VACUUM/checkpoint sequence failed — disk space may not be fully reclaimed");
            }
        }

        _logger.LogWarning("Database reset completed for user {UserId}: {@Options}", userId, opts);
    }
}
