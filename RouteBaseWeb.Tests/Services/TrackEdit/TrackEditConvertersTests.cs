using RouteBaseWeb.Services.TrackEdit;
using RouteBaseWeb.Services.Tracks;
using Xunit;

namespace RouteBaseWeb.Tests.Services.TrackEdit;

/// <summary>Pure converters (A2 §4.5): the DecodedTrack → import-point round trip is bit-exact through the
/// codec, and sensor aggregates cover only present samples (whole-NULL channel → nulls).</summary>
public class TrackEditConvertersTests
{
    [Fact]
    public void ToExternalPoints_RoundTrip_BitExact()
    {
        var time = new[] { 1735718400.0, 1735718401.0, 1735718402.0, 1735718403.0 };
        var lat = new[] { 47.100000, double.NaN, 47.300000, 47.400000 };   // NaN required sample
        var lng = new[] { 8.500000, 8.500000, 8.500000, 8.500000 };
        var altitude = new[] { 100.0, 110.0, double.NaN, 130.0 };          // NaN optional sample
        var original = DecodedTrackExtensions.FromDoubleChannels(
            time, lat, lng, altitude, null, null, null, null, null, null, null); // power/etc whole-NULL

        var roundTripped = DecodedTrackExtensions.FromExternalPoints(TrackEditConverters.ToExternalPoints(original));

        Assert.Equal(TrackBlobCodec.Encode(original), TrackBlobCodec.Encode(roundTripped));
    }

    [Fact]
    public void ComputeSensorStats_PresentSamplesOnly_NullOnAbsent()
    {
        var time = new[] { 1735718400.0, 1735718401.0, 1735718402.0 };
        var lat = new[] { 47.0, 47.0, 47.0 };
        var lng = new[] { 8.0, 8.0, 8.0 };
        var heartRate = new[] { 120.0, double.NaN, 130.0 };   // present channel, one NullQ sample
        var track = DecodedTrackExtensions.FromDoubleChannels(
            time, lat, lng, null, null, null, heartRate, null, null, null, null); // power/cadence absent

        var stats = TrackEditConverters.ComputeSensorStats(track);

        Assert.Equal(125.0, stats.HrAvg);
        Assert.Equal(130.0, stats.HrMax);
        Assert.Null(stats.PowAvg);
        Assert.Null(stats.PowMax);
        Assert.Null(stats.CadAvg);
        Assert.Null(stats.CadMax);
    }
}
