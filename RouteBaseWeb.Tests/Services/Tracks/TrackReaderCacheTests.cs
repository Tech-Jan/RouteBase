using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Tracks;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Tracks;

/// <summary>
/// LRU + byte-accounting semantics of the decode cache. Keys are object shas, not track ids: content is
/// immutable, so a key can never point at different bytes.
///
/// The old TE-05 stamp-guard tests (a decode that began before a blob rewrite must not overwrite the cache)
/// are GONE, not ported: they defended against a race that required content to be mutable. An edit now writes
/// a new object under a new hash and moves the head, so a cached decode is never stale and there is nothing to
/// invalidate. <see cref="TrackReaderCache.Evict"/> exists only for objects reclaimed by a permanent purge.
/// </summary>
public class TrackReaderCacheTests
{
    // A track of `n` points; `withOptional` adds one optional (Altitude) channel.
    private static DecodedTrack Track(TrackId trackId, int n, bool withOptional = false) =>
        DecodedTrackExtensions.FromDoubleChannels(
            new double[n], new double[n], new double[n],
            withOptional ? new double[n] : null, null, null, null, null, null, null);

    // Distinct, deterministic stand-ins for content hashes.
    private static string Sha(int n) => $"{n:x64}";

    [Fact]
    public void SizeOf_CountsOnlyAllocatedChannels()
    {
        // Required only: 128 + n*4*3 (lat/lng/time int[]).
        Assert.Equal(128 + 10 * 4 * 3, TrackReaderCache.SizeOf(Track(new TrackId(1), 10)));
        // One optional channel adds n*4.
        Assert.Equal(128 + 10 * 4 * 3 + 10 * 4, TrackReaderCache.SizeOf(Track(new TrackId(1), 10, withOptional: true)));
        // Pressure (channel 10, 4d859a9) is charged like every other optional channel. TE-04.
        var withPressure = DecodedTrackExtensions.FromDoubleChannels(
            new double[10], new double[10], new double[10],
            null, null, null, null, null, null, null, pressure: new double[10]);
        Assert.Equal(128 + 10 * 4 * 3 + 10 * 4, TrackReaderCache.SizeOf(withPressure));
    }

    [Fact]
    public void Set_SameKeyTwice_AccountingExact()
    {
        long small = TrackReaderCache.SizeOf(Track(new TrackId(0), 10));
        long big   = TrackReaderCache.SizeOf(Track(new TrackId(0), 20));
        var cache = new TrackReaderCache(big * 2);

        cache.Set(Sha(1), Track(new TrackId(1), 10));
        cache.Set(Sha(1), Track(new TrackId(1), 20));              // same key re-set — old size must be released
        Assert.Equal(big, cache.CurrentBytes);
        Assert.NotEqual(small, big);
    }

    /// <summary>An edit is a NEW key, so both decodes coexist — that is the whole point: the pre-edit decode
    /// is still valid content, not a stale entry to be hunted down.</summary>
    [Fact]
    public void EditedTrack_IsADifferentKey_BothDecodesCoexist()
    {
        var cache = new TrackReaderCache(1024 * 1024);

        cache.Set(Sha(1), Track(new TrackId(1), 10));   // pre-edit content
        cache.Set(Sha(2), Track(new TrackId(1), 5));    // post-edit content of the same track

        Assert.Equal(10, cache.TryGet(Sha(1))!.Count);
        Assert.Equal(5, cache.TryGet(Sha(2))!.Count);
    }

    [Fact]
    public void Set_OverLimit_EvictsLeastRecentlyUsed()
    {
        long size = TrackReaderCache.SizeOf(Track(new TrackId(0), 10));
        var cache = new TrackReaderCache(size * 2);

        cache.Set(Sha(1), Track(new TrackId(1), 10));
        cache.Set(Sha(2), Track(new TrackId(2), 10));
        cache.Set(Sha(3), Track(new TrackId(3), 10));              // evicts the LRU tail (sha 1)

        Assert.Null(cache.TryGet(Sha(1)));
        Assert.NotNull(cache.TryGet(Sha(2)));
        Assert.NotNull(cache.TryGet(Sha(3)));
        Assert.Equal(size * 2, cache.CurrentBytes);
    }

    [Fact]
    public void TryGet_MovesEntryToFront()
    {
        long size = TrackReaderCache.SizeOf(Track(new TrackId(0), 10));
        var cache = new TrackReaderCache(size * 2);

        cache.Set(Sha(1), Track(new TrackId(1), 10));
        cache.Set(Sha(2), Track(new TrackId(2), 10));
        cache.TryGet(Sha(1));                        // 1 becomes most-recently-used
        cache.Set(Sha(3), Track(new TrackId(3), 10));             // …so 2 is the tail that goes

        Assert.NotNull(cache.TryGet(Sha(1)));
        Assert.Null(cache.TryGet(Sha(2)));
        Assert.NotNull(cache.TryGet(Sha(3)));
    }

    [Fact]
    public void Set_EntryLargerThanLimit_NotCachedButUsable()
    {
        var cache = new TrackReaderCache(64);         // smaller than any track's fixed overhead

        cache.Set(Sha(1), Track(new TrackId(1), 10));

        Assert.Null(cache.TryGet(Sha(1)));
        Assert.Equal(0, cache.CurrentBytes);
    }

}
