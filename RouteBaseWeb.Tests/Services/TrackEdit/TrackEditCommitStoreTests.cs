using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.TrackEdit;
using RouteBaseWeb.Services.Tracks;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace RouteBaseWeb.Tests.Services.TrackEdit;

/// <summary>
/// Round-trip semantics for <see cref="TrackEditCommitStore"/> against a real (in-memory) SQLite context.
/// A commit row stores the SCRIPT; what it DID is derived — the versions carrying its commit_id (seq 1 =
/// created that track, seq > 1 = replaced one) and the activities carrying its consumed_commit_id. These
/// tests pin that derivation, including the property no stored id list had: purge a track and it drops out
/// of the answer instead of dangling.
/// </summary>
public class TrackEditCommitStoreTests : IAsyncLifetime
{
    private RouteBaseDbContext _context = null!;
    private TrackEditCommitStore _store = null!;
    private TrackContentStore _content = null!;

    public async Task InitializeAsync()
    {
        var conn = $"Data Source=file:trackeditcommit_test_{Guid.NewGuid():N}?mode=memory&cache=shared";
        var options = new DbContextOptionsBuilder<RouteBaseDbContext>().UseSqlite(conn).Options;
        _context = new RouteBaseDbContext(options);
        await _context.Database.OpenConnectionAsync();
        await _context.Database.EnsureCreatedAsync();
        var schema = new DbSchemaManager(_context);
        await schema.EnsureTrackEditTablesAsync();
        await schema.EnsureTrackContentTablesAsync();
        _store = new TrackEditCommitStore(_context);
        _content = new TrackContentStore(_context, new TrackObjectStore(_context, TimeProvider.System), TimeProvider.System);

        _context.Users.Add(new AppUser { UserId = "u-1", DisplayName = "u", Email = "u@t.com", IsAdmin = false });
        await _context.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _context.Database.CloseConnectionAsync();
        await _context.DisposeAsync();
    }

    private static readonly EditScript Script = new(1,
    [
        new LoadActivityOp(101),
        new LoadActivityOp(202),
        new AppendOp(0, 1, TimeMode.Close, 60),
    ]);

    [Fact]
    public async Task Insert_ThenGet_RoundtripsScriptAndMode()
    {
        var committedAt = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        var commitId = await _store.InsertAsync("u-1", committedAt, "replace", "session-1", Script);
        Assert.True(commitId.Value > 0);

        var read = await _store.GetAsync(commitId);

        Assert.NotNull(read);
        Assert.Equal(commitId, read!.CommitId);
        Assert.Equal("u-1", read.UserId);
        Assert.Equal(committedAt, read.CommittedAt);
        Assert.Equal("replace", read.SaveMode);
        Assert.Equal("session-1", read.SessionId);
        Assert.Equal(3, read.Script.Ops.Count);
        Assert.Null(read.RevertedAt);
        Assert.Empty(read.ReplacedIds);   // nothing stamped yet — a commit that touched nothing did nothing
        Assert.Empty(read.CreatedIds);
        Assert.Empty(read.TrashedIds);
    }

    /// <summary>The three id lists, derived from what the rows actually say.</summary>
    [Fact]
    public async Task IdLists_AreDerivedFromVersionsAndConsumedStamp()
    {
        var commitId = await _store.InsertAsync("u-1", DateTime.UtcNow, "replace", null, Script);

        await AddActivityAsync(new TrackId(101));
        await _content.AppendVersionAsync(new TrackId(101), [1], pointCount: 1);                       // v1: the import
        await _content.AppendVersionAsync(new TrackId(101), [2], pointCount: 1, commitId: commitId);   // v2: the edit → replaced

        await AddActivityAsync(new TrackId(303));
        await _content.AppendVersionAsync(new TrackId(303), [3], pointCount: 1);
        await _content.StampCreatingCommitAsync(new TrackId(303), commitId);                            // seq 1 → created

        await AddActivityAsync(new TrackId(202));
        await _context.Database.ExecuteSqlRawAsync(
            "UPDATE Activity SET consumed_commit_id = {0} WHERE track_ID = 202;", commitId.Value);

        var read = await _store.GetAsync(commitId);

        Assert.Equal(new[] { new TrackId(101) }, read!.ReplacedIds);
        Assert.Equal(new[] { new TrackId(303) }, read.CreatedIds);
        Assert.Equal(new[] { new TrackId(202) }, read.TrashedIds);
    }

    /// <summary>What a stored JSON id list could never do: a purged track simply stops being part of the
    /// answer, instead of leaving a dangling id behind.</summary>
    [Fact]
    public async Task PurgedTrack_DropsOutOfTheDerivedLists()
    {
        var commitId = await _store.InsertAsync("u-1", DateTime.UtcNow, "replace", null, Script);
        await AddActivityAsync(new TrackId(101));
        await _content.AppendVersionAsync(new TrackId(101), [1], pointCount: 1);
        await _content.AppendVersionAsync(new TrackId(101), [2], pointCount: 1, commitId: commitId);
        Assert.Single((await _store.GetAsync(commitId))!.ReplacedIds);

        await _context.Database.ExecuteSqlRawAsync("DELETE FROM Activity WHERE track_ID = 101;");

        Assert.Empty((await _store.GetAsync(commitId))!.ReplacedIds);
    }

    [Fact]
    public async Task Insert_NullSessionId_ReadsBackNull()
    {
        var commitId = await _store.InsertAsync("u-1", DateTime.UtcNow, "copy", null, Script);

        var read = await _store.GetAsync(commitId);
        Assert.Null(read!.SessionId);
    }

    [Fact]
    public async Task Get_MissingId_ReturnsNull()
    {
        Assert.Null(await _store.GetAsync(new CommitId(999)));
    }

    [Fact]
    public async Task MarkReverted_SetsRevertedAt()
    {
        var commitId = await _store.InsertAsync("u-1", DateTime.UtcNow, "replace", null, Script);
        var revertedAt = new DateTime(2026, 1, 2, 8, 0, 0, DateTimeKind.Utc);

        await _store.MarkRevertedAsync(commitId, revertedAt);

        var read = await _store.GetAsync(commitId);
        Assert.Equal(revertedAt, read!.RevertedAt);
    }

    // ListForUser_NewestFirst retired (DC29-R1): it pinned TrackEditCommitStore.ListForUserAsync, which
    // was dead in production (superseded by NewestOtherCommitReplacingAsync) and is now deleted. The
    // subject is gone, not weakened — the save-flow "a commit row was/ wasn't written" oracle it shared
    // moved to TrackEditTestDb.CommitIdsForAsync (DC-2 pattern).

    private async Task AddActivityAsync(TrackId trackId)
    {
        _context.Activities.Add(new Activity { TrackId = trackId, UserId = "u-1" });
        await _context.SaveChangesAsync();
    }
}
