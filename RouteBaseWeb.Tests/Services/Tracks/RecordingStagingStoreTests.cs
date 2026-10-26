using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Sync;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Tracks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Tracks;

/// <summary>
/// The staging writer/reader on the live-recording data-loss path. Real in-memory SQLite; a separate
/// keep-alive connection holds the shared-cache DB open while the store drives the CONTEXT connection through
/// its own ref-counted Open/Close — that split is deliberate: it lets a method whose <c>OpenConnectionAsync</c>
/// went missing be caught (the connection is genuinely closed between calls), instead of masked by a test that
/// keeps it open.
///
/// What is pinned: every optional channel round-trips through append→read, a mid-batch constraint violation
/// rolls the WHOLE batch back (never a partial write), a null timestamp is rejected by the column's NOT NULL
/// (not by a null-deref in ToEpoch), MAX(distance) reads back both with and without rows, and every read/append
/// still works when enlisted in an ambient EF transaction.
/// </summary>
public class RecordingStagingStoreTests : IAsyncLifetime
{
    private static readonly TrackId TrackA = new(1);
    private static readonly TrackId TrackB = new(2);
    private static readonly TrackId EmptyTrack = new(3);

    // 2025-01-01T00:00:00Z = 1735689600 epoch seconds; +5s below. ToEpoch truncates to whole seconds.
    private static readonly DateTime TsBase = new(2025, 1, 1, 0, 0, 5, DateTimeKind.Utc);
    private const long TsBaseEpoch = 1735689605;

    private SqliteConnection _keepAlive = null!;
    private RouteBaseDbContext _context = null!;
    private RecordingStagingStore _store = null!;

    public async Task InitializeAsync()
    {
        var connStr = $"Data Source=file:recstaging_{Guid.NewGuid():N}?mode=memory&cache=shared";
        _keepAlive = new SqliteConnection(connStr);
        await _keepAlive.OpenAsync();

        var options = new DbContextOptionsBuilder<RouteBaseDbContext>().UseSqlite(connStr).Options;
        _context = new RouteBaseDbContext(options);
        await _context.Database.EnsureCreatedAsync();
        await new DbSchemaManager(_context).EnsureRecordingStagingTableAsync();

        // Recording_Staging.track_ID has an FK to Activity(track_ID); seed the parents so INSERTs satisfy it.
        _context.Users.Add(new AppUser
        {
            UserId = "u", DisplayName = "u", Email = "u@test.com", IsAdmin = false,
            CreatedAt = DateTime.UtcNow.ToString("O"), LastLogin = DateTime.UtcNow.ToString("O"),
        });
        foreach (var id in new[] { TrackA, TrackB, EmptyTrack })
            _context.Activities.Add(new Activity { TrackId = id, UserId = "u" });
        await _context.SaveChangesAsync();

        _store = new RecordingStagingStore(_context);
    }

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        await _keepAlive.DisposeAsync();
    }

    private static ExternalGpsPoint Pt(
        double lat, double lng, DateTime? ts,
        double? alt = null, double? speed = null, double? dist = null, double? pressure = null) => new()
    {
        Lat = lat, Lng = lng, TimestampUtc = ts,
        Altitude = alt, Speed = speed, Distance = dist, Pressure = pressure,
    };

    /// <summary>Every optional channel a point carries is what comes back — the write binds (RS store lines
    /// 69-73) and the read materializes (ReadPoint) each one. A dropped Speed or Pressure (bound as DBNull, or
    /// read as null) would be a silent per-column data loss no coarser assertion catches.</summary>
    [Fact]
    public async Task Append_ThenRead_RoundTripsEveryChannel()
    {
        var p = Pt(12.5, -34.25, TsBase, alt: 100.5, speed: 3.5, dist: 42.0, pressure: 1013.25);
        await _store.AppendAsync(TrackA, startSeq: 0, new[] { p });

        var read = Assert.Single(await _store.GetPointsAsync(TrackA));
        Assert.Equal(0L, read.Seq);
        Assert.Equal(12.5, read.Lat);
        Assert.Equal(-34.25, read.Lng);
        Assert.Equal(100.5, read.Altitude);
        Assert.Equal(3.5, read.Speed);        // pins the Speed bind + read
        Assert.Equal(42.0, read.Distance);
        Assert.Equal(TsBaseEpoch, read.TsEpoch);
        Assert.Equal(1013.25, read.Pressure);  // pins the Pressure bind + read
    }

    /// <summary>A NULL in an optional channel round-trips as NULL — the store must not confuse "no reading"
    /// with a zero. (The read side is <c>IsDBNull(n) ? null : Get…</c>; asserting null pins the true branch.)</summary>
    [Fact]
    public async Task Append_ThenRead_KeepsNullChannelsNull()
    {
        await _store.AppendAsync(TrackA, 0, new[] { Pt(1.0, 2.0, TsBase) });

        var read = Assert.Single(await _store.GetPointsAsync(TrackA));
        Assert.Null(read.Altitude);
        Assert.Null(read.Speed);
        Assert.Null(read.Distance);
        Assert.Null(read.Pressure);
    }

    /// <summary>An empty batch writes nothing and does not throw (the early-return fast path).</summary>
    [Fact]
    public async Task Append_EmptyBatch_WritesNothing()
    {
        await _store.AppendAsync(TrackA, 0, Array.Empty<ExternalGpsPoint>());
        Assert.Equal(0L, await _store.CountAsync(TrackA));
    }

    /// <summary>
    /// The data-loss guard: outside an ambient transaction the batch is wrapped in a local transaction, so a
    /// constraint violation on ANY row rolls back the whole batch — never a torn write of the rows before it.
    /// The physical connection is held open across the failure on purpose: a transaction left un-disposed by a
    /// broken finally then stays pending and trips the next command, instead of being silently rolled back by
    /// the connection close (which would hide the defect).
    /// </summary>
    [Fact]
    public async Task Append_MidBatchConstraintViolation_RollsBackTheWholeBatch()
    {
        await _context.Database.OpenConnectionAsync();
        try
        {
            var good = Pt(1.0, 2.0, TsBase);
            var bad = Pt(3.0, 4.0, ts: null);   // ts_utc is NOT NULL → row 2 fails mid-batch
            await Assert.ThrowsAnyAsync<Exception>(
                () => _store.AppendAsync(TrackA, 0, new[] { good, bad, good }));

            Assert.Equal(0L, await _store.CountAsync(TrackA));   // row 1 must not have survived
        }
        finally
        {
            await _context.Database.CloseConnectionAsync();
        }
    }

    /// <summary>A null timestamp is rejected by the column's NOT NULL constraint (a SqliteException), not by a
    /// null-deref while computing the epoch — ToEpoch maps a null instant to null and lets the DB be the gate.</summary>
    [Fact]
    public async Task Append_NullTimestamp_FailsAsAConstraintViolation()
    {
        var ex = await Assert.ThrowsAsync<SqliteException>(
            () => _store.AppendAsync(TrackA, 0, new[] { Pt(1.0, 2.0, ts: null) }));
        Assert.Equal(19, ex.SqliteErrorCode);   // SQLITE_CONSTRAINT — the ts_utc NOT NULL, not an NRE in ToEpoch
    }

    [Fact]
    public async Task GetLastPoint_ReturnsHighestSeq_OrNullWhenEmpty()
    {
        Assert.Null(await _store.GetLastPointAsync(EmptyTrack));

        await _store.AppendAsync(TrackA, 0, new[]
        {
            Pt(1.0, 1.0, TsBase, dist: 10.0),
            Pt(2.0, 2.0, TsBase, dist: 20.0),
            Pt(3.0, 3.0, TsBase, dist: 30.0),
        });

        var last = await _store.GetLastPointAsync(TrackA);
        Assert.NotNull(last);
        Assert.Equal(2L, last!.Value.Seq);      // seq 2 is the highest
        Assert.Equal(30.0, last.Value.Distance);
    }

    [Fact]
    public async Task Exists_TrueOnlyWhenTheTrackHasStagedRows()
    {
        Assert.False(await _store.ExistsAsync(TrackA));
        await _store.AppendAsync(TrackA, 0, new[] { Pt(1.0, 2.0, TsBase) });
        Assert.True(await _store.ExistsAsync(TrackA));
        Assert.False(await _store.ExistsAsync(EmptyTrack));   // scoped to the track
    }

    [Fact]
    public async Task Count_And_NextSeq_TrackTheRowCount()
    {
        Assert.Equal(0L, await _store.CountAsync(TrackA));
        Assert.Equal(0L, await _store.NextSeqAsync(EmptyTrack));   // COALESCE(MAX,-1)+1 = 0 on an empty track

        await _store.AppendAsync(TrackA, 0, new[]
        {
            Pt(1.0, 1.0, TsBase), Pt(2.0, 2.0, TsBase), Pt(3.0, 3.0, TsBase),
        });

        Assert.Equal(3L, await _store.CountAsync(TrackA));
        Assert.Equal(3L, await _store.NextSeqAsync(TrackA));       // MAX(seq)=2 → next dense ordinal 3
    }

    [Fact]
    public async Task GetPointsAfterSeq_ReturnsOnlyStrictlyNewerRows()
    {
        await _store.AppendAsync(TrackA, 0, new[]
        {
            Pt(0.0, 0.0, TsBase), Pt(1.0, 1.0, TsBase),
            Pt(2.0, 2.0, TsBase), Pt(3.0, 3.0, TsBase),
        });

        var after = await _store.GetPointsAfterSeqAsync(TrackA, afterSeq: 1);
        Assert.Equal(new[] { 2L, 3L }, after.Select(p => p.Seq));   // seq > 1 only
    }

    /// <summary>MAX(distance) over the staged rows — the value the append-time cumulative distance seeds from.</summary>
    [Fact]
    public async Task GetMaxDistance_ReturnsTheLargest_AcrossRows()
    {
        await _store.AppendAsync(TrackA, 0, new[]
        {
            Pt(1.0, 1.0, TsBase, dist: 10.0),
            Pt(2.0, 2.0, TsBase, dist: 30.0),   // the max is not the last row
            Pt(3.0, 3.0, TsBase, dist: 20.0),
        });

        Assert.Equal(30.0, await _store.GetMaxDistanceAsync(TrackA));
    }

    /// <summary>No staged rows → MAX(distance) is SQL NULL → the method returns null, not a throw from
    /// Convert.ToDouble(DBNull).</summary>
    [Fact]
    public async Task GetMaxDistance_EmptyTrack_ReturnsNull()
    {
        Assert.Null(await _store.GetMaxDistanceAsync(EmptyTrack));
    }

    /// <summary>The finalize-compaction delete is bounded to the caller's snapshot seq: rows at or below it go,
    /// later arrivals survive, and a null bound is a no-op (nothing existed at snapshot time).</summary>
    [Fact]
    public async Task DeleteUpToSeq_DropsThroughTheBound_AndNullIsANoOp()
    {
        await _store.AppendAsync(TrackA, 0, new[]
        {
            Pt(0.0, 0.0, TsBase), Pt(1.0, 1.0, TsBase),
            Pt(2.0, 2.0, TsBase), Pt(3.0, 3.0, TsBase), Pt(4.0, 4.0, TsBase),
        });

        await _store.DeleteUpToSeqAsync(TrackA, uptoSeq: null);   // no-op
        Assert.Equal(5L, await _store.CountAsync(TrackA));

        await _store.DeleteUpToSeqAsync(TrackA, uptoSeq: 2);      // seq 0,1,2 go; 3,4 survive
        Assert.Equal(new[] { 3L, 4L }, (await _store.GetPointsAsync(TrackA)).Select(p => p.Seq));
    }

    /// <summary>
    /// Every read and the append enlist any ambient EF transaction: Microsoft.Data.Sqlite throws when a command
    /// runs on a connection that has a pending transaction unless the command points at it. So a method whose
    /// EnlistAmbientTx went missing would throw here — this is the one test that exercises all of them at once.
    /// </summary>
    [Fact]
    public async Task AllReadsAndAppend_EnlistAnAmbientEfTransaction()
    {
        await _store.AppendAsync(TrackA, 0, new[]
        {
            Pt(1.0, 1.0, TsBase, dist: 10.0),
            Pt(2.0, 2.0, TsBase, dist: 30.0),
        });

        await using var tx = await _context.Database.BeginTransactionAsync();

        Assert.Equal(2, (await _store.GetPointsAsync(TrackA)).Count);            // ReadAsync
        Assert.Single(await _store.GetPointsAfterSeqAsync(TrackA, 0));           // ReadAsync (cursor branch)
        Assert.NotNull(await _store.GetLastPointAsync(TrackA));                  // GetLastPointAsync
        Assert.Equal(30.0, await _store.GetMaxDistanceAsync(TrackA));           // GetMaxDistanceAsync
        Assert.True(await _store.ExistsAsync(TrackA));                           // ExistsAsync
        Assert.Equal(2L, await _store.CountAsync(TrackA));                      // CountAsync
        Assert.Equal(2L, await _store.NextSeqAsync(TrackA));                    // NextSeqAsync

        await _store.AppendAsync(TrackA, 2, new[] { Pt(3.0, 3.0, TsBase, dist: 40.0) });   // append inside the tx
        Assert.Equal(3L, await _store.CountAsync(TrackA));

        await tx.CommitAsync();
        Assert.Equal(3L, await _store.CountAsync(TrackA));                      // the in-tx write persisted
    }
}
