using System.Data.Common;
using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Lifecycle;
using RouteBaseWeb.Tests.Helpers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Lifecycle;

/// <summary>
/// The batch lifecycle seam (perf P7): TransitionManyAsync/RestoreManyAsync must be behaviourally
/// identical to N single-item calls — same validation, same lifecycle_prev bookkeeping, ONE audit
/// row per moved item — while writing the whole set in one save. The last test pins the "one"
/// with an EF command counter: the batch must not degrade back into per-item round-trips.
/// </summary>
public class BatchLifecycleTests : IAsyncLifetime
{
    private const string Me = "me";
    private const string Other = "other";

    private SqliteConnection _conn = null!;
    private RouteBaseDbContext _context = null!;
    private TestVisibilityService _visibility = null!;
    private ActivityLifecycleService _lifecycle = null!;

    public async Task InitializeAsync()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        await _conn.OpenAsync();
        var options = new DbContextOptionsBuilder<RouteBaseDbContext>().UseSqlite(_conn).Options;
        _context = new RouteBaseDbContext(options);
        await _context.Database.EnsureCreatedAsync();

        foreach (var uid in new[] { Me, Other })
            _context.Users.Add(new AppUser
            {
                UserId = uid, DisplayName = uid, Email = $"{uid}@test.com", IsAdmin = false,
                CreatedAt = DateTime.UtcNow.ToString("O"), LastLogin = DateTime.UtcNow.ToString("O"),
            });
        await _context.SaveChangesAsync();

        _visibility = new TestVisibilityService(Me);
        _lifecycle = TestLifecycle.For(_context, _visibility, new UserCacheInvalidator());
    }

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        await _conn.DisposeAsync();
    }

    private async Task<int> AddRideAsync(TrackId trackId, string lifecycle,
        string? prev = null, string? reason = null, string userId = Me, int? supersededBy = null)
    {
        _context.Activities.Add(new Activity
        {
            TrackId = trackId, UserId = userId, TrackName = $"ride-{trackId}", ActivityType = "Ride",
            Lifecycle = lifecycle, LifecyclePrev = prev, LifecycleReason = reason,
            SupersededBy = supersededBy is { } sb ? new TrackId(sb) : (TrackId?)null,
        });
        await _context.SaveChangesAsync();
        return trackId.Value;
    }

    private async Task<Activity> ReloadAsync(TrackId trackId)
    {
        _context.ChangeTracker.Clear();
        return await _context.Activities.FirstAsync(a => a.TrackId == trackId);
    }

    [Fact]
    public async Task TransitionMany_MovesAll_OneAuditRowPerItem()
    {
        await AddRideAsync(new TrackId(1), LifecycleStates.Released);
        await AddRideAsync(new TrackId(2), LifecycleStates.Released);
        await AddRideAsync(new TrackId(3), LifecycleStates.Released);

        var result = await _lifecycle.TransitionManyAsync(
            [new TrackId(1), new TrackId(2), new TrackId(3)], LifecycleStates.Trashed, LifecycleReasons.User);

        Assert.Equal([1, 2, 3], result.MovedIds.Select(t => t.Value).Order());
        foreach (int id in new[] { 1, 2, 3 })
        {
            var a = await ReloadAsync(new TrackId(id));
            Assert.Equal(LifecycleStates.Trashed, a.Lifecycle);
            Assert.Equal(LifecycleStates.Released, a.LifecyclePrev);
            Assert.Equal(LifecycleReasons.User, a.LifecycleReason);
        }

        // The batch seam never thins the audit: one row per moved item, fully attributed.
        var logs = await _context.ActivityLifecycleLogs.ToListAsync();
        Assert.Equal(3, logs.Count);
        Assert.All(logs, l =>
        {
            Assert.Equal(LifecycleStates.Released, l.FromState);
            Assert.Equal(LifecycleStates.Trashed, l.ToState);
            Assert.Equal(LifecycleReasons.User, l.Reason);
            Assert.Equal(Me, l.UserId);
        });
        Assert.Equal([1, 2, 3], logs.Select(l => l.TrackId.Value).Order());
    }

    [Fact]
    public async Task TransitionMany_SkipsForeignAndInvalid_MovesTheRest()
    {
        await AddRideAsync(new TrackId(1), LifecycleStates.Released);
        await AddRideAsync(new TrackId(2), LifecycleStates.Trashed, prev: LifecycleStates.Released); // self-transition → invalid
        await AddRideAsync(new TrackId(3), LifecycleStates.Released, userId: Other);                 // foreign → never loaded
        await AddRideAsync(new TrackId(4), LifecycleStates.Released);

        var result = await _lifecycle.TransitionManyAsync(
            [new TrackId(1), new TrackId(2), new TrackId(3), new TrackId(4), new TrackId(999)], LifecycleStates.Trashed, LifecycleReasons.User);

        Assert.Equal([1, 4], result.MovedIds.Select(t => t.Value).Order());
        Assert.Equal(LifecycleStates.Released, (await ReloadAsync(new TrackId(3))).Lifecycle); // untouched
        Assert.Equal(2, await _context.ActivityLifecycleLogs.CountAsync());       // no audit rows for skips
    }

    [Fact]
    public async Task RestoreMany_ReturnsEachToItsPrev_WithTheSingleItemReasonRules()
    {
        // Not trashed at all: skipped. Also serves as the SupersededBy FK target below.
        await AddRideAsync(new TrackId(3), LifecycleStates.Released);
        // Deleted by hand from circulation: back to released, reason 'user'.
        await AddRideAsync(new TrackId(1), LifecycleStates.Trashed,
            prev: LifecycleStates.Released, reason: LifecycleReasons.User, supersededBy: 3);
        // Rejected by the review queue: back to the queue — in-work + 'awaiting-review'.
        await AddRideAsync(new TrackId(2), LifecycleStates.Trashed,
            prev: LifecycleStates.InWork, reason: LifecycleReasons.ReviewRejected);

        var result = await _lifecycle.RestoreManyAsync([new TrackId(1), new TrackId(2), new TrackId(3)]);

        Assert.Equal([1, 2], result.MovedIds.Select(t => t.Value).Order());

        var restored = await ReloadAsync(new TrackId(1));
        Assert.Equal(LifecycleStates.Released, restored.Lifecycle);
        Assert.Equal(LifecycleReasons.User, restored.LifecycleReason);
        Assert.Null(restored.SupersededBy); // no longer merged into anything

        var backToQueue = await ReloadAsync(new TrackId(2));
        Assert.Equal(LifecycleStates.InWork, backToQueue.Lifecycle);
        Assert.Equal(LifecycleReasons.AwaitingReview, backToQueue.LifecycleReason);
    }

    [Fact]
    public async Task TransitionMany_OneLoadOneCommit_ForTheWholeBatch()
    {
        // P7's claim, stated honestly for SQLite (whose EF provider writes one command per row): the
        // batch must do ONE owner-scoped load and commit everything in ONE transaction — not the old
        // per-item load + save. Commits pin the transaction shape; the command bound catches a
        // reintroduced per-item reload (old shape: ≥3 commands/item + a commit each).
        var counter = new CountingInterceptor();
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        var options = new DbContextOptionsBuilder<RouteBaseDbContext>()
            .UseSqlite(conn).AddInterceptors(counter).Options;
        await using var context = new RouteBaseDbContext(options);
        await context.Database.EnsureCreatedAsync();

        context.Users.Add(new AppUser
        {
            UserId = Me, DisplayName = Me, Email = "me@test.com", IsAdmin = false,
            CreatedAt = DateTime.UtcNow.ToString("O"), LastLogin = DateTime.UtcNow.ToString("O"),
        });
        for (int id = 1; id <= 10; id++)
            context.Activities.Add(new Activity
            {
                TrackId = new TrackId(id), UserId = Me, TrackName = $"ride-{id}", ActivityType = "Ride",
                Lifecycle = LifecycleStates.Released,
            });
        await context.SaveChangesAsync();

        var lifecycle = TestLifecycle.For(context, new TestVisibilityService(Me), new UserCacheInvalidator());
        counter.Commands = 0;
        counter.Commits = 0;

        var result = await lifecycle.TransitionManyAsync(
            Enumerable.Range(1, 10).Select(i => new TrackId(i)).ToList(), LifecycleStates.Trashed, LifecycleReasons.User);

        Assert.Equal(10, result.Moved);
        Assert.Equal(1, counter.Commits);            // ONE transaction for the whole set (was one per item)
        Assert.InRange(counter.Commands, 1, 22);     // 1 load + ≤2 writes/item; old shape was ≥30 for 10
    }

    private sealed class CountingInterceptor : DbCommandInterceptor, IDbTransactionInterceptor
    {
        public int Commands;
        public int Commits;

        public void TransactionCommitting(
            DbTransaction transaction, TransactionEventData eventData, InterceptionResult result) =>
            Commits++;

        public ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction, TransactionEventData eventData, InterceptionResult result,
            CancellationToken cancellationToken = default)
        { Commits++; return ValueTask.FromResult(result); }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        { Commands++; return base.ReaderExecuting(command, eventData, result); }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        { Commands++; return base.ReaderExecutingAsync(command, eventData, result, cancellationToken); }

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        { Commands++; return base.NonQueryExecuting(command, eventData, result); }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        { Commands++; return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken); }
    }
}
