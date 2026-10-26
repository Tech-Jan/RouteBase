namespace RouteBaseWeb.Services.Tracks;

/// <summary>
/// Strict LRU cache of decoded tracks, bounded by an exact byte budget. Hand-rolled (not
/// <c>MemoryCache</c>) because MemoryCache's SizeLimit *rejects* new entries when full instead of
/// evicting — under analyze-all's sequential track scan that would freeze the cache at first fill.
/// Strict evict-oldest is the right policy for this access pattern.
///
/// Keyed by CONTENT (the object sha), not by track id. Content is immutable — a hash can never point at
/// different bytes — so an entry can never go stale and there is nothing to invalidate: editing a track
/// appends a new object under a new hash and moves the head, which is simply a different key. This is why
/// the old TE-05 stamp guard (a monotonic counter + per-track invalidate stamps, to stop a decode that began
/// before a blob-rewrite from caching stale points) is gone rather than ported: the race it defended against
/// cannot occur when nothing is ever rewritten.
///
/// Pure logic, no I/O — unit-testable without mocks. All operations are O(1) under one lock; the decode they
/// gate dominates, so contention is negligible.
/// </summary>
public sealed class TrackReaderCache
{
    private readonly record struct Entry(string Sha, DecodedTrack Track, long Size);

    private readonly long _limitBytes;
    private readonly object _lock = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> _index = new(StringComparer.Ordinal);
    private readonly LinkedList<Entry> _lru = new();   // head = most-recently used, tail = LRU
    private long _currentBytes;

    public TrackReaderCache(long limitBytes) => _limitBytes = limitBytes;

    /// <summary>Total bytes of all cached tracks — exact, for accounting assertions.</summary>
    public long CurrentBytes { get { lock (_lock) return _currentBytes; } }

    /// <summary>Cached decode of the object <paramref name="sha"/>, moved to most-recently-used. Null on miss.</summary>
    public DecodedTrack? TryGet(string sha)
    {
        lock (_lock)
        {
            if (!_index.TryGetValue(sha, out var node)) return null;
            _lru.Remove(node);
            _lru.AddFirst(node);
            return node.Value.Track;
        }
    }

    /// <summary>Store a decode, evicting LRU tails until it fits. An entry larger than the whole
    /// budget is not cached (the caller still holds the instance).</summary>
    public void Set(string sha, DecodedTrack track)
    {
        long size = SizeOf(track);
        lock (_lock)
        {
            if (_index.TryGetValue(sha, out var existing))
            {
                _currentBytes -= existing.Value.Size;
                _lru.Remove(existing);
                _index.Remove(sha);
            }

            if (size > _limitBytes) return;   // never cacheable; still usable by the caller

            while (_currentBytes + size > _limitBytes && _lru.Last != null)
                EvictTail();

            var node = _lru.AddFirst(new Entry(sha, track, size));
            _index[sha] = node;
            _currentBytes += size;
        }
    }

    // No public Evict: nothing ever needs one. An edit writes a different hash (the old key just stops being
    // asked for), and a swept object's decode ages out via LRU pressure — it can never serve wrong data,
    // because a hash cannot point at different bytes.

    private void EvictTail()
    {
        if (_lru.Last is not { } tail) return;   // over-budget loop never calls this empty; guard beats !
        _currentBytes -= tail.Value.Size;
        _lru.RemoveLast();
        _index.Remove(tail.Value.Sha);
    }

    /// <summary>Byte size = Σ allocated channel arrays (int[] len×4) + 128 fixed overhead.
    /// Whole-NULL channels are null-backed and cost nothing. Exact and deterministic.</summary>
    public static long SizeOf(DecodedTrack t)
    {
        long size = 128;
        int n = t.Count;
        size += (long)n * 4 * 3;             // Lat, Lng, TimeEpochS (required int[])
        foreach (var ch in new[] { t.Altitude, t.Distance, t.Speed, t.HeartRate, t.GradeSmooth, t.Cadence, t.Power, t.Pressure })
            if (!ch.IsNull) size += (long)n * 4;
        return size;
    }
}
