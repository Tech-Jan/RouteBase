using RouteBaseWeb.Services.Tracks;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Tracks;

public class DecodedTrackTests
{
    // Ordinal ≡ GPS_ID post re-key, so an n-point track spans ids 0..n-1 (the ramp).
    private static DecodedTrack With(int n) =>
        DecodedTrackExtensions.FromDoubleChannels(
            new double[n], new double[n], new double[n], null, null, null, null, null, null, null);

    [Fact]
    public void OrdinalRange_InclusiveBounds_MatchesSqlBetween()
    {
        var t = With(3);   // ids 0,1,2

        Assert.Equal((1, 1), t.OrdinalRange(1, 1));
        Assert.Equal((0, 2), t.OrdinalRange(0, 2));

        var above = t.OrdinalRange(3, 9);   // whole window past the track → empty (lo > hi)
        Assert.True(above.lo > above.hi);

        var below = t.OrdinalRange(-5, -1);   // whole window before the track → empty
        Assert.True(below.lo > below.hi);
    }

    [Fact]
    public void OrdinalRange_OutOfRangeBounds_Clamped()
    {
        var t = With(3);   // ids 0,1,2

        Assert.Equal((0, 2), t.OrdinalRange(-3, 7));                    // straddles the whole track
        Assert.Equal((2, 2), t.OrdinalRange(2, int.MaxValue));         // exit clamps to Count-1
        Assert.Equal((0, 1), t.OrdinalRange(int.MinValue, 1));         // entry clamps to 0
    }
}
