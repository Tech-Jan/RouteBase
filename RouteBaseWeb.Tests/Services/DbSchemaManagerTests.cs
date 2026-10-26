using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace RouteBaseWeb.Tests.Services;

/// <summary>
/// Regression tests for <see cref="DbSchemaManager"/> startup schema maintenance (SL-03): the init
/// block must run tables → columns → indexes so ALTER/index passes never hit a missing table,
/// and a table created by the schema manager must match the EF entity column-for-column.
/// </summary>
public class DbSchemaManagerTests : IAsyncLifetime
{
    private RouteBaseDbContext _context = null!;
    private DbSchemaManager _schema = null!;

    public async Task InitializeAsync()
    {
        var conn = $"Data Source=file:schemamgr_test_{Guid.NewGuid():N}?mode=memory&cache=shared";
        var options = new DbContextOptionsBuilder<RouteBaseDbContext>().UseSqlite(conn).Options;
        _context = new RouteBaseDbContext(options);
        await _context.Database.EnsureCreatedAsync();
        _schema = new DbSchemaManager(_context);
    }

    public Task DisposeAsync() => _context.DisposeAsync().AsTask();

    /// <summary>Runs the schema-manager methods in the Program.cs phase order (tables → columns → indexes).</summary>
    private async Task RunInitPhasesAsync()
    {
        await _schema.EnsureUsersTableAsync();
        await _schema.EnsureFriendshipsTableAsync();
        await _schema.EnsureRecordingSessionTableAsync();
        await _schema.EnsureHeatmapTablesAsync();
        await _schema.EnsureActivityBboxTableAsync();
        await _schema.EnsureNewColumnsAsync();
        await _schema.NormalizeDateTimeTextFormatsAsync();
        await _schema.EnsureIndexesAsync();
    }

    [Fact]
    public async Task EnsureSchema_OnDbMissingRecordingSessionTable_CreatesCompleteTable()
    {
        // Simulate a pre-recording-era DB (restored backup / old copy): the table doesn't exist.
        await _context.Database.ExecuteSqlRawAsync("DROP TABLE RecordingSession;");

        // Must not throw (EnsureNewColumnsAsync ALTERs RecordingSession — the table must be recreated first).
        await RunInitPhasesAsync();

        var columns = await _context.Database
            .SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('RecordingSession')")
            .ToListAsync();

        // The DDL-created table must carry every EF-entity column, including the one the entity has (SL-03).
        Assert.Contains("timezone_offset_minutes", columns, StringComparer.Ordinal);
        Assert.Contains("track_id", columns, StringComparer.Ordinal);
        Assert.Contains("last_sequence", columns, StringComparer.Ordinal);
    }

    [Fact]
    public async Task EnsureRecordingSessionTable_CreatesTheSingleActiveSessionIndex()
    {
        // RS-02: the partial unique index is the race-proof backstop under RecordingService's
        // check-then-insert. Seed one active session, then prove a second one for the same user is
        // rejected by the STORE, not merely by the application check.
        await _context.Database.ExecuteSqlRawAsync(
            "INSERT INTO Activity (track_ID, start_datetime_utc, visibility) VALUES " +
            "(101, '2025-01-01 00:00:00', 'friends'), (102, '2025-06-01 00:00:00', 'friends');");

        await _schema.EnsureRecordingSessionTableAsync();

        await _context.Database.ExecuteSqlRawAsync(
            "INSERT INTO RecordingSession (session_id, track_id, user_id, status, activity_type, started_at) " +
            "VALUES ('first', 101, 'u1', 'recording', 'Run', '2025-01-01 00:00:00');");

        await Assert.ThrowsAsync<SqliteException>(() => _context.Database.ExecuteSqlRawAsync(
            "INSERT INTO RecordingSession (session_id, track_id, user_id, status, activity_type, started_at) " +
            "VALUES ('second', 102, 'u1', 'paused', 'Run', '2025-06-01 00:00:00');"));
    }

    [Fact]
    public async Task NormalizeDateTimes_ConvertsIsoSeparator()
    {
        // Seed a legacy ISO-separator Activity row (the 'T' between date and time).
        await _context.Database.ExecuteSqlRawAsync(
            "INSERT INTO Activity (track_ID, start_datetime_loc, start_datetime_utc, visibility) " +
            "VALUES (1, '2025-06-10T08:30:00', '2025-06-10T06:30:00', 'friends');");

        await _schema.NormalizeDateTimeTextFormatsAsync();

        var stored = (await _context.Database
            .SqlQueryRaw<string>("SELECT start_datetime_loc AS Value FROM Activity WHERE track_ID = 1")
            .ToListAsync()).Single();
        Assert.Equal("2025-06-10 08:30:00", stored);

        // The EF LINQ date filter now matches the formerly-ISO row on its own local day.
        int matches = await _context.Activities
            .CountAsync(a => a.StartDateTimeLocal >= new DateTime(2025, 6, 10, 0, 0, 0, DateTimeKind.Unspecified)
                          && a.StartDateTimeLocal <  new DateTime(2025, 6, 11, 0, 0, 0, DateTimeKind.Unspecified));
        Assert.Equal(1, matches);
    }

    [Fact]
    public async Task EnsureTrackEditTablesAsync_IsIdempotentAndCreatesBoth()
    {
        await _schema.EnsureTrackEditTablesAsync();
        await _schema.EnsureTrackEditTablesAsync(); // must not throw (IF NOT EXISTS)

        var tables = await _context.Database
            .SqlQueryRaw<string>(
                "SELECT name AS Value FROM sqlite_master WHERE type = 'table' " +
                "AND name IN ('Track_Edit_Commit', 'Activity_Edit_Session')")
            .ToListAsync();
        Assert.Equal(2, tables.Count);

        var commitCols = await _context.Database
            .SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('Track_Edit_Commit')")
            .ToListAsync();
        Assert.Contains("commit_id", commitCols, StringComparer.Ordinal);
        Assert.Contains("script_json", commitCols, StringComparer.Ordinal);
        Assert.Contains("reverted_at", commitCols, StringComparer.Ordinal);
        // The id lists are derived from the version rows + consumed_commit_id, not stored as JSON columns.
        Assert.DoesNotContain("replaced_ids", commitCols, StringComparer.Ordinal);
        Assert.DoesNotContain("created_ids", commitCols, StringComparer.Ordinal);
        Assert.DoesNotContain("trashed_ids", commitCols, StringComparer.Ordinal);

        var sessionCols = await _context.Database
            .SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('Activity_Edit_Session')")
            .ToListAsync();
        Assert.Contains("session_id", sessionCols, StringComparer.Ordinal);
        Assert.Contains("cursor", sessionCols, StringComparer.Ordinal);
        Assert.Contains("stamps_json", sessionCols, StringComparer.Ordinal);
    }

    [Fact]
    public async Task EnsureProfileSettingsTable_CreatesTheTightenedShape()
    {
        // The raw-DDL path, not EF's: a context with no EnsureCreated, so the CREATE below is the one
        // under test. UserId NOT NULL and the absence of DataType/Description/DefaultValue are the shape
        // the settings slice assumes (every row belongs to a user; defaults live in ProfileSettingsDefaults).
        var conn = $"Data Source=file:psshape_{Guid.NewGuid():N}?mode=memory&cache=shared";
        var options = new DbContextOptionsBuilder<RouteBaseDbContext>().UseSqlite(conn).Options;
        await using var ctx = new RouteBaseDbContext(options);
        await ctx.Database.OpenConnectionAsync();

        await new DbSchemaManager(ctx).EnsureProfileSettingsTableAsync();

        var columns = new Dictionary<string, bool>(StringComparer.Ordinal);
        await using (var cmd = ctx.Database.GetDbConnection().CreateCommand())
        {
            cmd.CommandText = "SELECT name, \"notnull\" FROM pragma_table_info('ProfileSettings')";
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                columns[reader.GetString(0)] = reader.GetInt64(1) == 1;
        }

        Assert.Equal(
            new[] { "Category", "CreatedAt", "Id", "SettingKey", "SettingValue", "UpdatedAt", "UserId" },
            columns.Keys.OrderBy(c => c, StringComparer.Ordinal).ToArray());
        Assert.True(columns["UserId"], "UserId must be NOT NULL — every settings row belongs to a user");
    }
}
