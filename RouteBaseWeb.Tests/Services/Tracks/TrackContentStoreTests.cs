using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Tracks;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Tracks;

/// <summary>
/// The content axis' load-bearing semantics (ActivityLifecycle Batch 1), against a real (in-memory) SQLite
/// context: content is immutable and content-addressed, "changing a track" APPENDS a version and moves the
/// head, and nothing a later write does can destroy what an earlier version points at. Replaces the old
/// TrackBlobStoreTests, whose subject (a mutable one-row-per-track blob) no longer exists.
/// </summary>
public class TrackContentStoreTests : IAsyncLifetime
{
    private static readonly TrackId TheTrack = new(7);
    private static readonly TrackId OtherTrack = new(8);
    private static readonly CommitId TestCommit = new(42);

    private RouteBaseDbContext _context = null!;
    private ITrackObjectStore _objects = null!;
    private TrackContentStore _content = null!;

    public async Task InitializeAsync()
    {
        var conn = $"Data Source=file:trackcontent_test_{Guid.NewGuid():N}?mode=memory&cache=shared";
        var options = new DbContextOptionsBuilder<RouteBaseDbContext>().UseSqlite(conn).Options;
        _context = new RouteBaseDbContext(options);
        // Keep one connection open for the whole test so the shared-cache in-memory DB survives the
        // store's own ref-counted open/close cycles.
        await _context.Database.OpenConnectionAsync();
        await _context.Database.EnsureCreatedAsync();
        await new DbSchemaManager(_context).EnsureTrackContentTablesAsync();

        // Activity_Version has an FK to Activity(track_ID); seed the parents so INSERTs satisfy it.
        _context.Users.Add(new AppUser
        {
            UserId = "u", DisplayName = "u", Email = "u@test.com", IsAdmin = false,
            CreatedAt = DateTime.UtcNow.ToString("O"), LastLogin = DateTime.UtcNow.ToString("O"),
        });
        _context.Activities.Add(new Activity { TrackId = TheTrack, UserId = "u" });
        _context.Activities.Add(new Activity { TrackId = OtherTrack, UserId = "u" });
        await _context.SaveChangesAsync();

        // Activity_Version.commit_id is likewise a real FK (into Track_Edit_Commit) — a version can only be
        // stamped with a commit that exists. Seed the one the commit-stamping tests use.
        await SeedCommitAsync(TestCommit.Value);

        _objects = new TrackObjectStore(_context, TimeProvider.System);
        _content = new TrackContentStore(_context, _objects, TimeProvider.System);
    }

    public async Task DisposeAsync()
    {
        await _context.Database.CloseConnectionAsync();
        await _context.DisposeAsync();
    }

    [Fact]
    public async Task AppendVersion_ThenReadHead_ReturnsSameBytes()
    {
        var data = new byte[] { 1, 2, 3, 4, 5, 250, 255 };
        await _content.AppendVersionAsync(TheTrack, data, pointCount: 100);

        Assert.Equal(data, await _content.GetHeadDataAsync(TheTrack));
    }

    [Fact]
    public async Task Head_NoContent_ReturnsNull()
    {
        Assert.Null(await _content.GetHeadDataAsync(TheTrack));
    }

    /// <summary>
    /// The two counts the Details page renders so the history panel can skip its page-load fetch. They are the
    /// inputs to hasHistory() (version-history-model.ts), which owns the rule — so what is pinned here is that
    /// the COUNTS are right, not the verdict.
    ///
    /// AnyCommit is the whole point: it is what tells a ride BORN from a save (a merge product — one version,
    /// written by the merge commit) apart from an imported one (also one version, but no commit wrote it).
    /// </summary>
    [Fact]
    public async Task HistoryFacts_CountsVersionsAndWhetherASaveWroteAny()
    {
        Assert.Equal(new TrackHistoryFacts(0, false), await _content.HistoryFactsAsync(TheTrack));

        // An import: one version, no commit — nothing to show.
        await _content.AppendVersionAsync(TheTrack, [1], pointCount: 1);
        Assert.Equal(new TrackHistoryFacts(1, false), await _content.HistoryFactsAsync(TheTrack));

        // An edit of it: two versions, one of them a save's.
        await _content.AppendVersionAsync(TheTrack, [2], pointCount: 1, commitId: TestCommit);
        Assert.Equal(new TrackHistoryFacts(2, true), await _content.HistoryFactsAsync(TheTrack));

        // A merge product: its ONE version is the merge commit's. One version, but a history all the same —
        // the counts have to say so, or the panel hides the only door that can un-merge it.
        await _content.AppendVersionAsync(OtherTrack, [3], pointCount: 1, commitId: TestCommit);
        Assert.Equal(new TrackHistoryFacts(1, true), await _content.HistoryFactsAsync(OtherTrack));
    }

    /// <summary>The invariant the whole model rests on: an edit never overwrites — the superseded version is
    /// still there, under its own hash. (The bug this replaced: the pre-edit bytes lived only as a child row
    /// of the activity, so deleting the activity destroyed them.)</summary>
    [Fact]
    public async Task SecondVersion_MovesHead_AndLeavesTheFirstReadable()
    {
        var v1Bytes = new byte[] { 1, 1, 1 };
        var v2Bytes = new byte[] { 7, 8 };

        var v1 = await _content.AppendVersionAsync(TheTrack, v1Bytes, pointCount: 3);
        var v2 = await _content.AppendVersionAsync(TheTrack, v2Bytes, pointCount: 2);

        Assert.Equal(v2, await _content.GetHeadVersionIdAsync(TheTrack));
        Assert.Equal(v2Bytes, await _content.GetHeadDataAsync(TheTrack));

        var versions = await _content.ListVersionsAsync(TheTrack);
        Assert.Equal(new[] { 1, 2 }, versions.Select(v => v.Seq));
        Assert.Equal(v1Bytes, await _objects.GetAsync(versions[0].ObjectSha));   // v1's content survived the edit

        Assert.Equal([v1], await _content.ParentsOfAsync(v2));                   // lineage: v2 supersedes v1
        Assert.Empty(await _content.ParentsOfAsync(v1));                         // v1 is where lineage begins
    }

    /// <summary>Revert is a head move: no bytes written, no object touched.</summary>
    [Fact]
    public async Task SetHead_MovesBackToAnEarlierVersion()
    {
        var v1Bytes = new byte[] { 1, 1, 1 };
        var v1 = await _content.AppendVersionAsync(TheTrack, v1Bytes, pointCount: 3);
        await _content.AppendVersionAsync(TheTrack, [7, 8], pointCount: 2);

        await _content.SetHeadAsync(TheTrack, v1);

        Assert.Equal(v1, await _content.GetHeadVersionIdAsync(TheTrack));
        Assert.Equal(v1Bytes, await _content.GetHeadDataAsync(TheTrack));
    }

    /// <summary>"Restore v1" appends a NEW version reusing v1's object — history stays append-only, and the
    /// restore costs zero new bytes because the content is already stored.</summary>
    [Fact]
    public async Task RestoreVersion_AppendsAVersion_AndStoresNoNewObject()
    {
        var v1 = await _content.AppendVersionAsync(TheTrack, [1, 1, 1], pointCount: 3);
        await _content.AppendVersionAsync(TheTrack, [7, 8], pointCount: 2);
        int objectsBefore = await ObjectCountAsync();

        var v3 = await _content.RestoreVersionAsync(TheTrack, v1);

        Assert.Equal(objectsBefore, await ObjectCountAsync());                   // zero new bytes
        Assert.Equal(v3, await _content.GetHeadVersionIdAsync(TheTrack));
        Assert.Equal(new byte[] { 1, 1, 1 }, await _content.GetHeadDataAsync(TheTrack));

        var versions = await _content.ListVersionsAsync(TheTrack);
        Assert.Equal(new[] { 1, 2, 3 }, versions.Select(v => v.Seq));            // nothing renumbered or rewritten
        Assert.Equal(versions[0].ObjectSha, versions[2].ObjectSha);              // v3 IS v1's content
    }

    /// <summary>Identical content is one object, whoever writes it — the free half of content-addressing
    /// (re-importing the same GPX, or two activities that are the same ride).</summary>
    [Fact]
    public async Task IdenticalContent_AcrossTracks_IsStoredOnce()
    {
        var data = new byte[] { 4, 2 };
        await _content.AppendVersionAsync(TheTrack, data, pointCount: 1);
        await _content.AppendVersionAsync(OtherTrack, data, pointCount: 1);

        Assert.Equal(1, await ObjectCountAsync());
        Assert.Equal(await _content.GetHeadShaAsync(TheTrack), await _content.GetHeadShaAsync(OtherTrack));
    }

    /// <summary>Objects die by refcount, never by track key — a sweep must not touch content another version
    /// still references (that shared object is exactly what makes the naive "delete my blobs" purge wrong).</summary>
    [Fact]
    public async Task Sweep_ReclaimsOnlyUnreferencedObjects()
    {
        await _content.AppendVersionAsync(TheTrack, [1, 1, 1], pointCount: 3);
        await _content.AppendVersionAsync(TheTrack, [7, 8], pointCount: 2);
        string headSha = (await _content.GetHeadShaAsync(TheTrack))!;
        await _objects.PutAsync([9, 9, 9], codecVersion: 1, pointCount: 1);      // orphan: no version points at it

        int swept = await _objects.SweepUnreferencedAsync();

        Assert.Equal(1, swept);
        Assert.Equal(2, await ObjectCountAsync());                               // both of the track's versions kept
        Assert.NotNull(await _objects.GetAsync(headSha));
    }

    /// <summary>
    /// A scoped sweep only considers the objects the purge could have orphaned. This is what stops a delete
    /// from collecting an object another writer has just PUT but not yet attached a version to — the write
    /// is invisible to the purge because it was never a candidate, not because the timing happened to work.
    /// </summary>
    [Fact]
    public async Task ScopedSweep_LeavesObjectsItWasNotAskedAbout()
    {
        await _content.AppendVersionAsync(TheTrack, [1, 1, 1], pointCount: 3);
        string orphan = await _objects.PutAsync([9, 9, 9], codecVersion: 1, pointCount: 1);
        string inFlight = await _objects.PutAsync([4, 4], codecVersion: 1, pointCount: 1);  // version row not written yet

        int swept = await _objects.SweepUnreferencedAsync([orphan]);

        Assert.Equal(1, swept);
        Assert.Null(await _objects.GetAsync(orphan));
        Assert.NotNull(await _objects.GetAsync(inFlight));   // untouched: not a candidate
    }

    /// <summary>
    /// Two activities with byte-identical content share ONE object — the dedupe the content axis exists for.
    /// Each still reads back as itself: content is addressed by hash and carries no track id, so the shared
    /// decode cannot hand activity B the identity of whichever activity decoded those bytes first.
    /// </summary>
    [Fact]
    public async Task IdenticalContentOnTwoTracks_SharesOneObject_AndNeitherInheritsTheOthersIdentity()
    {
        byte[] same = [1, 2, 3];
        await _content.AppendVersionAsync(TheTrack, same, pointCount: 3);
        await _content.AppendVersionAsync(OtherTrack, same, pointCount: 3);

        Assert.Equal(1, await ObjectCountAsync());
        Assert.Equal(await _content.GetHeadShaAsync(TheTrack), await _content.GetHeadShaAsync(OtherTrack));

        // Each track's head is its OWN version row, on its own seq — sharing bytes is not sharing identity.
        var mine  = await _content.GetVersionAsync((await _content.GetHeadVersionIdAsync(TheTrack))!.Value);
        var other = await _content.GetVersionAsync((await _content.GetHeadVersionIdAsync(OtherTrack))!.Value);
        Assert.Equal(TheTrack, mine!.ActivityId);
        Assert.Equal(OtherTrack, other!.ActivityId);
    }

    [Fact]
    public async Task VersionsOfCommit_ReturnsWhatThatEditProduced()
    {
        await _content.AppendVersionAsync(TheTrack, [1], pointCount: 1);                              // import: no commit
        var edited = await _content.AppendVersionAsync(TheTrack, [2], pointCount: 1, commitId: TestCommit);

        var versions = await _content.VersionsOfCommitAsync(TestCommit);

        Assert.Equal(edited, Assert.Single(versions).VersionId);
    }

    /// <summary>
    /// Purging a commit must not leave a version pointing at an id that no longer resolves — the dangling-id
    /// class this model exists to kill. The FK (ON DELETE SET NULL) makes it structural, not a caller's duty.
    /// </summary>
    [Fact]
    public async Task PurgingACommit_NullsTheVersionsItStamped_RatherThanDangling()
    {
        await _content.AppendVersionAsync(TheTrack, [1], pointCount: 1);
        var edited = await _content.AppendVersionAsync(TheTrack, [2], pointCount: 1, commitId: TestCommit);

        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM Track_Edit_Commit WHERE commit_id = {0};", TestCommit.Value);

        var version = await _content.GetVersionAsync(edited);
        Assert.NotNull(version);
        Assert.Null(version!.CommitId);                                   // not 42-pointing-at-nothing
        Assert.Empty(await _content.VersionsOfCommitAsync(TestCommit));     // the commit's effect is simply gone
    }

    /// <summary>A version can only be stamped with a commit that exists (the other half of the same FK).</summary>
    [Fact]
    public async Task StampingANonexistentCommit_IsRejected()
    {
        await Assert.ThrowsAnyAsync<Exception>(() =>
            _content.AppendVersionAsync(TheTrack, [1], pointCount: 1, commitId: new CommitId(9999)));
    }

    /// <summary>
    /// Re-writing the head's exact bytes with no commit behind it (a re-finalize: elevation refresh, backfill)
    /// changes nothing, so it appends nothing — otherwise a backfill sweep would stamp a no-op version onto
    /// every track in the DB and the revision list a user reads would be mostly noise.
    /// </summary>
    [Fact]
    public async Task UncommittedRewriteOfUnchangedContent_AppendsNoVersion()
    {
        var v1 = await _content.AppendVersionAsync(TheTrack, [1, 2, 3], pointCount: 3);

        var again = await _content.AppendVersionAsync(TheTrack, [1, 2, 3], pointCount: 3);

        Assert.Equal(v1, again);
        Assert.Single(await _content.ListVersionsAsync(TheTrack));
    }

    /// <summary>
    /// ...but a COMMITTED write always appends, even when the bytes are unchanged (a whole-track crop). The
    /// version row is the record that this commit touched this track — "what did commit C replace?" is a query
    /// over commit_id, and suppressing the row would make the commit claim it replaced nothing.
    /// </summary>
    [Fact]
    public async Task CommittedRewriteOfUnchangedContent_StillAppendsAVersion()
    {
        await _content.AppendVersionAsync(TheTrack, [1, 2, 3], pointCount: 3);

        var edited = await _content.AppendVersionAsync(TheTrack, [1, 2, 3], pointCount: 3, commitId: TestCommit);

        Assert.Equal(2, (await _content.ListVersionsAsync(TheTrack)).Count);
        Assert.Equal(edited, Assert.Single(await _content.VersionsOfCommitAsync(TestCommit)).VersionId);
        Assert.Equal(1, await ObjectCountAsync());   // ...and it cost zero bytes: same sha, stored once
    }

    /// <summary>
    /// SVC-08 integration check: many concurrent writers (each its own DbContext) appending to the SAME
    /// track never surface an exception and always leave a contiguous, non-colliding seq range. (SQLite's
    /// locking in this single-process/shared-cache setup serializes writers tightly enough that the exact
    /// read-then-insert race rarely lands here — <see cref="InsertVersionWithSeqRetry_RecoversFromTheExactRaceSVC08LeftUnhandled"/>
    /// forces that race directly. This test is the belt to that test's suspenders.)
    /// </summary>
    [Fact]
    public async Task ConcurrentAppends_ToTheSameTrack_AllSucceed_WithNoSeqLeftColliding()
    {
        const int Writers = 16;
        string connStr = _context.Database.GetDbConnection().ConnectionString;

        var tasks = Enumerable.Range(0, Writers).Select(async i =>
        {
            var options = new DbContextOptionsBuilder<RouteBaseDbContext>().UseSqlite(connStr).Options;
            await using var ctx = new RouteBaseDbContext(options);
            await ctx.Database.OpenConnectionAsync();
            try
            {
                var content = new TrackContentStore(ctx, new TrackObjectStore(ctx, TimeProvider.System), TimeProvider.System);
                return await content.AppendVersionAsync(TheTrack, [(byte)i], pointCount: 1);
            }
            finally
            {
                await ctx.Database.CloseConnectionAsync();
            }
        });

        var versionIds = await Task.WhenAll(tasks);

        Assert.Equal(Writers, versionIds.Distinct().Count());   // every writer landed its own version row
        var seqs = (await _content.ListVersionsAsync(TheTrack)).Select(v => v.Seq).OrderBy(s => s).ToList();
        Assert.Equal(Enumerable.Range(1, Writers), seqs);        // contiguous 1..N — no collision left unresolved
    }

    /// <summary>
    /// Pins the exact condition <see cref="TrackContentStore.InsertVersionWithSeqRetryAsync"/> exists to
    /// catch: a genuine two-writer race isn't reproducible on demand in a single process (see the stress
    /// test above), so this forces it directly — insert a REAL row at the seq a writer would compute
    /// (simulating "another writer already took this slot"), then hand that same stale seq straight to the
    /// low-level insert. Proves (a) SQLite really does raise UNIQUE(track_id, seq) for this exact shape and
    /// (b) <see cref="TrackContentStore.IsSeqUniqueViolation"/> recognizes it — the two facts the retry
    /// loop's <c>catch when</c> clause depends on. The loop itself (recompute MAX, retry) is then a
    /// six-line, inspectable wrapper around these two proven facts.
    /// </summary>
    [Fact]
    public async Task InsertVersionAsync_ThrowsAClassifiableUniqueViolation_ForAnAlreadyTakenSeq()
    {
        const int staleSeq = 1; // what NextSeqAsync would hand out on an empty track
        // object_sha carries a real FK to Track_Object — the row needs a genuine object behind it.
        string sha = await _objects.PutAsync([9, 9], codecVersion: 1, pointCount: 1);
        await _context.Database.ExecuteSqlRawAsync(
            "INSERT INTO Activity_Version (track_id, seq, object_sha, point_count, created_at) " +
            "VALUES ({0}, {1}, {2}, 1, '2026-07-17 00:00:00');", TheTrack.Value, staleSeq, sha);

        var ex = await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() =>
            _content.InsertVersionAsync(TheTrack, staleSeq, sha, commitId: null, pointCount: 1, stats: null, CancellationToken.None));

        Assert.True(TrackContentStore.IsSeqUniqueViolation(ex));
    }

    // ---- coverage the mutation baseline showed the suite above was missing ----

    /// <summary>
    /// The scalars a caller stamps onto a version (distance/ascent/duration) are written and read back exactly
    /// — a history view renders them without decoding the object, so a dropped stat (bound as DBNull, or read
    /// back as null) is a silently wrong number on every revision row.
    /// </summary>
    [Fact]
    public async Task AppendWithStats_RoundTripsDistanceAscentDuration()
    {
        var v = await _content.AppendVersionAsync(
            TheTrack, [1, 2, 3], pointCount: 3,
            stats: new TrackVersionStats(Distance: 12.5, Ascent: 34.0, DurationS: 56.5));

        var row = await _content.GetVersionAsync(v);
        Assert.NotNull(row);
        Assert.Equal(12.5, row!.Distance);
        Assert.Equal(34.0, row.Ascent);
        Assert.Equal(56.5, row.DurationS);
    }

    /// <summary>Asking for a track's head version id when it has no content returns null (not a throw from
    /// unwrapping a null scalar).</summary>
    [Fact]
    public async Task GetHeadVersionId_NoContent_ReturnsNull()
    {
        Assert.Null(await _content.GetHeadVersionIdAsync(TheTrack));
    }

    /// <summary>The batch id-list read returns ONLY the requested versions — an empty/missing WHERE would hand
    /// back every version in the table, which the history reader would then treat as this query's answer.</summary>
    [Fact]
    public async Task GetVersions_ReturnsOnlyTheRequestedIds()
    {
        var v1 = await _content.AppendVersionAsync(TheTrack, [1], pointCount: 1);
        await _content.AppendVersionAsync(TheTrack, [2], pointCount: 1);          // v2 — must NOT come back
        var v3 = await _content.AppendVersionAsync(OtherTrack, [3], pointCount: 1);

        var got = await _content.GetVersionsAsync([v1, v3]);

        Assert.Equal(new[] { v1, v3 }, got.Select(r => r.VersionId).OrderBy(x => x.Value));
    }

    /// <summary>
    /// RestoreVersion refuses an id that does not exist, and an id that belongs to ANOTHER track — restoring a
    /// stranger's version onto this track would fork its head onto content it never had. Both refusals name the
    /// offending id/track, so the messages are pinned too (an emptied message is a mutant that survives a bare
    /// "it threw").
    /// </summary>
    [Fact]
    public async Task RestoreVersion_NonexistentId_ThrowsNamingTheId()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _content.RestoreVersionAsync(TheTrack, new VersionId(99999)));
        Assert.Contains("99999", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RestoreVersion_VersionOfAnotherTrack_IsRejected()
    {
        var alien = await _content.AppendVersionAsync(OtherTrack, [1, 2, 3], pointCount: 3);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _content.RestoreVersionAsync(TheTrack, alien));
        Assert.Contains("belongs to activity", ex.Message, StringComparison.Ordinal);

        Assert.Null(await _content.GetHeadVersionIdAsync(TheTrack));   // and TheTrack was left untouched
    }

    /// <summary>
    /// SetHead to a version that is not this track's is refused AND leaves the existing head intact. The refusal
    /// is the point: the clear-then-set runs in a transaction precisely so the failed set rolls back the clear,
    /// rather than leaving the track headless (silently contentless — an outcome the partial unique index cannot
    /// catch, only the row count can).
    /// </summary>
    [Fact]
    public async Task SetHead_ToAVersionNotOfThisTrack_ThrowsAndKeepsTheExistingHead()
    {
        var head = await _content.AppendVersionAsync(TheTrack, [1, 2, 3], pointCount: 3);
        var alien = await _content.AppendVersionAsync(OtherTrack, [9], pointCount: 1);   // a real version, wrong track

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _content.SetHeadAsync(TheTrack, alien));
        Assert.Contains("headless", ex.Message, StringComparison.Ordinal);

        Assert.Equal(head, await _content.GetHeadVersionIdAsync(TheTrack));   // NOT cleared — the clear rolled back
    }

    /// <summary>
    /// A write that fails partway (a nonexistent commit id trips the FK on the version INSERT, AFTER the object
    /// has been PUT) rolls back as one unit — the orphan object does not survive. This is the atomicity the
    /// no-partial-writes model rests on: content that no version points at is exactly what the store must never
    /// leave behind.
    /// </summary>
    [Fact]
    public async Task FailedAppend_RollsBackTheObjectWrite_LeavingNoOrphan()
    {
        await Assert.ThrowsAnyAsync<Exception>(
            () => _content.AppendVersionAsync(TheTrack, [5, 5, 5], pointCount: 1, commitId: new CommitId(9999)));

        Assert.Equal(0, await ObjectCountAsync());   // the object PUT rolled back with the failed version insert
    }

    /// <summary>
    /// DeleteVersions is a request, not a warrant: it removes ONLY the requested non-head versions (a non-head
    /// version the caller did not ask about survives, and the head is untouchable), it drops those versions' own
    /// lineage edges, and the deletion is durable (it commits — a rolled-back delete would leave the rows).
    /// </summary>
    [Fact]
    public async Task DeleteVersions_RemovesOnlyRequestedNonHeadVersions_AndTheirEdges()
    {
        var v1 = await _content.AppendVersionAsync(TheTrack, [1], pointCount: 1);
        var v2 = await _content.AppendVersionAsync(TheTrack, [2], pointCount: 1);   // parent v1 — NOT requested
        var v3 = await _content.AppendVersionAsync(TheTrack, [3], pointCount: 1);   // parent v2 — requested
        var v4 = await _content.AppendVersionAsync(TheTrack, [4], pointCount: 1);   // parent v3 — head

        var shas = await _content.DeleteVersionsAsync(TheTrack, [v1, v3]);

        var remaining = (await _content.ListVersionsAsync(TheTrack)).Select(r => r.VersionId).ToList();
        Assert.Equal(new[] { v2, v4 }, remaining);      // v1,v3 gone; v2 (unasked) and v4 (head) kept
        Assert.Equal(2, shas.Count);                     // both deleted versions' shas returned as GC candidates
        Assert.Empty(await _content.ParentsOfAsync(v3)); // v3's own edge (v3→v2) was dropped
        Assert.Equal([v1], await _content.ParentsOfAsync(v2));   // v2 survived → its edge to v1 is left intact
    }

    /// <summary>DropParentEdges removes exactly the named edges of a version.</summary>
    [Fact]
    public async Task DropParentEdges_RemovesTheNamedEdge()
    {
        var v1 = await _content.AppendVersionAsync(TheTrack, [1], pointCount: 1);
        var v2 = await _content.AppendVersionAsync(TheTrack, [2], pointCount: 1);   // parent v1
        Assert.Equal([v1], await _content.ParentsOfAsync(v2));

        await _content.DropParentEdgesAsync(v2, [v1]);

        Assert.Empty(await _content.ParentsOfAsync(v2));
    }

    /// <summary>An empty parent list is a no-op — the guard exists because an unguarded <c>IN ()</c> is a SQL
    /// syntax error, and because nothing should be dropped when nothing was named.</summary>
    [Fact]
    public async Task DropParentEdges_EmptyList_IsANoOp()
    {
        var v1 = await _content.AppendVersionAsync(TheTrack, [1], pointCount: 1);
        var v2 = await _content.AppendVersionAsync(TheTrack, [2], pointCount: 1);   // parent v1

        await _content.DropParentEdgesAsync(v2, Array.Empty<VersionId>());   // must not throw

        Assert.Equal([v1], await _content.ParentsOfAsync(v2));   // and the edge is untouched
    }

    /// <summary>
    /// A constraint violation that is NOT the (track_id, seq) UNIQUE — here a duplicate Track_Object primary key,
    /// which is still SQLITE_CONSTRAINT (code 19) — is correctly NOT classified as the seq race the retry loop
    /// swallows. The classifier must match on BOTH the code AND the message; a mutant that keeps only one would
    /// retry (or give up on) the wrong exceptions.
    /// </summary>
    [Fact]
    public async Task IsSeqUniqueViolation_IsFalse_ForADifferentConstraint()
    {
        var sha = await _objects.PutAsync([1, 2], codecVersion: 1, pointCount: 1);   // row now exists for this sha

        var ex = await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() =>
            _context.Database.ExecuteSqlRawAsync(
                "INSERT INTO Track_Object (sha256, codec_version, point_count, byte_len, data, created_at) " +
                "VALUES ({0}, 1, 1, 2, {1}, '2026-07-17 00:00:00');",
                sha, new byte[] { 1, 2 }));

        Assert.Equal(19, ex.SqliteErrorCode);                         // it IS a constraint violation...
        Assert.False(TrackContentStore.IsSeqUniqueViolation(ex));      // ...but not the seq one
    }

    /// <summary>
    /// Every raw-ADO read and the append enlist any ambient EF transaction, so a save that appends versions
    /// inside its own transaction commits or rolls back as one unit. Microsoft.Data.Sqlite throws when a command
    /// runs on a connection with a pending transaction unless it points at it — so a method whose EnlistAmbientTx
    /// went missing throws here. This one test drives them all: append (head lookup, seq scalar, insert), then
    /// every reader, all inside a live transaction, then commits and confirms the writes persisted.
    /// </summary>
    [Fact]
    public async Task AppendAndReads_EnlistAnAmbientEfTransaction()
    {
        await using var tx = await _context.Database.BeginTransactionAsync();

        var v1 = await _content.AppendVersionAsync(TheTrack, [1, 2, 3], pointCount: 3);
        var v2 = await _content.AppendVersionAsync(TheTrack, [4, 5, 6], pointCount: 3, commitId: TestCommit);

        Assert.Equal(2, (await _content.ListVersionsAsync(TheTrack)).Count);                 // QueryAsync
        Assert.Equal(new TrackHistoryFacts(2, true), await _content.HistoryFactsAsync(TheTrack));
        Assert.NotNull(await _content.GetHeadShaAsync(TheTrack));                             // ScalarAsync
        Assert.Equal([v1], await _content.ParentsOfAsync(v2));                               // ParentsOfAsync
        var many = await _content.ParentsOfManyAsync([v1, v2]);                              // ParentsOfManyAsync
        Assert.Equal([v1], many[v2]);
        Assert.False(many.ContainsKey(v1));                                                  // v1 is an import (0 parents)

        await tx.CommitAsync();

        Assert.Equal(2, (await _content.ListVersionsAsync(TheTrack)).Count);   // the in-transaction writes persisted
    }

    private async Task SeedCommitAsync(int commitId) =>
        await _context.Database.ExecuteSqlRawAsync(
            "INSERT INTO Track_Edit_Commit (commit_id, user_id, committed_at, save_mode, script_json) " +
            "VALUES ({0}, 'u', '2026-07-12 00:00:00', 'replace', '{{}}');", commitId);

    private async Task<int> ObjectCountAsync() =>
        await _context.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM Track_Object").SingleAsync();
}
