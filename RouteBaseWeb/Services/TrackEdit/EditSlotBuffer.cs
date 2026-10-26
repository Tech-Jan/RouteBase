using RouteBaseWeb.Services.Tracks;

namespace RouteBaseWeb.Services.TrackEdit;

/// <summary>
/// Internal work representation of one live slot during a replay (A1 §3). Holds the 11 codec-order
/// channels as quantized <c>int[]</c> (null entry = whole-NULL channel), the time channel's QBias, the
/// point count and the slot's origin. Built from a <see cref="DecodedTrack"/> by REFERENCING its RawQ
/// arrays (no copy on load — cached decodes are shared and must never be mutated); every modifying
/// primitive allocates fresh arrays (copy-on-write at op granularity). The only per-point code paths live
/// here: slice / concat / present-run subtract — all <c>Array.Copy</c> + single passes, no per-point
/// objects, no LINQ.
/// </summary>
internal sealed class EditSlotBuffer
{
    // Codec order (TrackBlobCodec channel order) is THE order everywhere. Time and Distance are the only
    // channels with junction treatment (§2.2/§2.3); the rest are pure slices/concats.
    private const int TimeChannel = 0;
    private const int DistanceChannel = 4;
    private const int ChannelCount = TrackBlobCodec.ChannelCount;

    private readonly int[]?[] _channels;   // 11, codec order; null = whole-NULL
    // Per-channel: true once this buffer owns a private, mutable copy of the array. False = the array is
    // still the aliased cached decode (FromDecodedTrack) and must be copied before any in-place mutate.
    private readonly bool[] _channelOwned;
    private readonly long _timeQBias;
    private readonly int _count;

    private EditSlotBuffer(int[]?[] channels, long timeQBias, int count, SlotOrigin origin, bool ownsChannels)
    {
        _channels = channels;
        _timeQBias = timeQBias;
        _count = count;
        Origin = origin;
        _channelOwned = new bool[ChannelCount];
        if (ownsChannels) Array.Fill(_channelOwned, true);
    }

    public int Count => _count;
    public long TimeQBias => _timeQBias;
    public SlotOrigin Origin { get; }

    /// <summary>Reference the decoded track's RawQ arrays (no copy). In-place mutators copy-on-write
    /// (see <see cref="EnsureWritable"/>), so sharing cached decode arrays is safe even if a caller
    /// mutates a buffer built straight from a decode.</summary>
    public static EditSlotBuffer FromDecodedTrack(DecodedTrack t, SlotOrigin origin)
    {
        int[]?[] channels = new int[ChannelCount][];
        channels[0] = t.TimeEpochS.RawQ;
        channels[1] = t.Lat.RawQ;
        channels[2] = t.Lng.RawQ;
        channels[3] = t.Altitude.RawQ;
        channels[4] = t.Distance.RawQ;
        channels[5] = t.Speed.RawQ;
        channels[6] = t.HeartRate.RawQ;
        channels[7] = t.GradeSmooth.RawQ;
        channels[8] = t.Cadence.RawQ;
        channels[9] = t.Power.RawQ;
        channels[10] = t.Pressure.RawQ;
        return new EditSlotBuffer(channels, t.TimeEpochS.QBias, t.Count, origin, ownsChannels: false);
    }

    /// <summary>New buffer holding ordinals [from..to] inclusive of this one (fresh arrays).</summary>
    public EditSlotBuffer Slice(int from, int to, SlotOrigin origin)
    {
        int len = to - from + 1;
        int[]?[] channels = new int[ChannelCount][];
        for (int i = 0; i < ChannelCount; i++)
        {
            int[]? src = _channels[i];
            if (src is null) continue;
            var dst = new int[len];
            Array.Copy(src, from, dst, 0, len);
            channels[i] = dst;
        }
        return new EditSlotBuffer(channels, _timeQBias, len, origin, ownsChannels: true);
    }

    /// <summary>New buffer with ordinals [from..to] REMOVED (head + tail copied into fresh arrays).</summary>
    public EditSlotBuffer WithoutRange(int from, int to, SlotOrigin origin)
    {
        int tailLen = _count - to - 1;
        int newCount = from + tailLen;
        int[]?[] channels = new int[ChannelCount][];
        for (int i = 0; i < ChannelCount; i++)
        {
            int[]? src = _channels[i];
            if (src is null) continue;
            var dst = new int[newCount];
            Array.Copy(src, 0, dst, 0, from);            // head run
            Array.Copy(src, to + 1, dst, from, tailLen); // tail run
            channels[i] = dst;
        }
        return new EditSlotBuffer(channels, _timeQBias, newCount, origin, ownsChannels: true);
    }

    /// <summary>Concatenate <paramref name="a"/> then <paramref name="b"/>. Mixed channel presence fills the
    /// missing side's run with NullQ; both whole-NULL stays whole-NULL. Result carries a's (target's) QBias;
    /// b's time samples are copied verbatim and rebased/shifted by the caller.</summary>
    public static EditSlotBuffer Concat(EditSlotBuffer a, EditSlotBuffer b, SlotOrigin origin)
    {
        int total = a._count + b._count;
        int[]?[] channels = new int[ChannelCount][];
        for (int i = 0; i < ChannelCount; i++)
        {
            int[]? ca = a._channels[i];
            int[]? cb = b._channels[i];
            if (ca is null && cb is null) continue;
            var dst = new int[total];
            if (ca is null) Fill(dst, 0, a._count); else Array.Copy(ca, 0, dst, 0, a._count);
            if (cb is null) Fill(dst, a._count, b._count); else Array.Copy(cb, 0, dst, a._count, b._count);
            channels[i] = dst;
        }
        return new EditSlotBuffer(channels, a._timeQBias, total, origin, ownsChannels: true);
    }

    // ---- present-run subtracts (copy-on-write, then mutate the private copy) ----

    /// <summary>Subtract <paramref name="amount"/> seconds from every PRESENT time sample in [start..end);
    /// NullQ samples stay NullQ. Long math, folded back to int32. Returns false on int32 overflow.</summary>
    public bool ShiftTimePresent(int start, int end, long amount) =>
        SubtractPresent(EnsureWritable(TimeChannel), start, end, amount);

    /// <summary>Subtract <paramref name="delta"/> from every PRESENT distance sample in [start..end).
    /// Returns false on int32 overflow.</summary>
    public bool RebaseDistancePresent(int start, int end, long delta) =>
        SubtractPresent(EnsureWritable(DistanceChannel), start, end, delta);

    /// <summary>Copy-on-write guard: if <paramref name="channel"/> is still the aliased cached decode
    /// (see <see cref="FromDecodedTrack"/>), replace it with a private copy before the first in-place
    /// mutation — so a mutate-after-decode can never corrupt the shared sha-keyed cache, whatever the
    /// call site. A whole-NULL channel (null) needs no copy.</summary>
    private int[]? EnsureWritable(int channel)
    {
        int[]? arr = _channels[channel];
        if (arr is not null && !_channelOwned[channel])
        {
            arr = (int[])arr.Clone();
            _channels[channel] = arr;
            _channelOwned[channel] = true;
        }
        return arr;
    }

    private static bool SubtractPresent(int[]? arr, int start, int end, long amount)
    {
        if (arr is null || amount == 0) return true;
        for (int j = start; j < end; j++)
        {
            int q = arr[j];
            if (q == TrackChannel.NullQ) continue;
            long v = (long)q - amount;
            if (v <= int.MinValue || v > int.MaxValue) return false; // NullQ (int.MinValue) reserved
            arr[j] = (int)v;
        }
        return true;
    }

    // ---- present-sample lookups (anchors) ----

    /// <summary>Raw time offset at <paramref name="index"/> (channel value, not absolute). False if NullQ.</summary>
    public bool TryTimeQAt(int index, out int q)
    {
        int[]? t = _channels[TimeChannel];
        if (t is not null && t[index] != TrackChannel.NullQ) { q = t[index]; return true; }
        q = 0;
        return false;
    }

    /// <summary>First present ABSOLUTE time (offset + QBias) in [start..end). False if none present.</summary>
    public bool TryFirstPresentTimeAbs(int start, int end, out long abs)
    {
        int[]? t = _channels[TimeChannel];
        if (t is not null)
            for (int j = start; j < end; j++)
                if (t[j] != TrackChannel.NullQ) { abs = t[j] + _timeQBias; return true; }
        abs = 0;
        return false;
    }

    /// <summary>Last present ABSOLUTE time (offset + QBias) in [start..end). False if none present.</summary>
    public bool TryLastPresentTimeAbs(int start, int end, out long abs)
    {
        int[]? t = _channels[TimeChannel];
        if (t is not null)
            for (int j = end - 1; j >= start; j--)
                if (t[j] != TrackChannel.NullQ) { abs = t[j] + _timeQBias; return true; }
        abs = 0;
        return false;
    }

    /// <summary>First present distance offset in [start..end). False if none present (or whole-NULL).</summary>
    public bool TryFirstPresentDistance(int start, int end, out long q)
    {
        int[]? d = _channels[DistanceChannel];
        if (d is not null)
            for (int j = start; j < end; j++)
                if (d[j] != TrackChannel.NullQ) { q = d[j]; return true; }
        q = 0;
        return false;
    }

    /// <summary>Last present distance offset in [start..end). False if none present (or whole-NULL).</summary>
    public bool TryLastPresentDistance(int start, int end, out long q)
    {
        int[]? d = _channels[DistanceChannel];
        if (d is not null)
            for (int j = end - 1; j >= start; j--)
                if (d[j] != TrackChannel.NullQ) { q = d[j]; return true; }
        q = 0;
        return false;
    }

    /// <summary>Materialize into a <see cref="DecodedTrack"/> via the internal TrackChannel ctor (same
    /// assembly). Pure content — the slot's identity stays on <see cref="Origin"/>, which is the only thing
    /// that knows whether this slot maps to an existing activity or to one the save is about to create.</summary>
    public DecodedTrack ToDecodedTrack()
    {
        var s = TrackBlobCodec.Scales;
        return new DecodedTrack
        {
            Count = _count,
            TimeEpochS = new TrackChannel(_channels[0], s[0], _timeQBias),
            Lat = new TrackChannel(_channels[1], s[1]),
            Lng = new TrackChannel(_channels[2], s[2]),
            Altitude = new TrackChannel(_channels[3], s[3]),
            Distance = new TrackChannel(_channels[4], s[4]),
            Speed = new TrackChannel(_channels[5], s[5]),
            HeartRate = new TrackChannel(_channels[6], s[6]),
            GradeSmooth = new TrackChannel(_channels[7], s[7]),
            Cadence = new TrackChannel(_channels[8], s[8]),
            Power = new TrackChannel(_channels[9], s[9]),
            Pressure = new TrackChannel(_channels[10], s[10]),
        };
    }

    private static void Fill(int[] dst, int start, int len)
    {
        for (int j = 0; j < len; j++) dst[start + j] = TrackChannel.NullQ;
    }
}
