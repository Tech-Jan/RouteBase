using System.Globalization;
using System.Text.Json;
using RouteBaseWeb.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace RouteBaseWeb.Services.TrackEdit;

/// <summary>
/// Raw ADO access to Track_Edit_Commit: one row per save — the SCRIPT, i.e. the explanation of an edit
/// (A1 §2.8, invariant 5). No EF entity — Dem_Tiles precedent.
///
/// What a commit DID is not stored, it is DERIVED — the rows it touched carry its id, so the answer can never
/// drift from reality (the old replaced_ids/created_ids/trashed_ids JSON columns could, and could not be
/// joined or indexed either):
///
///   created   Activity_Version.commit_id = C AND seq = 1   (that commit brought the track into existence)
///   replaced  Activity_Version.commit_id = C AND seq > 1   (that commit wrote a new version of a track)
///   trashed   Activity.consumed_commit_id = C              (that commit spliced the track into another)
///
/// Purge a track and it simply drops out of the derivation — no dangling ids to clean up.
///
/// RETENTION (decision F, 2026-07-17): commits are AUDIT rows and are deliberately never GC'd — the only
/// delete is the per-user reset. A commit whose tracks were all purged is retained, referenced by nothing:
/// that is the record that an edit happened, not a leak. Do not add a "cleanup" that deletes such rows.
///
/// script_json uses the shared TrackEditJson.Options (invariant 8). Pins the physical handle via the EF
/// facade's ref-counted Open/Close, so GetDbConnection() is valid only inside each call.
/// </summary>
public sealed class TrackEditCommitStore(RouteBaseDbContext context)
{
    private readonly RouteBaseDbContext _context = context;

    /// <summary>Insert one commit row; returns the new commit_id. The id lists are not passed in — the save
    /// stamps this commit_id onto the versions it writes and the sources it consumes.</summary>
    public async Task<CommitId> InsertAsync(
        string userId, DateTime committedAt, string saveMode, string? sessionId, EditScript script,
        CancellationToken ct = default)
    {
        await _context.Database.OpenConnectionAsync(ct);
        try
        {
            var conn = (SqliteConnection)_context.Database.GetDbConnection();
            await using (var cmd = conn.CreateCommand())
            {
                SqliteAdo.EnlistAmbientTx(_context, cmd);
                cmd.CommandText = @"
                    INSERT INTO Track_Edit_Commit
                        (user_id, committed_at, save_mode, session_id, script_json)
                    VALUES ($user, $committed, $mode, $session, $script);";
                SqliteAdo.Param(cmd, "$user").Value = userId;
                SqliteAdo.Param(cmd, "$committed").Value = SqliteAdo.Fmt(committedAt);
                SqliteAdo.Param(cmd, "$mode").Value = saveMode;
                SqliteAdo.Param(cmd, "$session").Value = (object?)sessionId ?? DBNull.Value;
                SqliteAdo.Param(cmd, "$script").Value = JsonSerializer.Serialize(script, TrackEditJson.Options);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await using var idCmd = conn.CreateCommand();
            SqliteAdo.EnlistAmbientTx(_context, idCmd);
            idCmd.CommandText = "SELECT last_insert_rowid();";
            return new CommitId(Convert.ToInt32(await idCmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture));
        }
        finally
        {
            await _context.Database.CloseConnectionAsync();
        }
    }

    /// <summary>One commit by id, or null when absent.</summary>
    public async Task<TrackEditCommitEntry?> GetAsync(CommitId commitId, CancellationToken ct = default)
    {
        var rows = await QueryAsync("WHERE commit_id = $p", commitId.Value.ToString(CultureInfo.InvariantCulture), ct);
        return rows.FirstOrDefault();
    }

    /// <summary>
    /// The newest non-reverted commit (other than <paramref name="excludeCommitId"/>) that REPLACED the track —
    /// the revert's badge retarget. One targeted query: the old way (list every commit the user ever made, with
    /// full scripts + derived id lists, to scan for one id) grew without bound, since commits are never purged.
    /// </summary>
    public async Task<CommitId?> NewestOtherCommitReplacingAsync(
        TrackId trackId, CommitId excludeCommitId, string userId, CancellationToken ct = default)
    {
        await _context.Database.OpenConnectionAsync(ct);
        try
        {
            var conn = (SqliteConnection)_context.Database.GetDbConnection();
            await using var cmd = conn.CreateCommand();
            SqliteAdo.EnlistAmbientTx(_context, cmd);
            cmd.CommandText = @"
                SELECT c.commit_id FROM Track_Edit_Commit c
                WHERE c.user_id = $u AND c.commit_id <> $x AND c.reverted_at IS NULL
                  AND EXISTS (SELECT 1 FROM Activity_Version v
                              WHERE v.commit_id = c.commit_id AND v.track_id = $t AND v.seq > 1)
                ORDER BY c.committed_at DESC, c.commit_id DESC LIMIT 1;";
            SqliteAdo.Param(cmd, "$u").Value = userId;
            SqliteAdo.Param(cmd, "$x", excludeCommitId.Value);
            SqliteAdo.Param(cmd, "$t").Value = trackId.Value;
            var result = await cmd.ExecuteScalarAsync(ct);
            return result is null or DBNull ? null : new CommitId(Convert.ToInt32(result, CultureInfo.InvariantCulture));
        }
        finally
        {
            await _context.Database.CloseConnectionAsync();
        }
    }

    /// <summary>Stamp a commit as reverted (revert audit — A2 owns the actual data restore).</summary>
    public async Task MarkRevertedAsync(CommitId commitId, DateTime revertedAt, CancellationToken ct = default)
    {
        await _context.Database.OpenConnectionAsync(ct);
        try
        {
            var conn = (SqliteConnection)_context.Database.GetDbConnection();
            await using var cmd = conn.CreateCommand();
            SqliteAdo.EnlistAmbientTx(_context, cmd);
            cmd.CommandText = "UPDATE Track_Edit_Commit SET reverted_at = $reverted WHERE commit_id = $id;";
            SqliteAdo.Param(cmd, "$reverted").Value = SqliteAdo.Fmt(revertedAt);
            SqliteAdo.Param(cmd, "$id", commitId.Value);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            await _context.Database.CloseConnectionAsync();
        }
    }

    // ---- internals ----

    /// <summary>Commit rows + their derived id lists. The lists come from two set-based queries over the
    /// SAME commit set (never one query per commit): the versions those commits wrote, and the activities
    /// they consumed.</summary>
    private async Task<IReadOnlyList<TrackEditCommitEntry>> QueryAsync(
        string whereClause, string param, CancellationToken ct)
    {
        await _context.Database.OpenConnectionAsync(ct);
        try
        {
            var conn = (SqliteConnection)_context.Database.GetDbConnection();

            var rows = new List<(int CommitId, string UserId, DateTime At, string Mode, string? Session,
                EditScript Script, DateTime? RevertedAt)>();
            await using (var cmd = conn.CreateCommand())
            {
                SqliteAdo.EnlistAmbientTx(_context, cmd);
                cmd.CommandText =
                    "SELECT commit_id, user_id, committed_at, save_mode, session_id, script_json, reverted_at " +
                    $"FROM Track_Edit_Commit {whereClause};";
                SqliteAdo.Param(cmd, "$p").Value = param;
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                    rows.Add((
                        reader.GetInt32(0), reader.GetString(1), SqliteAdo.ParseTs(reader.GetString(2)),
                        reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4),
                        JsonSerializer.Deserialize<EditScript>(reader.GetString(5), TrackEditJson.Options)
                            ?? throw new InvalidOperationException("script_json held JSON null — the store only writes serialized scripts"),
                        reader.IsDBNull(6) ? null : SqliteAdo.ParseTs(reader.GetString(6))));
            }
            if (rows.Count == 0) return [];

            string ids = string.Join(",", rows.Select(r => r.CommitId));   // ints from the DB — no injection surface
            var created = new Dictionary<int, List<TrackId>>();
            var replaced = new Dictionary<int, List<TrackId>>();
            var trashed = new Dictionary<int, List<TrackId>>();

            await using (var cmd = conn.CreateCommand())
            {
                SqliteAdo.EnlistAmbientTx(_context, cmd);
                cmd.CommandText =
                    $"SELECT commit_id, track_id, seq FROM Activity_Version WHERE commit_id IN ({ids}) ORDER BY track_id;";
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var bucket = reader.GetInt32(2) == 1 ? created : replaced;   // seq 1 = this commit created it
                    Add(bucket, reader.GetInt32(0), new TrackId(reader.GetInt32(1)));
                }
            }

            await using (var cmd = conn.CreateCommand())
            {
                SqliteAdo.EnlistAmbientTx(_context, cmd);
                cmd.CommandText =
                    $"SELECT consumed_commit_id, track_ID FROM Activity WHERE consumed_commit_id IN ({ids}) ORDER BY track_ID;";
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                    Add(trashed, reader.GetInt32(0), new TrackId(reader.GetInt32(1)));
            }

            return rows.Select(r => new TrackEditCommitEntry(
                new CommitId(r.CommitId), r.UserId, r.At, r.Mode, r.Session, r.Script,
                replaced.GetValueOrDefault(r.CommitId) ?? [],
                created.GetValueOrDefault(r.CommitId) ?? [],
                trashed.GetValueOrDefault(r.CommitId) ?? [],
                r.RevertedAt)).ToList();
        }
        finally
        {
            await _context.Database.CloseConnectionAsync();
        }
    }

    private static void Add(Dictionary<int, List<TrackId>> map, int commitId, TrackId trackId)
    {
        if (!map.TryGetValue(commitId, out var list)) map[commitId] = list = [];
        list.Add(trackId);
    }
}

/// <summary>One save-commit audit row. The three id lists are DERIVED at read time (see the store) — they are
/// not columns, so they cannot drift from what the versions and activities actually say.</summary>
public sealed record TrackEditCommitEntry(
    CommitId CommitId, string UserId, DateTime CommittedAt, string SaveMode, string? SessionId,
    EditScript Script, IReadOnlyList<TrackId> ReplacedIds, IReadOnlyList<TrackId> CreatedIds,
    IReadOnlyList<TrackId> TrashedIds, DateTime? RevertedAt);
