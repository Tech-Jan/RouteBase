using System.Globalization;
using RouteBaseWeb.Models;
using RouteBaseWeb.Services.TrackEdit;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace RouteBaseWeb.Services.Tracks;

/// <summary>One row of a track's revision list. Stats are carried so a history view needs no blob decode.</summary>
public sealed record TrackVersionRow(
    VersionId VersionId, TrackId ActivityId, int Seq, string ObjectSha, CommitId? CommitId,
    int PointCount, double? Distance, double? Ascent, double? DurationS, DateTime CreatedAt);

/// <summary>Scalars stamped onto a version at write time (all optional — a caller without a decoded track
/// can omit them; the object is always authoritative).</summary>
public sealed record TrackVersionStats(double? Distance = null, double? Ascent = null, double? DurationS = null);

/// <summary>What a ride's revision list amounts to, without reading it. <paramref name="AnyCommit"/> = at least
/// one version was written by a SAVE, which is what tells a merge/split product (born from an edit, one version)
/// apart from an imported ride (also one version, but no commit). The rule these two feed lives in
/// <c>hasHistory</c> (version-history-model.ts) — see <see cref="TrackContentStore.HistoryFactsAsync"/>.</summary>
public sealed record TrackHistoryFacts(int VersionCount, bool AnyCommit);

/// <summary>
/// The revision axis: append-only <c>Activity_Version</c> rows over immutable objects, one of which is
/// flagged <c>is_head</c> — the version a track currently IS. The flag lives on the version (not as a pointer
/// on Activity) so a dangling head is structurally impossible and "exactly one head per track" is enforced by
/// a partial unique index rather than by us.
///
/// This is the single seam every writer goes through — it replaced <c>TrackBlobStore.PutAsync</c>, which
/// overwrote a track's only copy. Writing content now means APPENDING a version and moving the head; the
/// previous version keeps existing, which is what makes revert a pointer move and makes it impossible for a
/// delete to destroy an original (the bug this model was built for).
///
/// Raw ADO, no EF entity (Dem_Tiles precedent). Enlists any ambient EF transaction, so a save that appends
/// versions inside its tx commits or rolls back as one unit.
/// </summary>
public sealed class TrackContentStore(RouteBaseDbContext context, ITrackObjectStore objects, TimeProvider clock)
{
    private readonly RouteBaseDbContext _context = context;
    private readonly ITrackObjectStore _objects = objects;

    /// <summary>
    /// Store <paramref name="encoded"/> as the track's NEW current content: object + version(seq+1) + head move.
    /// Returns the new version id.
    ///
    /// <paramref name="parentVersionIds"/> records lineage (invariant: 0 parents = import/recording ·
    /// 1 = an edit of that version · N = a merge of N inputs). Omit it for an ordinary edit and the current
    /// head is used — the version this one supersedes.
    ///
    /// An UNCOMMITTED write of content identical to the current head is not appended: the head version id comes
    /// straight back and the revision list is untouched (git's "nothing to commit"). That is what keeps a
    /// re-finalize — an elevation refresh, an ElevationBackfill sweep — from stamping a new version per track
    /// whose object is byte-for-byte the one before it, leaving a history that is mostly no-ops.
    ///
    /// A COMMITTED write always appends, even when the bytes are unchanged (a whole-track crop). The version
    /// row is not just content there: it is the record that this commit touched this track — "what did commit C
    /// replace?" is the query <c>commit_id = C AND seq > 1</c>, and suppressing the row would make the commit
    /// claim it replaced nothing. It costs no bytes either way: the object is the same sha, stored once.
    /// </summary>
    public async Task<VersionId> AppendVersionAsync(
        TrackId trackId, byte[] encoded, int pointCount,
        TrackVersionStats? stats = null, CommitId? commitId = null,
        IReadOnlyList<VersionId>? parentVersionIds = null, CancellationToken ct = default)
    {
        // Sha first, store write later: the no-op path (an uncommitted write of the head's exact bytes —
        // an elevation-refresh sweep over unchanged tracks) then never ships the blob to the store at all.
        string sha = ITrackObjectStore.ShaOf(encoded);
        var head = await HeadAsync(trackId, ct);
        if (commitId is null && head is { } existing && existing.Sha == sha)
            return existing.VersionId;

        // Object + version + parents + head move commit or roll back as one unit even for a caller without
        // its own transaction (finalize) — a crash can't leave a version row nobody points at.
        bool ownTx = _context.Database.CurrentTransaction is null;
        var tx = ownTx ? await _context.Database.BeginTransactionAsync(ct) : null;
        try
        {
            await _objects.PutAsync(encoded, TrackBlobCodec.Version, pointCount, ct);

            var parents = parentVersionIds ?? (head is { } h ? [h.VersionId] : Array.Empty<VersionId>());

            var versionId = await InsertVersionWithSeqRetryAsync(trackId, sha, commitId, pointCount, stats, ct);

            foreach (var parent in parents)
                await _context.Database.ExecuteSqlRawAsync(
                    "INSERT OR IGNORE INTO Version_Parent (version_id, parent_version_id) VALUES ({0}, {1});",
                    [versionId.Value, parent.Value], ct);

            await SetHeadAsync(trackId, versionId, ct);
            if (tx is not null) await tx.CommitAsync(ct);
            return versionId;
        }
        finally
        {
            if (tx is not null) await tx.DisposeAsync();
        }
    }

    /// <summary>
    /// Make an EARLIER version current again by appending a new version that reuses its object (§restore-v2).
    /// Costs zero new bytes — the object already exists — and keeps history append-only: nothing is rewritten
    /// or renumbered, so "restored v2" is itself a point in the record. Returns the new version id.
    /// Restoring content the head already has is a no-op (same rule as AppendVersionAsync).
    /// </summary>
    public async Task<VersionId> RestoreVersionAsync(TrackId trackId, VersionId versionId, CancellationToken ct = default)
    {
        var target = await GetVersionAsync(versionId, ct)
            ?? throw new InvalidOperationException($"version {versionId} does not exist");
        if (target.ActivityId != trackId)
            throw new InvalidOperationException($"version {versionId} belongs to activity {target.ActivityId}, not {trackId}");

        if (await HeadAsync(trackId, ct) is { } head && head.Sha == target.ObjectSha)
            return head.VersionId;

        bool ownTx = _context.Database.CurrentTransaction is null;
        var tx = ownTx ? await _context.Database.BeginTransactionAsync(ct) : null;
        try
        {
            var newVersionId = await InsertVersionWithSeqRetryAsync(
                trackId, target.ObjectSha, commitId: null, target.PointCount,
                new TrackVersionStats(target.Distance, target.Ascent, target.DurationS), ct);

            await _context.Database.ExecuteSqlRawAsync(
                "INSERT OR IGNORE INTO Version_Parent (version_id, parent_version_id) VALUES ({0}, {1});",
                [newVersionId.Value, versionId.Value], ct);

            await SetHeadAsync(trackId, newVersionId, ct);
            if (tx is not null) await tx.CommitAsync(ct);
            return newVersionId;
        }
        finally
        {
            if (tx is not null) await tx.DisposeAsync();
        }
    }

    /// <summary>
    /// Make an existing version of the track the current one (revert = a head move, no bytes written).
    ///
    /// Clear-then-set, not one CASE update: SQLite enforces the partial unique index ROW BY ROW, so a single
    /// statement that flags the new head before unflagging the old one trips "one head per track" mid-flight.
    /// Both statements run in one transaction (the caller's if there is one), so the track is never left with
    /// two heads — and never with none.
    ///
    /// The set is scoped to the track AND asserts it hit a row: an id belonging to another track (or to no row
    /// at all) would otherwise clear this track's head and set none, leaving it headless — i.e. silently
    /// contentless. The partial unique index cannot catch that; only the row count can.
    /// </summary>
    public async Task SetHeadAsync(TrackId trackId, VersionId versionId, CancellationToken ct = default)
    {
        bool ownTx = _context.Database.CurrentTransaction is null;
        var tx = ownTx ? await _context.Database.BeginTransactionAsync(ct) : null;
        try
        {
            await _context.Database.ExecuteSqlRawAsync(
                "UPDATE Activity_Version SET is_head = 0 WHERE track_id = {0} AND is_head = 1;", [trackId.Value], ct);
            int affected = await _context.Database.ExecuteSqlRawAsync(
                "UPDATE Activity_Version SET is_head = 1 WHERE version_id = {0} AND track_id = {1};",
                [versionId.Value, trackId.Value], ct);
            if (affected != 1)
                throw new InvalidOperationException(
                    $"version {versionId} is not a version of track {trackId} — refusing to leave it headless");
            if (tx is not null) await tx.CommitAsync(ct);
        }
        finally
        {
            if (tx is not null) await tx.DisposeAsync();
        }
    }

    /// <summary>
    /// Delete versions outright — the ONE destructive operation on content, used only to clear a track's
    /// history. Guarded to <paramref name="trackId"/> and to non-head rows, so it can never leave a track
    /// contentless however it is called.
    ///
    /// Edges are dropped explicitly rather than left to <c>ON DELETE CASCADE</c>: the cascade only fires when
    /// the FK pragma is on, and a stale Version_Parent row keyed by a dead version is a landmine we would be
    /// betting a PRAGMA on. Only the doomed versions' OWN edges go — edges pointing AT them (another ride that
    /// was split from this one) are deliberately left to dangle, which is the designed "its inputs were purged"
    /// state that the history reader already reports honestly.
    ///
    /// Returns the object shas the deleted versions referenced — the caller feeds them to the object sweep as
    /// GC candidates. They are candidates, NOT garbage: another version (or another track) may still hold the
    /// same sha, and content is shared by hash.
    /// </summary>
    public async Task<IReadOnlyList<string>> DeleteVersionsAsync(
        TrackId trackId, IReadOnlyCollection<VersionId> versionIds, CancellationToken ct = default)
    {
        if (versionIds.Count == 0) return [];

        // Same guard as the version delete below (this track, non-head): the ids a caller passes are a
        // REQUEST, not a warrant — an id that fails the guard keeps its row and must keep its lineage
        // edges too, or a surviving version silently reads as parentless ("Imported") in the history.
        var headId = await GetHeadVersionIdAsync(trackId, ct);
        var deletable = (await ListVersionsAsync(trackId, ct))
            .Where(v => versionIds.Contains(v.VersionId) && v.VersionId != headId)
            .ToList();
        if (deletable.Count == 0) return [];
        string ids = string.Join(',', deletable.Select(v => v.VersionId));   // ints, not user text — no injection surface

        var shas = deletable
            .Select(v => v.ObjectSha)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        bool ownTx = _context.Database.CurrentTransaction is null;
        var tx = ownTx ? await _context.Database.BeginTransactionAsync(ct) : null;
        try
        {
            // EF1002: `ids` is a comma-joined list of int VersionIds (see comment above) — no injection surface.
#pragma warning disable EF1002
            await _context.Database.ExecuteSqlRawAsync(
                $"DELETE FROM Version_Parent WHERE version_id IN ({ids});", ct);
            await _context.Database.ExecuteSqlRawAsync(
                $"DELETE FROM Activity_Version WHERE version_id IN ({ids}) AND track_id = {{0}} AND is_head = 0;",
                [trackId.Value], ct);
#pragma warning restore EF1002
            if (tx is not null) await tx.CommitAsync(ct);
        }
        finally
        {
            if (tx is not null) await tx.DisposeAsync();
        }
        return shas;
    }

    /// <summary>
    /// Drop a version's lineage edges to specific parents. Used when clearing a history: the head's edge into
    /// its own (now deleted) chain must go, or the reader sees a parent that does not resolve and reports a
    /// never-merged ride as "merged from rides that have been deleted". Cross-track edges are NOT passed here —
    /// a real merge's parents still exist, and that provenance is worth keeping.
    /// </summary>
    public async Task DropParentEdgesAsync(
        VersionId versionId, IReadOnlyCollection<VersionId> parentIds, CancellationToken ct = default)
    {
        if (parentIds.Count == 0) return;
        string ids = string.Join(',', parentIds);   // ints, not user text — no injection surface
#pragma warning disable EF1002
        await _context.Database.ExecuteSqlRawAsync(
            $"DELETE FROM Version_Parent WHERE version_id = {{0}} AND parent_version_id IN ({ids});",
            [versionId.Value], ct);
#pragma warning restore EF1002
    }

    /// <summary>
    /// Record that a commit CREATED this track: stamp it on the track's first version. Together with the
    /// commit_id the save stamps on the versions it replaces, this is what makes a commit's effect derivable
    /// (seq 1 = created · seq > 1 = replaced) instead of a JSON id list that has to be kept in sync.
    /// Stamped per creation, so a crash mid-way still records exactly what was created up to that point.
    /// </summary>
    public async Task StampCreatingCommitAsync(TrackId trackId, CommitId commitId, CancellationToken ct = default) =>
        await _context.Database.ExecuteSqlRawAsync(
            "UPDATE Activity_Version SET commit_id = {0} WHERE track_id = {1} AND seq = 1;",
            [commitId.Value, trackId.Value], ct);

    /// <summary>
    /// Record lineage on a version that already exists — the merge/split case, where the content is written by
    /// the ordinary import funnel (which knows nothing of parents) and the provenance is known only afterwards.
    /// Idempotent (INSERT OR IGNORE), so a retried phase-2 creation does not duplicate the DAG edges.
    /// </summary>
    public async Task AddParentsAsync(
        VersionId versionId, IReadOnlyList<VersionId> parentVersionIds, CancellationToken ct = default)
    {
        foreach (var parent in parentVersionIds)
            await _context.Database.ExecuteSqlRawAsync(
                "INSERT OR IGNORE INTO Version_Parent (version_id, parent_version_id) VALUES ({0}, {1});",
                [versionId.Value, parent.Value], ct);
    }

    /// <summary>The bytes a track currently IS, or null when it has no content.</summary>
    public async Task<byte[]?> GetHeadDataAsync(TrackId trackId, CancellationToken ct = default)
    {
        string? sha = await GetHeadShaAsync(trackId, ct);
        return sha is null ? null : await _objects.GetAsync(sha, ct);
    }

    /// <summary>The address of a track's current content (the cache key — immutable, so it never goes stale).</summary>
    public async Task<string?> GetHeadShaAsync(TrackId trackId, CancellationToken ct = default) =>
        await ScalarAsync<string>(
            "SELECT object_sha FROM Activity_Version WHERE track_id = $t AND is_head = 1 LIMIT 1;",
            "$t", trackId.Value, ct);

    public async Task<VersionId?> GetHeadVersionIdAsync(TrackId trackId, CancellationToken ct = default)
    {
        var value = await ScalarAsync<long?>(
            "SELECT version_id FROM Activity_Version WHERE track_id = $t AND is_head = 1 LIMIT 1;",
            "$t", trackId.Value, ct);
        return value is null ? null : new VersionId((int)value.Value);
    }

    /// <summary>
    /// The two raw facts the history panel needs to decide whether it has anything to show, in ONE indexed seek
    /// (IX_ActivityVersion_Track) — no rows, no decode, no lineage walk.
    ///
    /// Deliberately FACTS, not a verdict: "does this ride have a history?" is a rule with a subtlety (a ride
    /// BORN from a save has one version, and that version is an edit), and that rule lives in exactly one place
    /// — <c>hasHistory</c> in version-history-model.ts. Returning a bool from here would be a second copy of it,
    /// free to drift. Details renders these two numbers so the panel can skip its page-load fetch entirely on a
    /// never-edited ride, which is most of them (the F-1 pattern: server-render the fact, delete the fetch).
    ///
    /// <c>COUNT(commit_id)</c> counts the NON-NULL ones — how many of this ride's versions a save wrote.
    /// </summary>
    public async Task<TrackHistoryFacts> HistoryFactsAsync(TrackId trackId, CancellationToken ct = default)
    {
        await _context.Database.OpenConnectionAsync(ct);
        try
        {
            var conn = (SqliteConnection)_context.Database.GetDbConnection();
            await using var cmd = conn.CreateCommand();
            SqliteAdo.EnlistAmbientTx(_context, cmd);
            cmd.CommandText =
                "SELECT COUNT(*), COUNT(commit_id) FROM Activity_Version WHERE track_id = $t;";
            SqliteAdo.Param(cmd, "$t").Value = trackId.Value;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            return await reader.ReadAsync(ct)
                ? new TrackHistoryFacts(reader.GetInt32(0), reader.GetInt32(1) > 0)
                : new TrackHistoryFacts(0, false);
        }
        finally
        {
            await _context.Database.CloseConnectionAsync();
        }
    }

    /// <summary>A track's full revision list, oldest first.</summary>
    public async Task<IReadOnlyList<TrackVersionRow>> ListVersionsAsync(TrackId trackId, CancellationToken ct = default) =>
        await QueryAsync("WHERE track_id = $p ORDER BY seq", trackId.Value, ct);

    public async Task<TrackVersionRow?> GetVersionAsync(VersionId versionId, CancellationToken ct = default) =>
        (await QueryAsync("WHERE version_id = $p", versionId.Value, ct)).FirstOrDefault();

    /// <summary>The versions one commit produced — a commit's effect on content, without any id lists.</summary>
    public async Task<IReadOnlyList<TrackVersionRow>> VersionsOfCommitAsync(CommitId commitId, CancellationToken ct = default) =>
        await QueryAsync("WHERE commit_id = $p ORDER BY version_id", commitId.Value, ct);

    /// <summary>
    /// The versions with these ids, in ONE seek — the history reader resolves every parent edge at once rather
    /// than one <see cref="GetVersionAsync"/> per edge. Ids that do not exist are simply absent from the result
    /// (a purged parent is a dangling edge by design, and the caller reports it as missing).
    /// </summary>
    public async Task<IReadOnlyList<TrackVersionRow>> GetVersionsAsync(
        IReadOnlyCollection<VersionId> versionIds, CancellationToken ct = default)
    {
        if (versionIds.Count == 0) return [];
        string ids = string.Join(',', versionIds);   // ints, not user text — no injection surface (as :186)
        return await QueryAsync($"WHERE version_id IN ({ids})", param: null, ct);
    }

    /// <summary>
    /// Parents of many versions in ONE seek — the history reader asks for every version at once. An id with no
    /// parents is absent from the dictionary (callers treat that as an empty list: 0 parents = an import).
    /// </summary>
    public async Task<IReadOnlyDictionary<VersionId, List<VersionId>>> ParentsOfManyAsync(
        IReadOnlyCollection<VersionId> versionIds, CancellationToken ct = default)
    {
        var byVersion = new Dictionary<VersionId, List<VersionId>>();
        if (versionIds.Count == 0) return byVersion;
        string ids = string.Join(',', versionIds);

        await _context.Database.OpenConnectionAsync(ct);
        try
        {
            var conn = (SqliteConnection)_context.Database.GetDbConnection();
            await using var cmd = conn.CreateCommand();
            SqliteAdo.EnlistAmbientTx(_context, cmd);
            cmd.CommandText =
                $"SELECT version_id, parent_version_id FROM Version_Parent WHERE version_id IN ({ids}) " +
                "ORDER BY version_id, parent_version_id;";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var versionId = new VersionId(reader.GetInt32(0));
                if (!byVersion.TryGetValue(versionId, out var parents))
                    byVersion[versionId] = parents = [];
                parents.Add(new VersionId(reader.GetInt32(1)));
            }
        }
        finally
        {
            await _context.Database.CloseConnectionAsync();
        }
        return byVersion;
    }

    /// <summary>Parents of a version (lineage: 0 = import · 1 = edit/split · N = merge).</summary>
    public async Task<IReadOnlyList<VersionId>> ParentsOfAsync(VersionId versionId, CancellationToken ct = default)
    {
        var parents = new List<VersionId>();
        await _context.Database.OpenConnectionAsync(ct);
        try
        {
            var conn = (SqliteConnection)_context.Database.GetDbConnection();
            await using var cmd = conn.CreateCommand();
            SqliteAdo.EnlistAmbientTx(_context, cmd);
            cmd.CommandText = "SELECT parent_version_id FROM Version_Parent WHERE version_id = $v ORDER BY parent_version_id;";
            SqliteAdo.Param(cmd, "$v", versionId.Value);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) parents.Add(new VersionId(reader.GetInt32(0)));
        }
        finally
        {
            await _context.Database.CloseConnectionAsync();
        }
        return parents;
    }

    // ---- internals ----

    /// <summary>The head's id + sha in one seek (the two public getters exist for callers that want one half).</summary>
    private async Task<(VersionId VersionId, string Sha)?> HeadAsync(TrackId trackId, CancellationToken ct)
    {
        await _context.Database.OpenConnectionAsync(ct);
        try
        {
            var conn = (SqliteConnection)_context.Database.GetDbConnection();
            await using var cmd = conn.CreateCommand();
            SqliteAdo.EnlistAmbientTx(_context, cmd);
            cmd.CommandText = "SELECT version_id, object_sha FROM Activity_Version WHERE track_id = $t AND is_head = 1 LIMIT 1;";
            SqliteAdo.Param(cmd, "$t").Value = trackId.Value;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            return (new VersionId(reader.GetInt32(0)), reader.GetString(1));
        }
        finally
        {
            await _context.Database.CloseConnectionAsync();
        }
    }

    private async Task<int> NextSeqAsync(TrackId trackId, CancellationToken ct)
    {
        var max = await ScalarAsync<long?>(
            "SELECT MAX(seq) FROM Activity_Version WHERE track_id = $t;", "$t", trackId.Value, ct);
        return (int)(max ?? 0) + 1;
    }

    private const int MaxSeqRetries = 5;

    /// <summary>
    /// Read-MAX-then-insert is not atomic: two concurrent writers on the same track (e.g. two edits, or an
    /// edit racing a re-finalize) can both compute the same next seq and one loses to the <c>UNIQUE
    /// (track_id, seq)</c> constraint (SVC-08). Recomputing and retrying is safe here — unlike a client
    /// request retry, nothing about this write depends on the seq value the caller chose.
    /// </summary>
    internal async Task<VersionId> InsertVersionWithSeqRetryAsync(
        TrackId trackId, string sha, CommitId? commitId, int pointCount, TrackVersionStats? stats, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            int seq = await NextSeqAsync(trackId, ct);
            try
            {
                return await InsertVersionAsync(trackId, seq, sha, commitId, pointCount, stats, ct);
            }
            catch (SqliteException ex) when (attempt < MaxSeqRetries && IsSeqUniqueViolation(ex))
            {
                // Another writer took this seq between our MAX(seq) read and our INSERT — retry with a fresh MAX.
            }
        }
    }

    // internal, not private: RouteBaseWeb.Tests forces the exact UNIQUE(track_id, seq) violation directly
    // (InternalsVisibleTo) — a genuine two-writer race isn't reproducible on demand in a single-process test.
    internal static bool IsSeqUniqueViolation(SqliteException ex) =>
        ex.SqliteErrorCode == 19 // SQLITE_CONSTRAINT
        && ex.Message.Contains("Activity_Version.track_id, Activity_Version.seq", StringComparison.Ordinal);

    internal async Task<VersionId> InsertVersionAsync(
        TrackId trackId, int seq, string sha, CommitId? commitId, int pointCount, TrackVersionStats? stats, CancellationToken ct)
    {
        await _context.Database.OpenConnectionAsync(ct);
        try
        {
            var conn = (SqliteConnection)_context.Database.GetDbConnection();
            await using var cmd = conn.CreateCommand();
            SqliteAdo.EnlistAmbientTx(_context, cmd);
            cmd.CommandText =
                "INSERT INTO Activity_Version " +
                "(track_id, seq, object_sha, commit_id, point_count, distance, ascent, duration_s, created_at) " +
                "VALUES ($a, $s, $o, $c, $p, $d, $asc, $dur, $at); SELECT last_insert_rowid();";
            SqliteAdo.Param(cmd, "$a").Value = trackId.Value;
            SqliteAdo.Param(cmd, "$s").Value = seq;
            SqliteAdo.Param(cmd, "$o").Value = sha;
            SqliteAdo.Param(cmd, "$c").Value = (object?)commitId?.Value ?? DBNull.Value;
            SqliteAdo.Param(cmd, "$p").Value = pointCount;
            SqliteAdo.Param(cmd, "$d").Value = (object?)stats?.Distance ?? DBNull.Value;
            SqliteAdo.Param(cmd, "$asc").Value = (object?)stats?.Ascent ?? DBNull.Value;
            SqliteAdo.Param(cmd, "$dur").Value = (object?)stats?.DurationS ?? DBNull.Value;
            SqliteAdo.Param(cmd, "$at").Value = SqliteAdo.Fmt(clock.GetUtcNow().UtcDateTime);
            return new VersionId(Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture));
        }
        finally
        {
            await _context.Database.CloseConnectionAsync();
        }
    }

    private async Task<IReadOnlyList<TrackVersionRow>> QueryAsync(string whereClause, int? param, CancellationToken ct)
    {
        var rows = new List<TrackVersionRow>();
        await _context.Database.OpenConnectionAsync(ct);
        try
        {
            var conn = (SqliteConnection)_context.Database.GetDbConnection();
            await using var cmd = conn.CreateCommand();
            SqliteAdo.EnlistAmbientTx(_context, cmd);
            cmd.CommandText =
                "SELECT version_id, track_id, seq, object_sha, commit_id, point_count, distance, ascent, " +
                $"duration_s, created_at FROM Activity_Version {whereClause};";
            if (param is int p) SqliteAdo.Param(cmd, "$p").Value = p;   // the IN-list queries carry their ids inline
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                rows.Add(new TrackVersionRow(
                    new VersionId(reader.GetInt32(0)), new TrackId(reader.GetInt32(1)), reader.GetInt32(2), reader.GetString(3),
                    reader.IsDBNull(4) ? null : new CommitId(reader.GetInt32(4)),
                    reader.GetInt32(5),
                    reader.IsDBNull(6) ? null : reader.GetDouble(6),
                    reader.IsDBNull(7) ? null : reader.GetDouble(7),
                    reader.IsDBNull(8) ? null : reader.GetDouble(8),
                    SqliteAdo.ParseTs(reader.GetString(9))));
        }
        finally
        {
            await _context.Database.CloseConnectionAsync();
        }
        return rows;
    }

    private async Task<T?> ScalarAsync<T>(string sql, string paramName, int value, CancellationToken ct)
    {
        await _context.Database.OpenConnectionAsync(ct);
        try
        {
            var conn = (SqliteConnection)_context.Database.GetDbConnection();
            await using var cmd = conn.CreateCommand();
            SqliteAdo.EnlistAmbientTx(_context, cmd);
            cmd.CommandText = sql;
            SqliteAdo.Param(cmd, paramName).Value = value;
            var result = await cmd.ExecuteScalarAsync(ct);
            if (result is null or DBNull) return default;
            return (T)Convert.ChangeType(result, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T), CultureInfo.InvariantCulture);
        }
        finally
        {
            await _context.Database.CloseConnectionAsync();
        }
    }
}
