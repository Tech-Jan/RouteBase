using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Lifecycle;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Services.Segments;
using RouteBaseWeb.Services.Sharing;
using RouteBaseWeb.Services.Tracks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Sharing;

/// <summary>
/// The owner-only contract. A failure here is a privacy leak, not a cosmetic regression: the whole reason
/// segment sharing was scoped out originally was that identity-widening cannot express "widen the segment,
/// not the efforts", and these tests are what say it is expressed correctly now.
/// </summary>
public class ShareSegmentReaderTests : IAsyncLifetime
{
    private const string Owner = "owner-user";
    private const string Friend = "friend-user";
    private const string Stranger = "stranger-user";
    private const int SegId = 7;

    private RouteBaseDbContext _db = null!;
    private IMemoryCache _cache = null!;
    private ShareSegmentReader _reader = null!;

    private static ShareGrant Grant(string owner = Owner, int segmentId = SegId) =>
        new(TokenId: 1, ShareKind.Segment, segmentId, owner, DateTime.UtcNow.AddHours(1));

    public async Task InitializeAsync()
    {
        var conn = $"Data Source=file:sharesegread_{Guid.NewGuid():N}?mode=memory&cache=shared";
        var options = new DbContextOptionsBuilder<RouteBaseDbContext>().UseSqlite(conn).Options;
        _db = new RouteBaseDbContext(options);
        await _db.Database.OpenConnectionAsync();
        await _db.Database.EnsureCreatedAsync();

        foreach (var (id, name) in new[] { (Owner, "Owner"), (Friend, "Friendly Friend"), (Stranger, "A Stranger") })
            _db.Users.Add(new AppUser { UserId = id, DisplayName = name, Email = $"{id}@t.com", IsAdmin = false });

        _db.Segments.Add(new Segment { SegmentId = new SegmentId(SegId), UserId = Owner, SegmentName = "Test climb" });

        // 1 = owner released, 2 = owner trashed, 3 = friend's, 4 = public stranger's, 5 = owner excluded.
        _db.Activities.Add(Act(1, Owner, LifecycleStates.Released, "private"));
        _db.Activities.Add(Act(2, Owner, "trashed", "private"));
        _db.Activities.Add(Act(3, Friend, LifecycleStates.Released, "friends"));
        _db.Activities.Add(Act(4, Stranger, LifecycleStates.Released, "public"));
        _db.Activities.Add(Act(5, Owner, LifecycleStates.Released, "private"));
        await _db.SaveChangesAsync();

        foreach (var trackId in new[] { 1, 2, 3, 4, 5 }) _db.GpsInSegments.Add(Effort(new TrackId(trackId)));
        await _db.SaveChangesAsync();

        // Effort on track 5 is durably excluded by the owner.
        var excluded = await _db.GpsInSegments.FirstAsync(e => e.TrackId == new TrackId(5));
        _db.SegmentEffortExclusions.Add(new SegmentEffortExclusion
        {
            SegmentId = new SegmentId(SegId),
            TrackId = new TrackId(5),
            EntryIndex = excluded.GpsIdEntryCalculated ?? excluded.GpsIdEntryGuessed ?? 0,
        });
        await _db.SaveChangesAsync();

        _cache = new MemoryCache(new MemoryCacheOptions());
        var invalidator = new UserCacheInvalidator();
        var segCache = // The shared TestVisibilityService: SegmentCache only consults it for key-tagging, and the
        // reader itself never gates through it — that is the point of the isolation test below.
        new SegmentCache(_cache, new TestVisibilityService(Owner), invalidator);
        _reader = new ShareSegmentReader(_db, _cache, segCache, new ThrowingPerfBuilder(), new NullTrackReader());
    }

    private static Activity Act(int id, string user, string lifecycle, string visibility) => new()
    {
        TrackId = new TrackId(id),
        UserId = user,
        TrackName = $"ride-{id}",
        Lifecycle = lifecycle,
        Visibility = visibility,
    };

    /// <summary>An effort with full stored aggregates, so the projection keeps it.</summary>
    private static GpsInSegment Effort(TrackId trackId) => new()
    {
        SegmentId = new SegmentId(SegId),
        TrackId = trackId,
        GpsIdEntryCalculated = 1,
        GpsIdExitCalculated = 50,
        SegPerc = 99,
        EffortDistance = 2500,
        EffortElevation = 100,
        EffortTime = 700 + trackId.Value,
        EffortSpeedAvg = 3.5,
    };

    public async Task DisposeAsync()
    {
        _cache.Dispose();
        await _db.DisposeAsync();
    }

    // ---- the headline requirement ----------------------------------------------------------------

    [Fact]
    public async Task OwnerEfforts_OmitsAFriendsEffort()
    {
        // THE test. Widening identity would run VisibleActivities as the owner, whose friends clause would
        // hand every friend's effort to an anonymous stranger — worse than leaking the owner's own data.
        var efforts = await _reader.GetOwnerEffortsAsync(Grant());

        Assert.DoesNotContain(efforts, e => e.ActivityId == 3);
        Assert.NotEmpty(efforts); // anti-vacuity: an always-empty reader would pass the line above
    }

    [Fact]
    public async Task OwnerEfforts_OmitsAPublicStrangersEffort()
    {
        // Pins "owner's efforts only" — not even anonymized public ones, which was the earlier design.
        var efforts = await _reader.GetOwnerEffortsAsync(Grant());

        Assert.DoesNotContain(efforts, e => e.ActivityId == 4);
    }

    [Fact]
    public async Task OwnerEfforts_OmitsTheOwnersOwnNonReleasedActivity()
    {
        // Lifecycle is restated explicitly in OwnerEfforts because we no longer inherit it from
        // VisibleActivities — this is the clause most likely to be dropped by a future edit.
        var efforts = await _reader.GetOwnerEffortsAsync(Grant());

        Assert.DoesNotContain(efforts, e => e.ActivityId == 2);
    }

    [Fact]
    public async Task OwnerEfforts_OmitsExcludedEfforts()
    {
        // Anti-joined out rather than marked: a share view has no Restore action, so a marked row is dead UI.
        var efforts = await _reader.GetOwnerEffortsAsync(Grant());

        Assert.DoesNotContain(efforts, e => e.ActivityId == 5);
    }

    [Fact]
    public async Task OwnerEfforts_KeepsExactlyTheOwnersReleasedNonExcludedEfforts()
    {
        var efforts = await _reader.GetOwnerEffortsAsync(Grant());

        Assert.Equal([1], efforts.Select(e => e.ActivityId).OrderBy(id => id).ToArray());
    }

    [Fact]
    public async Task OwnerEfforts_CarryNoRiderIdentity()
    {
        // The AS-05 shape. Stripped explicitly rather than left to fall out of the projection: "it happens
        // to be the owner anyway" is not a property a future refactor preserves.
        var effort = Assert.Single(await _reader.GetOwnerEffortsAsync(Grant()));

        Assert.Null(effort.UserId);
        Assert.Null(effort.UserDisplayName);
        Assert.False(effort.IsOwnEffort);
    }

    // ---- grant scoping ----------------------------------------------------------------------------

    [Fact]
    public async Task GetSegment_ReturnsNull_WhenTheGrantOwnerNoLongerOwnsTheSegment()
    {
        // MintAsync guarantees owner == segment.UserId at mint, but nothing re-checks it afterwards, and
        // Segment.UserId is nullable. A transferred segment must fall out of every live link.
        var seg = await _db.Segments.FirstAsync(s => s.SegmentId == new SegmentId(SegId));
        seg.UserId = Stranger;
        await _db.SaveChangesAsync();

        Assert.Null(await _reader.GetSegmentAsync(Grant()));
        Assert.Empty(await _reader.GetOwnerEffortsAsync(Grant()));
    }

    [Fact]
    public async Task ATransferredSegment_FallsOutOfALiveLink_EVENWithAWarmCache()
    {
        // The gate must run BEFORE the cache lookup, not after. Read once to warm
        // CacheKeys.ShareSegment{Efforts,Totals}, then transfer the segment away: a cache consulted first
        // would keep answering with the pre-transfer leaderboard for up to SegmentCache.MaxLifetime,
        // making the read-time ownership re-assertion GrantedSegment documents a no-op on the hot path.
        Assert.NotEmpty(await _reader.GetOwnerEffortsAsync(Grant()));
        Assert.NotNull(await _reader.GetOwnerEffortTotalsAsync(Grant()));

        var seg = await _db.Segments.FirstAsync(s => s.SegmentId == new SegmentId(SegId));
        seg.UserId = Stranger;
        await _db.SaveChangesAsync();

        Assert.Empty(await _reader.GetOwnerEffortsAsync(Grant()));
        Assert.Null(await _reader.GetOwnerEffortTotalsAsync(Grant()));
    }

    [Fact]
    public async Task OwnerEfforts_EmptyResult_IsCachedLikeANonEmptyOne()
    {
        // The zero-effort case must not be a cache hole: without a Set on the empty path, every open of a
        // zero-effort share re-runs the whole permitted-set query while non-empty results get the TTL.
        // Observed the same way the warm-cache test above observes caching: mutate the DB behind the cache
        // and expect the cached answer (real effort writes evict via InvalidateSegment / the owner token).
        _db.GpsInSegments.RemoveRange(_db.GpsInSegments.Where(e => e.TrackId == new TrackId(1)));
        await _db.SaveChangesAsync();

        Assert.Empty(await _reader.GetOwnerEffortsAsync(Grant()));

        _db.GpsInSegments.Add(Effort(new TrackId(1)));
        await _db.SaveChangesAsync();

        Assert.Empty(await _reader.GetOwnerEffortsAsync(Grant()));
    }

    [Fact]
    public async Task AnActivityKindGrant_ReadsNothing()
    {
        // Belt and braces behind the controller's own kind check.
        var activityGrant = new ShareGrant(1, ShareKind.Activity, SegId, Owner, DateTime.UtcNow.AddHours(1));

        Assert.Null(await _reader.GetSegmentAsync(activityGrant));
        Assert.Empty(await _reader.GetOwnerEffortsAsync(activityGrant));
        Assert.Null(await _reader.GetOwnerEffortTotalsAsync(activityGrant));
    }

    // ---- totals ------------------------------------------------------------------------------------

    [Fact]
    public async Task Totals_CoverExactlyTheEffortsTheListReturns()
    {
        // One query drives both, so the totals box and the row list cannot disagree — and no aggregate
        // about any other rider is computed at any point.
        var efforts = await _reader.GetOwnerEffortsAsync(Grant());
        var totals = await _reader.GetOwnerEffortTotalsAsync(Grant());

        Assert.NotNull(totals);
        Assert.Equal(efforts.Count, totals!.EffortCount);
        Assert.Equal(efforts.Sum(e => e.Distance), totals.TotalDistance);
        Assert.Equal(efforts.Sum(e => e.ElapsedTime), totals.TotalTime);
    }

    [Fact]
    public async Task Totals_AreNull_WhenAnAggregateIsStillPending()
    {
        // The "-" rule, inherited from the shared projection rather than restated here.
        var row = await _db.GpsInSegments.FirstAsync(e => e.TrackId == new TrackId(1));
        row.EffortDistance = null;
        await _db.SaveChangesAsync();

        Assert.Null(await _reader.GetOwnerEffortTotalsAsync(Grant()));
    }

    // ---- the allow-list ----------------------------------------------------------------------------

    [Fact]
    public async Task Tracks_ReturnOnlyPermittedTrackIds()
    {
        // Derived server-side from the grant: no caller-supplied ids reach this method at all.
        var page = await _reader.GetTracksAsync(Grant(), skip: 0, take: 150, maxPointsPerTrack: 0);

        Assert.Equal(1, page.Total);
        // NullTrackReader decodes nothing, so no points come back — the id set is the assertion here.
        Assert.All(page.Tracks, t => Assert.Equal(1, t.TrackId));
    }

    [Fact]
    public async Task Tracks_PagingCannotExceedTheMaxTracksCeiling()
    {
        // MaxTracks is documented as a HARD ceiling on what one segment's share view will ever serve.
        // Enforced only on the reported Total, skip-paging (?skip=200, ?skip=350…) would walk the whole
        // permitted set and the ceiling would bind only well-behaved clients.
        await SeedPermittedTracksAsync(100, ShareSegmentReader.MaxTracks + 10);
        var recorder = new RecordingTrackReader();
        var reader = ReaderWith(recorder);

        var page = await reader.GetTracksAsync(
            Grant(), skip: ShareSegmentReader.MaxTracks - 50, take: 150, maxPointsPerTrack: 0);
        Assert.Equal(ShareSegmentReader.MaxTracks, page.Total);
        Assert.Equal(50, recorder.RequestedIds.Count);

        recorder.RequestedIds.Clear();
        var beyond = await reader.GetTracksAsync(
            Grant(), skip: ShareSegmentReader.MaxTracks, take: 150, maxPointsPerTrack: 0);
        Assert.Empty(beyond.Tracks);
        Assert.Empty(recorder.RequestedIds);
        Assert.Equal(ShareSegmentReader.MaxTracks, beyond.Total);
    }

    [Fact]
    public async Task Tracks_PageInFirstEffortTimeOrder_StableAcrossRequests()
    {
        // OrderBy(...).Select(TrackId).Distinct() erases the ordering (the order columns are not in the
        // DISTINCT projection), so Skip/Take paged over an UNDEFINED order and slices could duplicate or
        // miss tracks across requests. Pins: order = first (min) effort time, ties by id, same for every
        // page size.
        await SeedPermittedTracksAsync(101, 1, _ => 900);
        await SeedPermittedTracksAsync(102, 1, _ => 800);
        await SeedPermittedTracksAsync(103, 1, _ => 850);
        await SeedPermittedTracksAsync(104, 1, _ => 1000);
        // Track 104 gets a SECOND, earlier effort — the min defines its position, not the first row.
        var second = Effort(new TrackId(104));
        second.EffortTime = 600;
        _db.GpsInSegments.Add(second);
        await _db.SaveChangesAsync();

        var recorder = new RecordingTrackReader();
        var reader = ReaderWith(recorder);
        // Base fixture's permitted track 1 has EffortTime 701.
        var expected = new[] { 104, 1, 102, 103, 101 };

        for (var skip = 0; skip < expected.Length; skip++)
            await reader.GetTracksAsync(Grant(), skip, take: 1, maxPointsPerTrack: 0);
        Assert.Equal(expected, recorder.RequestedIds);

        recorder.RequestedIds.Clear();
        await reader.GetTracksAsync(Grant(), skip: 0, take: 5, maxPointsPerTrack: 0);
        Assert.Equal(expected, recorder.RequestedIds);
    }

    [Fact]
    public async Task Track_ForAFriendsTrack_IsNull()
    {
        // The single-id path intersects against the SAME PermittedEfforts definition, so it cannot drift
        // from the list endpoint. Null → the controller 404s, indistinguishably from an unknown id.
        Assert.Null(await _reader.GetTrackAsync(Grant(), trackId: new TrackId(3), maxPoints: 0));
        Assert.Null(await _reader.GetTrackAsync(Grant(), trackId: new TrackId(999_999), maxPoints: 0));
    }

    [Fact]
    public async Task Track_ThatDoesNotDecode_IsNull_NotAnEmptyList()
    {
        // NULL and EMPTY mean different things to the controller: null → 404, empty → a 200 carrying no
        // points. Track 1 IS permitted, and NullTrackReader decodes nothing, so this is the permitted-but-
        // undecodable case. Collapsing the two would silently turn a 404 into an empty 200 — the kind of
        // contract drift a decode refactor makes easy and no other test here would notice.
        Assert.Null(await _reader.GetTrackAsync(Grant(), trackId: new TrackId(1), maxPoints: 0));
    }

    [Fact]
    public async Task Performance_DropsForeignEffortIdsSilently()
    {
        // Asking for a friend's effort returns nothing for it — no error, no 404, no acknowledgement that
        // the id exists. ThrowingPerfBuilder means reaching the builder at all with a foreign row fails.
        var friendEffort = await _db.GpsInSegments.FirstAsync(e => e.TrackId == new TrackId(3));

        var result = await _reader.GetPerformanceAsync(
            Grant(), new HashSet<GpsInSegmentId> { friendEffort.GpsInSegmentId }, 0);

        Assert.Empty(result);
    }

    // ---- paging helpers ----------------------------------------------------------------------------

    /// <summary>A reader over the same DB/cache but with a track reader that records which ids were
    /// requested — the paging tests assert on the id WINDOW, not on decoded points.</summary>
    private ShareSegmentReader ReaderWith(ITrackReader tracks) => new(
        _db, _cache,
        new SegmentCache(_cache, new TestVisibilityService(Owner), new UserCacheInvalidator()),
        new ThrowingPerfBuilder(), tracks);

    private async Task SeedPermittedTracksAsync(int firstTrackId, int count, Func<int, double>? effortTime = null)
    {
        for (var i = 0; i < count; i++)
        {
            var id = firstTrackId + i;
            _db.Activities.Add(Act(id, Owner, LifecycleStates.Released, "private"));
            var effort = Effort(new TrackId(id));
            if (effortTime != null) effort.EffortTime = effortTime(id);
            _db.GpsInSegments.Add(effort);
        }
        await _db.SaveChangesAsync();
    }

    // ---- fakes -------------------------------------------------------------------------------------

    /// <summary>Throws if the reader ever hands it rows — every test here asserts on gating, so reaching
    /// the GPS→wire math means an unauthorized row got through.</summary>
    private sealed class ThrowingPerfBuilder : ISegmentEffortPerformanceBuilder
    {
        public Task<List<EffortPerformanceData>> BuildAsync(
            SegmentId segmentId, IReadOnlyList<(GpsInSegment Row, Activity Activity)> authorized,
            int maxPointsPerEffort, CancellationToken ct = default)
            => authorized.Count == 0
                ? Task.FromResult(new List<EffortPerformanceData>())
                : throw new InvalidOperationException(
                    $"perf builder reached with {authorized.Count} row(s): " +
                    string.Join(",", authorized.Select(a => a.Row.TrackId)));
    }

    private sealed class NullTrackReader : ITrackReader
    {
        public Task<DecodedTrack?> GetTrackAsync(TrackId trackId, CancellationToken ct = default)
            => Task.FromResult<DecodedTrack?>(null);
    }

    private sealed class RecordingTrackReader : ITrackReader
    {
        public readonly List<int> RequestedIds = new();

        public Task<DecodedTrack?> GetTrackAsync(TrackId trackId, CancellationToken ct = default)
        {
            RequestedIds.Add(trackId.Value);
            return Task.FromResult<DecodedTrack?>(null);
        }
    }
}
