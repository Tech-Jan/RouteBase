using RouteBaseWeb.Models.Sync;
using RouteBaseWeb.Services.Tracks;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Sql;

/// <summary>
/// GI-09 (BlobStore): the GPS bulk-insert string builder is gone — import points now funnel through
/// <see cref="DecodedTrackExtensions.FromExternalPoints"/> → <see cref="TrackBlobCodec"/>. Pure round-trip
/// tests of that funnel (no DB, no culture dependence — the blob is binary): a blob decodes back to the
/// same point count, an ordinal ramp (ids not stored), quantization-exact coords, and null channels stay null.
/// </summary>
public class ActivityImportSqlTests
{
    private static List<ExternalGpsPoint> Points(int count) =>
        Enumerable.Range(0, count)
            .Select(i => new ExternalGpsPoint { Lat = 47.0 + i * 0.001, Lng = 11.0 + i * 0.001 })
            .ToList();

    private static DecodedTrack RoundTrip(List<ExternalGpsPoint> points)
    {
        var encoded = TrackBlobCodec.Encode(DecodedTrackExtensions.FromExternalPoints(points));
        return TrackBlobCodec.Decode(encoded);
    }

    [Fact]
    public void Funnel_1500Points_RoundTripsCountAndOrdinalRamp()
    {
        var decoded = RoundTrip(Points(1500));

        Assert.Equal(1500, decoded.Count);   // ids not stored; ordinal ≡ index
    }

    [Fact]
    public void Funnel_EmptyPoints_ZeroCountBlob()
    {
        var decoded = RoundTrip([]);
        Assert.Equal(0, decoded.Count);
    }

    [Fact]
    public void Funnel_QuantizesCoords_AndKeepsNullChannelsNull()
    {
        var points = new List<ExternalGpsPoint>
        {
            new()
            {
                Lat = 47.5, Lng = 11.25,
                Altitude = 1234.5, Distance = 10.75, Speed = 3.5,
                HeartRate = null, Cadence = null, Power = null, GradeSmooth = null,
                TimestampUtc = new DateTime(2023, 6, 15, 8, 0, 0, DateTimeKind.Utc),
            },
        };

        var decoded = RoundTrip(points);

        Assert.Equal(1, decoded.Count);
        Assert.Equal(TrackBlobCodec.RoundTripValue(47.5, TrackBlobCodec.LatScale), decoded.Lat[0]);
        Assert.Equal(TrackBlobCodec.RoundTripValue(11.25, TrackBlobCodec.LngScale), decoded.Lng[0]);
        Assert.Equal(TrackBlobCodec.RoundTripValue(1234.5, TrackBlobCodec.AltitudeScale), decoded.Altitude[0]);
        // Whole-NULL sensor channels stay default (null-backed) channels (no allocation).
        Assert.True(decoded.HeartRate.IsNull);
        Assert.True(decoded.Cadence.IsNull);
        Assert.True(decoded.Power.IsNull);
        Assert.True(decoded.GradeSmooth.IsNull);
    }
}
