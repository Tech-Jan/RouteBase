using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.TrackEdit;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace RouteBaseWeb.Tests.Services.TrackEdit;

/// <summary>
/// Round-trip semantics for <see cref="EditSessionStore"/> against a real (in-memory) SQLite context,
/// same fixture style as ErrorLogStoreTests/TrackContentStoreTests. Table created via the schema manager.
/// </summary>
public class EditSessionStoreTests : IAsyncLifetime
{
    private RouteBaseDbContext _context = null!;
    private EditSessionStore _store = null!;

    public async Task InitializeAsync()
    {
        var conn = $"Data Source=file:editsession_test_{Guid.NewGuid():N}?mode=memory&cache=shared";
        var options = new DbContextOptionsBuilder<RouteBaseDbContext>().UseSqlite(conn).Options;
        _context = new RouteBaseDbContext(options);
        // One connection held open so the shared-cache in-memory DB survives the store's open/close cycles.
        await _context.Database.OpenConnectionAsync();
        await _context.Database.EnsureCreatedAsync();
        await new DbSchemaManager(_context).EnsureTrackEditTablesAsync();
        _store = new EditSessionStore(_context);
    }

    public async Task DisposeAsync()
    {
        await _context.Database.CloseConnectionAsync();
        await _context.DisposeAsync();
    }

    private static readonly EditScript Script = new(1,
    [
        new LoadActivityOp(101),
        new CropToOp(0, 10, 90),
    ]);

    private static EditSessionSnapshot Snapshot(
        string sessionId = "s-1", string userId = "u-1", string status = "active", int cursor = 2) => new(
        sessionId, userId, Script, cursor,
        [new TrackVersionStamp(101, 500, "deadbeef")],
        status,
        new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc),
        new DateTime(2026, 1, 1, 12, 5, 0, DateTimeKind.Utc));

    [Fact]
    public async Task CreateAndGet_RoundtripsAllColumns()
    {
        var session = Snapshot();
        await _store.CreateAsync(session);

        var read = await _store.GetAsync("s-1");

        Assert.NotNull(read);
        Assert.Equal(session.SessionId, read!.SessionId);
        Assert.Equal(session.UserId, read.UserId);
        Assert.Equal(session.Status, read.Status);
        Assert.Equal(session.Cursor, read.Cursor);
        Assert.Equal(session.Script.SchemaVersion, read.Script.SchemaVersion);
        Assert.Equal(session.Script.Ops.Count, read.Script.Ops.Count);
        Assert.Equal(session.Stamps.Single().ActivityId, read.Stamps.Single().ActivityId);
        Assert.Equal(session.Stamps.Single().BlobSha256, read.Stamps.Single().BlobSha256);
        Assert.Equal(session.CreatedAt, read.CreatedAt);
        Assert.Equal(session.UpdatedAt, read.UpdatedAt);
    }

    [Fact]
    public async Task UpdateScriptCursorStamps_PersistsAndBumpsUpdatedAt()
    {
        await _store.CreateAsync(Snapshot());

        var newScript = new EditScript(1, [new LoadActivityOp(101), new LoadActivityOp(202), new AppendOp(0, 1, TimeMode.Keep, null)]);
        var newStamps = new[] { new TrackVersionStamp(101, 500, "aaa"), new TrackVersionStamp(202, 300, "bbb") };
        var bumped = new DateTime(2026, 1, 1, 13, 0, 0, DateTimeKind.Utc);

        await _store.UpdateScriptCursorStampsAsync("s-1", newScript, cursor: 3, newStamps, bumped);

        var read = await _store.GetAsync("s-1");
        Assert.NotNull(read);
        Assert.Equal(3, read!.Cursor);
        Assert.Equal(3, read.Script.Ops.Count);
        Assert.Equal(2, read.Stamps.Count);
        Assert.Equal(bumped, read.UpdatedAt);
    }

    [Fact]
    public async Task Get_MissingId_ReturnsNull()
    {
        Assert.Null(await _store.GetAsync("nope"));
    }

    [Fact]
    public async Task Delete_RemovesRow()
    {
        await _store.CreateAsync(Snapshot());

        await _store.DeleteAsync("s-1");

        Assert.Null(await _store.GetAsync("s-1"));
    }

    [Fact]
    public async Task Delete_Missing_NoThrow()
    {
        await _store.DeleteAsync("nope"); // no row — must be a silent no-op
    }

    [Fact]
    public async Task UpdateScriptCursorStamps_WrongExpectedCursor_ReturnsFalseAndWritesNothing()
    {
        await _store.CreateAsync(Snapshot()); // stored cursor = 2
        var newScript = new EditScript(1, [new LoadActivityOp(101)]);
        var bumped = new DateTime(2026, 1, 1, 14, 0, 0, DateTimeKind.Utc);

        var ok = await _store.UpdateScriptCursorStampsAsync(
            "s-1", newScript, cursor: 1, [], bumped, expectedCursor: 1); // 1 != stored 2

        Assert.False(ok);
        var read = await _store.GetAsync("s-1");
        Assert.Equal(2, read!.Cursor);          // unchanged
        Assert.Equal(2, read.Script.Ops.Count); // unchanged
    }

    [Fact]
    public async Task UpdateScriptCursorStamps_MatchingCursor_ReturnsTrue()
    {
        await _store.CreateAsync(Snapshot()); // stored cursor = 2
        var newScript = new EditScript(1,
            [new LoadActivityOp(101), new LoadActivityOp(202), new CropToOp(0, 0, 5)]);
        var bumped = new DateTime(2026, 1, 1, 15, 0, 0, DateTimeKind.Utc);

        var ok = await _store.UpdateScriptCursorStampsAsync(
            "s-1", newScript, cursor: 3, [new TrackVersionStamp(101, 500, "a")], bumped, expectedCursor: 2);

        Assert.True(ok);
        var read = await _store.GetAsync("s-1");
        Assert.Equal(3, read!.Cursor);
        Assert.Equal(3, read.Script.Ops.Count);
    }

    [Fact]
    public async Task TrySetStatus_ExpectedCursorMismatch_ReturnsFalse()
    {
        await _store.CreateAsync(Snapshot(cursor: 3)); // active, cursor = 3
        var now = DateTime.UtcNow;

        var ok = await _store.TrySetStatusAsync("s-1", "active", "saved", now, expectedCursor: 2);

        Assert.False(ok);
        Assert.Equal("active", (await _store.GetAsync("s-1"))!.Status);
    }

    [Fact]
    public async Task Delete_SavedSession_KeepsRow()
    {
        await _store.CreateAsync(Snapshot(status: "saved")); // its commit references it

        Assert.False(await _store.DeleteAsync("s-1"));
        Assert.NotNull(await _store.GetAsync("s-1"));
    }

    // ---- denormalized resume fact: edit_count (slot0_activity_id is gone — the ride points at the session) ----

    [Fact]
    public async Task Create_WritesEditCount()
    {
        await _store.CreateAsync(Snapshot()); // [load 101, cropTo], cursor 2 → one edit behind the seed load

        Assert.Equal(1, await EditCountAsync("s-1"));
    }

    [Fact]
    public async Task Update_KeepsEditCountInStepWithCursor()
    {
        await _store.CreateAsync(Snapshot());
        var script = new EditScript(1,
            [new LoadActivityOp(101), new LoadActivityOp(202), new AppendOp(0, 1, TimeMode.Keep, null)]);
        var at = new DateTime(2026, 1, 1, 13, 0, 0, DateTimeKind.Utc);

        // Append: two seed loads + one real op behind the cursor.
        await _store.UpdateScriptCursorStampsAsync("s-1", script, cursor: 3, [], at, expectedCursor: 2);
        Assert.Equal(1, await EditCountAsync("s-1"));

        // Undo (cursor moves, script unchanged): the edit count must follow the cursor, not the op list.
        await _store.UpdateScriptCursorStampsAsync("s-1", script, cursor: 2, [], at, expectedCursor: 3);
        Assert.Equal(0, await EditCountAsync("s-1"));
    }

    [Fact]
    public async Task FindResumeHint_ActiveOnly_AndOwnerScoped()
    {
        await _store.CreateAsync(Snapshot(sessionId: "s-active"));
        await _store.CreateAsync(Snapshot(sessionId: "s-saved", status: "saved"));

        var hint = await _store.FindResumeHintAsync("u-1", "s-active");
        Assert.Equal("s-active", hint!.SessionId);
        Assert.Equal(1, hint.EditCount);

        Assert.Null(await _store.FindResumeHintAsync("u-1", "s-saved"));  // a saved session is not resumable
        Assert.Null(await _store.FindResumeHintAsync("u-2", "s-active")); // never leak another user's session
    }

    /// <summary>The denormalized column as the DB holds it (never re-derived on read).</summary>
    private async Task<int> EditCountAsync(string sessionId)
    {
        await _context.Database.OpenConnectionAsync();
        try
        {
            var conn = (SqliteConnection)_context.Database.GetDbConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT edit_count FROM Activity_Edit_Session WHERE session_id = $id;";
            var p = cmd.CreateParameter();
            p.ParameterName = "$id";
            p.Value = sessionId;
            cmd.Parameters.Add(p);
            await using var reader = await cmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            return reader.GetInt32(0);
        }
        finally
        {
            await _context.Database.CloseConnectionAsync();
        }
    }
}
