using RouteBaseWeb.Helpers;
using System.Globalization;
using System.Text.Json;
using RouteBaseWeb.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace RouteBaseWeb.Services.TrackEdit;

/// <summary>
/// Raw ADO access to Activity_Edit_Session: one row per in-progress/finished edit session (A1 §2.7).
/// No EF entity — Dem_Tiles/TrackBlobStore precedent (persistence ignorance). script_json/stamps_json
/// round-trip through the shared TrackEditJson options (invariant 8's single serialization). Session
/// lifecycle (active/saved/discarded transitions, cursor/undo logic) is owned by A2; this store is CRUD
/// only. Pins the physical handle via the EF facade's ref-counted Open/Close, so GetDbConnection() is
/// valid only inside each call.
/// </summary>
public sealed class EditSessionStore(RouteBaseDbContext context)
{
    private readonly RouteBaseDbContext _context = context;

    /// <summary>Insert a new session row (session_id is caller-assigned — GUID, RecordingSession precedent).</summary>
    public async Task CreateAsync(EditSessionSnapshot session, CancellationToken ct = default)
    {
        await _context.Database.OpenConnectionAsync(ct);
        try
        {
            var conn = (SqliteConnection)_context.Database.GetDbConnection();
            await using var cmd = conn.CreateCommand();
            SqliteAdo.EnlistAmbientTx(_context, cmd);
            cmd.CommandText = @"
                INSERT INTO Activity_Edit_Session
                    (session_id, user_id, status, script_json, cursor, stamps_json,
                     edit_count, created_at, updated_at)
                VALUES ($id, $user, $status, $script, $cursor, $stamps, $edits, $created, $updated);";
            SqliteAdo.Param(cmd, "$id").Value = session.SessionId;
            SqliteAdo.Param(cmd, "$user").Value = session.UserId;
            SqliteAdo.Param(cmd, "$status").Value = session.Status;
            SqliteAdo.Param(cmd, "$script").Value = SerializeScript(session.Script);
            SqliteAdo.Param(cmd, "$cursor").Value = session.Cursor;
            SqliteAdo.Param(cmd, "$stamps").Value = SerializeStamps(session.Stamps);
            SqliteAdo.Param(cmd, "$edits").Value = EditSessionFacts.EditCount(session.Script, session.Cursor);
            SqliteAdo.Param(cmd, "$created").Value = SqliteAdo.Fmt(session.CreatedAt);
            SqliteAdo.Param(cmd, "$updated").Value = SqliteAdo.Fmt(session.UpdatedAt);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            await _context.Database.CloseConnectionAsync();
        }
    }

    /// <summary>One session by id, or null when absent.</summary>
    public async Task<EditSessionSnapshot?> GetAsync(string sessionId, CancellationToken ct = default)
    {
        await _context.Database.OpenConnectionAsync(ct);
        try
        {
            var conn = (SqliteConnection)_context.Database.GetDbConnection();
            await using var cmd = conn.CreateCommand();
            SqliteAdo.EnlistAmbientTx(_context, cmd);
            cmd.CommandText = @"
                SELECT session_id, user_id, status, script_json, cursor, stamps_json, created_at, updated_at
                FROM Activity_Edit_Session WHERE session_id = $id LIMIT 1;";
            SqliteAdo.Param(cmd, "$id").Value = sessionId;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            return await reader.ReadAsync(ct) ? ReadRow(reader) : null;
        }
        finally
        {
            await _context.Database.CloseConnectionAsync();
        }
    }

    /// <summary>The resume banner's three values for the session a ride is CHECKED OUT into, or null when that
    /// session is not (or no longer) the user's active one. The caller resolves the id from the ride's
    /// <c>Activity.in_edit_session_id</c> pointer, so this is one PK row of three scalar columns — no
    /// script_json, no deserialization: it runs on every owner Details render.</summary>
    public async Task<EditResumeHint?> FindResumeHintAsync(
        string userId, string sessionId, CancellationToken ct = default)
    {
        await _context.Database.OpenConnectionAsync(ct);
        try
        {
            var conn = (SqliteConnection)_context.Database.GetDbConnection();
            await using var cmd = conn.CreateCommand();
            SqliteAdo.EnlistAmbientTx(_context, cmd);
            cmd.CommandText = @"
                SELECT session_id, edit_count, updated_at FROM Activity_Edit_Session
                WHERE session_id = $session AND status = 'active' AND user_id = $user LIMIT 1;";
            SqliteAdo.Param(cmd, "$session").Value = sessionId;
            SqliteAdo.Param(cmd, "$user").Value = userId;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            return await reader.ReadAsync(ct)
                ? new EditResumeHint(reader.GetString(0), reader.GetInt32(1), SqliteAdo.ParseTs(reader.GetString(2)).ToUtcOffset())
                : null;
        }
        finally
        {
            await _context.Database.CloseConnectionAsync();
        }
    }

    /// <summary>Write-through persist of script/cursor/stamps + updated_at in one UPDATE (A1 §2.7). When
    /// <paramref name="expectedCursor"/> is non-null the write is conditional on the row's current cursor
    /// (lost-update guard: two tabs appending off the same BaseCursor serialize, only the first matches — the
    /// loser sees 0 rows and the caller surfaces cursorConflict). Returns true iff exactly one row changed.</summary>
    public async Task<bool> UpdateScriptCursorStampsAsync(
        string sessionId, EditScript script, int cursor, IReadOnlyList<TrackVersionStamp> stamps,
        DateTime updatedAt, int? expectedCursor = null, CancellationToken ct = default)
    {
        await _context.Database.OpenConnectionAsync(ct);
        try
        {
            var conn = (SqliteConnection)_context.Database.GetDbConnection();
            await using var cmd = conn.CreateCommand();
            SqliteAdo.EnlistAmbientTx(_context, cmd);
            // edit_count is re-derived here, not just at create: an append can rewrite the script and undo/redo
            // moves the cursor, so the denormalized fact rides along with every write.
            cmd.CommandText = expectedCursor is null
                ? @"UPDATE Activity_Edit_Session
                    SET script_json = $script, cursor = $cursor, stamps_json = $stamps, updated_at = $updated,
                        edit_count = $edits
                    WHERE session_id = $id;"
                : @"UPDATE Activity_Edit_Session
                    SET script_json = $script, cursor = $cursor, stamps_json = $stamps, updated_at = $updated,
                        edit_count = $edits
                    WHERE session_id = $id AND cursor = $expected;";
            SqliteAdo.Param(cmd, "$script").Value = SerializeScript(script);
            SqliteAdo.Param(cmd, "$cursor").Value = cursor;
            SqliteAdo.Param(cmd, "$edits").Value = EditSessionFacts.EditCount(script, cursor);
            SqliteAdo.Param(cmd, "$stamps").Value = SerializeStamps(stamps);
            SqliteAdo.Param(cmd, "$updated").Value = SqliteAdo.Fmt(updatedAt);
            SqliteAdo.Param(cmd, "$id").Value = sessionId;
            if (expectedCursor is int expected) SqliteAdo.Param(cmd, "$expected").Value = expected;
            return await cmd.ExecuteNonQueryAsync(ct) == 1;
        }
        finally
        {
            await _context.Database.CloseConnectionAsync();
        }
    }

    /// <summary>
    /// Delete one session row — the abort. The ride's <c>Activity.in_edit_session_id</c> pointer is released by
    /// the FK's ON DELETE SET NULL, so the whole check-in-less check-out ends in one statement.
    ///
    /// A SAVED session is never deleted: its commit references it, and a save that flipped the status between a
    /// caller's read and this write must not have its provenance deleted out from under it — the guard makes
    /// that a 0-row no-op rather than a race. Returns true iff a row was removed (false = absent or saved).
    /// </summary>
    public async Task<bool> DeleteAsync(string sessionId, CancellationToken ct = default) =>
        await _context.Database.ExecuteSqlRawAsync(
            $"DELETE FROM Activity_Edit_Session WHERE session_id = {{0}} AND status <> '{ActivityEditSessionStatuses.Saved}';",
            [sessionId], ct) == 1;

    /// <summary>
    /// Age-out (B6): discard every ACTIVE session idle since before <paramref name="cutoff"/> — the same
    /// DELETE as <see cref="DeleteAsync"/> (FK ON DELETE SET NULL releases each ride's checkout pointer;
    /// no data was ever touched). SAVED rows are commit provenance and never match; updated_at is the
    /// house "yyyy-MM-dd HH:mm:ss" TEXT form, so the lexicographic compare is a correct time compare.
    /// Returns the number of sessions discarded.
    /// </summary>
    public async Task<int> DiscardIdleActiveAsync(DateTime cutoff, CancellationToken ct = default) =>
        await _context.Database.ExecuteSqlRawAsync(
            $"DELETE FROM Activity_Edit_Session WHERE status = '{ActivityEditSessionStatuses.Active}' AND updated_at < {{0}};",
            [SqliteAdo.Fmt(cutoff)], ct);

    /// <summary>Conditional status flip (A2 §3): UPDATE only when the row is still in
    /// <paramref name="fromStatus"/>. When <paramref name="expectedCursor"/> is non-null the flip also requires
    /// the cursor to be unchanged (save-vs-append guard: an append committed while save was hashing/replaying
    /// drifts the cursor, so the flip finds 0 rows and save aborts instead of dropping the edit). Returns true
    /// iff exactly one row changed — also the double-save and idempotent-discard check. Bumps <c>updated_at</c>.</summary>
    public async Task<bool> TrySetStatusAsync(
        string sessionId, string fromStatus, string toStatus, DateTime updatedAt,
        int? expectedCursor = null, CancellationToken ct = default)
    {
        await _context.Database.OpenConnectionAsync(ct);
        try
        {
            var conn = (SqliteConnection)_context.Database.GetDbConnection();
            await using var cmd = conn.CreateCommand();
            SqliteAdo.EnlistAmbientTx(_context, cmd);
            cmd.CommandText = expectedCursor is null
                ? @"UPDATE Activity_Edit_Session SET status = $to, updated_at = $updated
                    WHERE session_id = $id AND status = $from;"
                : @"UPDATE Activity_Edit_Session SET status = $to, updated_at = $updated
                    WHERE session_id = $id AND status = $from AND cursor = $cursor;";
            SqliteAdo.Param(cmd, "$to").Value = toStatus;
            SqliteAdo.Param(cmd, "$updated").Value = SqliteAdo.Fmt(updatedAt);
            SqliteAdo.Param(cmd, "$id").Value = sessionId;
            SqliteAdo.Param(cmd, "$from").Value = fromStatus;
            if (expectedCursor is int cursor) SqliteAdo.Param(cmd, "$cursor").Value = cursor;
            return await cmd.ExecuteNonQueryAsync(ct) == 1;
        }
        finally
        {
            await _context.Database.CloseConnectionAsync();
        }
    }

    private static EditSessionSnapshot ReadRow(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        DeserializeScript(reader.GetString(3)),
        reader.GetInt32(4),
        DeserializeStamps(reader.GetString(5)),
        reader.GetString(2),
        SqliteAdo.ParseTs(reader.GetString(6)),
        SqliteAdo.ParseTs(reader.GetString(7)));

    private static string SerializeScript(EditScript script) =>
        JsonSerializer.Serialize(script, TrackEditJson.Options);

    private static EditScript DeserializeScript(string json) =>
        JsonSerializer.Deserialize<EditScript>(json, TrackEditJson.Options)
        ?? throw new InvalidOperationException("edit_script column held literal JSON null");

    private static string SerializeStamps(IReadOnlyList<TrackVersionStamp> stamps) =>
        JsonSerializer.Serialize(stamps, TrackEditJson.Options);

    private static IReadOnlyList<TrackVersionStamp> DeserializeStamps(string json) =>
        JsonSerializer.Deserialize<List<TrackVersionStamp>>(json, TrackEditJson.Options) ?? [];
}
