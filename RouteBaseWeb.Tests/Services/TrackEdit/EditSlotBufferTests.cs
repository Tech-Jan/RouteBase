using RouteBaseWeb.Services.Tracks;
using RouteBaseWeb.Services.TrackEdit;
using Xunit;

namespace RouteBaseWeb.Tests.Services.TrackEdit;

/// <summary>
/// Copy-on-write guard for <see cref="EditSlotBuffer"/> (dm-3). A buffer built via
/// <see cref="EditSlotBuffer.FromDecodedTrack"/> aliases the sha-keyed cached decode's RawQ arrays
/// (no copy on load). The in-place mutators must copy the backing array on first write, so a
/// mutate-after-decode can never corrupt the shared cache — regardless of call site.
/// </summary>
public class EditSlotBufferTests
{
    private static readonly SlotOrigin Origin = new(1, null);

    // scale is irrelevant here — the assertions read raw quantized values, not dequantized ones.
    private static DecodedTrack MakeTrack(int count, int[] time, int[]? distance = null) =>
        new()
        {
            Count = count,
            TimeEpochS = new TrackChannel(time, 1.0),
            Lat = default,
            Lng = default,
            Distance = distance is null ? default : new TrackChannel(distance, 1.0),
        };

    [Fact]
    public void ShiftTimePresent_DoesNotMutateCachedDecodeArray()
    {
        var timeQ = new[] { 10, 20, TrackChannel.NullQ, 40 };
        var cached = (int[])timeQ.Clone(); // what the shared cache must still hold afterwards

        var buffer = EditSlotBuffer.FromDecodedTrack(MakeTrack(4, timeQ), Origin);

        Assert.True(buffer.ShiftTimePresent(0, 4, amount: 5));

        // The aliased cache array is byte-for-byte unchanged (copy-on-write kicked in)...
        Assert.Equal(cached, timeQ);
        // ...NullQ stayed NullQ, and the buffer's private copy carries the shift (10 - 5 = 5).
        Assert.True(buffer.TryTimeQAt(0, out int q));
        Assert.Equal(5, q);
        Assert.False(buffer.TryTimeQAt(2, out _)); // still NullQ
    }

    [Fact]
    public void RebaseDistancePresent_DoesNotMutateCachedDecodeArray()
    {
        var distQ = new[] { 100, 200, 300 };
        var cached = (int[])distQ.Clone();

        var buffer = EditSlotBuffer.FromDecodedTrack(MakeTrack(3, new[] { 0, 1, 2 }, distQ), Origin);

        Assert.True(buffer.RebaseDistancePresent(0, 3, delta: 50));

        Assert.Equal(cached, distQ);
        Assert.True(buffer.TryFirstPresentDistance(0, 3, out long d));
        Assert.Equal(50, d); // 100 - 50
    }

    [Fact]
    public void RepeatedShift_CopiesOnce_AndKeepsCacheClean()
    {
        var timeQ = new[] { 10, 20, 30 };
        var cached = (int[])timeQ.Clone();

        var buffer = EditSlotBuffer.FromDecodedTrack(MakeTrack(3, timeQ), Origin);

        // Second write reuses the private copy from the first (no re-clone), still isolating the cache.
        Assert.True(buffer.ShiftTimePresent(0, 3, amount: 5));
        Assert.True(buffer.ShiftTimePresent(0, 3, amount: 2));

        Assert.Equal(cached, timeQ);            // cache untouched by either write
        Assert.True(buffer.TryTimeQAt(0, out int q));
        Assert.Equal(3, q);                     // 10 - 5 - 2
    }
}
