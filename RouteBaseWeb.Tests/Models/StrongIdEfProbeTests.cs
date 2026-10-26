using RouteBaseWeb.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

// Namespace deliberately NOT RouteBaseWeb.Tests.Models — that would shadow `Models.` qualifiers
// repo-wide (the ConceptRefactors gotcha); every file in this folder flattens to Tests.
namespace RouteBaseWeb.Tests;

/// <summary>
/// THE K1 GATE (S6). Every EF behavior the strong-id recipe relies on, proven against real SQLite
/// through the value converter before any further id family is retyped: autoincrement readback into
/// the struct key, Where ==, Contains over a List of ids, nullable-FK write/read, and the FK's
/// ON DELETE SET NULL firing through the converter. If a probe here breaks, K1 stops — never
/// silently fall back to int keys.
/// </summary>
public class StrongIdEfProbeTests : IAsyncLifetime
{
    private SqliteConnection _keepAlive = null!;
    private RouteBaseDbContext _context = null!;

    public async Task InitializeAsync()
    {
        var conn = $"Data Source=file:strongid_{Guid.NewGuid():N}?mode=memory&cache=shared";
        _keepAlive = new SqliteConnection(conn);
        await _keepAlive.OpenAsync();
        _context = new RouteBaseDbContext(
            new DbContextOptionsBuilder<RouteBaseDbContext>().UseSqlite(conn).Options);
        await _context.Database.EnsureCreatedAsync();
        _context.Users.Add(new AppUser { UserId = "probe", DisplayName = "probe", Email = "p@t", IsAdmin = false });
        await _context.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        await _keepAlive.DisposeAsync();
    }

    private async Task<TrackEditCommit> AddCommitAsync()
    {
        var commit = new TrackEditCommit
        {
            UserId = "probe", CommittedAt = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc),
            SaveMode = "apply", ScriptJson = "{}",
        };
        _context.Set<TrackEditCommit>().Add(commit);
        await _context.SaveChangesAsync();
        return commit;
    }

    [Fact]
    public async Task Autoincrement_readback_populates_the_struct_key()
    {
        var a = await AddCommitAsync();
        var b = await AddCommitAsync();
        Assert.True(a.CommitId.Value > 0, "autoincrement did not read back through the converter");
        Assert.True(b.CommitId.Value > a.CommitId.Value, "keys are not monotonically assigned");
    }

    [Fact]
    public async Task Where_equality_translates_through_the_converter()
    {
        var commit = await AddCommitAsync();
        await AddCommitAsync(); // noise row
        var found = await _context.Set<TrackEditCommit>()
            .Where(c => c.CommitId == commit.CommitId)
            .SingleAsync();
        Assert.Equal(commit.CommitId, found.CommitId);
    }

    [Fact]
    public async Task Contains_over_a_list_of_ids_translates()
    {
        var a = await AddCommitAsync();
        var b = await AddCommitAsync();
        await AddCommitAsync(); // excluded
        var wanted = new List<CommitId> { a.CommitId, b.CommitId };
        var found = await _context.Set<TrackEditCommit>()
            .Where(c => wanted.Contains(c.CommitId))
            .OrderBy(c => c.CommitId)
            .Select(c => c.CommitId)
            .ToListAsync();
        Assert.Equal([a.CommitId, b.CommitId], found);
    }

    [Fact]
    public async Task Nullable_fk_writes_reads_and_nulls()
    {
        var commit = await AddCommitAsync();
        var activity = new Activity { TrackId = new TrackId(91001), UserId = "probe", TrackName = "probe-ride", EditedCommitId = commit.CommitId };
        _context.Activities.Add(activity);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var read = await _context.Activities.SingleAsync(a => a.TrackId == new TrackId(91001));
        Assert.Equal(commit.CommitId, read.EditedCommitId);

        read.EditedCommitId = null;
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        Assert.Null((await _context.Activities.SingleAsync(a => a.TrackId == new TrackId(91001))).EditedCommitId);
    }

    [Fact]
    public async Task Fk_on_delete_set_null_fires_through_the_converter()
    {
        var commit = await AddCommitAsync();
        _context.Activities.Add(new Activity { TrackId = new TrackId(91002), UserId = "probe", TrackName = "probe-ride-2", EditedCommitId = commit.CommitId });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        // Raw delete so the DATABASE (not EF fixup) is what nulls the FK — FKs are enforced in this
        // stack (bundled e_sqlite3 is built with SQLITE_DEFAULT_FOREIGN_KEYS).
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM Track_Edit_Commit WHERE commit_id = {0};", commit.CommitId.Value);
        Assert.Null((await _context.Activities.SingleAsync(a => a.TrackId == new TrackId(91002))).EditedCommitId);
    }
}
