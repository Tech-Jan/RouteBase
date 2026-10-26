using System.Text.Json;
using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Sharing;
using RouteBaseWeb.Services.Tracks;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Tracks;

/// <summary>
/// Wire precision for GPS channels.
///
/// The values on the wire used to be dequantized doubles run through unit conversions, which emit
/// IEEE-754 artifacts — <c>47.952659999999995</c>, <c>9.072000000000001</c> — so ~17 significant digits
/// were serialized where ~7 were meaningful. These pin that the artifacts are gone AND that the remaining
/// precision is still enough for what consumes it.
/// </summary>
public class GpsPrecisionTests
{
    [Fact]
    public void Coord_KeepsElevenCentimetreResolution()
    {
        // 1e-6 degrees is ~11 cm at the equator: finer than any map render, coarser than float noise.
        Assert.Equal(47.952660, GpsPrecision.Coord(47.952659999999995), precision: 9);
        Assert.NotEqual(GpsPrecision.Coord(47.952660), GpsPrecision.Coord(47.952671));
    }

    [Fact]
    public void Rounding_RemovesTheConversionArtifacts()
    {
        // The exact values the app produced: *3.6 (m/s -> km/h) and /1000 (m -> km).
        Assert.Equal("9.07", GpsPrecision.Spd(2.52 * 3.6).ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("476.4", GpsPrecision.Alt(476.40000000000003).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Distance is deliberately finer than the rest: the client interpolates DELTAS between consecutive
    /// points (replay-engine-base.ts, segment-stats-replay-efforts.ts, activity-segments.ts). At 1 s
    /// sampling and a slow 5 km/h the delta is ~1.4 m — still far above the 0.1 m quantum.
    /// </summary>
    [Fact]
    public void Distance_ResolvesConsecutivePointsEvenWhenCrawling()
    {
        const double metresPerSecondAt5Kmh = 5.0 / 3.6;
        var a = GpsPrecision.Dist(1000.0 / 1000.0);
        var b = GpsPrecision.Dist((1000.0 + metresPerSecondAt5Kmh) / 1000.0);

        Assert.True(b > a, "consecutive points must stay distinguishable after rounding");
    }

    [Fact]
    public void Sensor_ChannelsAreWholeNumbers()
    {
        Assert.Equal(142, GpsPrecision.Sensor(141.99999999999997));
        Assert.Equal(0, GpsPrecision.Sensor(0));
    }

    [Fact]
    public void Coord_NullablePassesNullThrough()
    {
        Assert.Null(GpsPrecision.Coord((double?)null));
        Assert.Equal(47.95266, GpsPrecision.Coord((double?)47.952659999999995));
    }
}

/// <summary>
/// The config island's wire contract. Mirrors TS <c>GpsTrackWire</c>; a break here is a silently blank
/// chart or map in the browser, which no other test would catch.
/// </summary>
public class GpsTrackWireDtoTests
{
    private static ActivityDetailView ViewWith(ActivityViewSurfaces surfaces, params GpsData[] points) =>
        new()
        {
            Activity = new Activity { TrackId = new TrackId(1), UserId = "u" },
            GpsData = points.ToList(),
            Surfaces = surfaces,
        };

    private static GpsData Point(int id, double hr = 0, double cadence = 0) => new()
    {
        GpsId = id, GpsLat = 47.952659999999995, GpsLong = 7.990132,
        Altitude = 476.40000000000003, Speed = 2.52, Distance = 1000,
        HeartRate = hr, Cadence = cadence,
    };

    /// <summary>
    /// Property names must reach the browser lowercase. The island is serialized by a bare
    /// JsonSerializer.Serialize with NO naming policy, so without [JsonPropertyName] a record would emit
    /// "Lat" and every consumer would read undefined — silently, at runtime only.
    /// </summary>
    [Fact]
    public void SerializesWithTheLowercaseWireNames()
    {
        var json = JsonSerializer.Serialize(
            ViewWith(ActivityViewSurfaces.ForDetailsPage(isOwner: true), Point(1)).GetGpsTrackForJavaScript());

        foreach (var key in new[] { "\"lat\"", "\"lng\"", "\"altitude\"", "\"speed\"", "\"distance\"", "\"t\"" })
            Assert.Contains(key, json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Lat\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void RoundsChannelsOnTheWayOut()
    {
        var json = JsonSerializer.Serialize(
            ViewWith(ActivityViewSurfaces.ForDetailsPage(isOwner: true), Point(1)).GetGpsTrackForJavaScript());

        Assert.DoesNotContain("47.952659999999995", json, StringComparison.Ordinal);
        Assert.DoesNotContain("476.40000000000003", json, StringComparison.Ordinal);
        Assert.Contains("47.95266", json, StringComparison.Ordinal);
    }

    /// <summary>gpsId's only consumer is the segment panel, which cannot open on a share view.</summary>
    [Fact]
    public void GpsId_PresentWhereSegmentsCanBeCreated_OmittedOnAShareView()
    {
        var withPanel = ViewWith(ActivityViewSurfaces.ForDetailsPage(isOwner: true), Point(1));
        var shareView = ViewWith(ActivityViewSurfaces.ReadOnlyShare, Point(1));

        Assert.NotNull(withPanel.GetGpsTrackForJavaScript().GpsId);
        Assert.Null(shareView.GetGpsTrackForJavaScript().GpsId);
        Assert.DoesNotContain("gpsId", JsonSerializer.Serialize(shareView.GetGpsTrackForJavaScript()),
            StringComparison.Ordinal);
    }

    [Fact]
    public void SensorChannels_OmittedWhenTheRideRecordedNone()
    {
        var noSensors = ViewWith(ActivityViewSurfaces.ReadOnlyShare, Point(1), Point(2));
        var track = noSensors.GetGpsTrackForJavaScript();

        Assert.Null(track.HeartRate);
        Assert.Null(track.Cadence);
        Assert.DoesNotContain("heartRate", JsonSerializer.Serialize(track), StringComparison.Ordinal);
    }

    [Fact]
    public void SensorChannels_KeptWhenEvenOnePointHasData()
    {
        var withHr = ViewWith(ActivityViewSurfaces.ReadOnlyShare, Point(1), Point(2, hr: 142));
        var track = withHr.GetGpsTrackForJavaScript();

        Assert.NotNull(track.HeartRate);
        Assert.Equal(new[] { 0d, 142d }, track.HeartRate!);
        Assert.Null(track.Cadence);   // cadence still absent — the two are judged independently
    }

    /// <summary>lat is the array every consumer takes its length from, so it is never omitted.</summary>
    [Fact]
    public void PositionChannels_AreAlwaysPresent()
    {
        var track = ViewWith(ActivityViewSurfaces.ReadOnlyShare, Point(1)).GetGpsTrackForJavaScript();

        Assert.Single(track.Lat);
        Assert.Single(track.Lng);
        Assert.Single(track.T);
    }
}

/// <summary>The segment map's track point contract — named DTO over the cached entity.</summary>
public class TrackPointDtoTests
{
    /// <summary>
    /// These endpoints hand back lists straight out of GpsCoordinateCache, shared between requests.
    /// Projecting (not mutating) is what keeps a later reader from getting pre-rounded values.
    /// </summary>
    [Fact]
    public void From_RoundsWithoutTouchingTheCachedEntity()
    {
        var cached = new GpsCoordinate { Lat = 47.952659999999995, Lng = 7.990132, GpsId = 42 };

        var dto = TrackPointDto.From(cached);

        Assert.Equal(47.95266, dto.Lat);
        Assert.Equal(42, dto.GpsId);
        Assert.Equal(47.952659999999995, cached.Lat);   // untouched
    }

    /// <summary>gpsId IS read here (segment-stats-map-data.ts matches effort entry/exit ids).</summary>
    [Fact]
    public void SerializesToTheShapeTheSegmentMapAlreadyConsumed()
    {
        var json = JsonSerializer.Serialize(
            TrackPointDto.From(new GpsCoordinate { Lat = 1, Lng = 2, GpsId = 3 }),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Contains("\"lat\"", json, StringComparison.Ordinal);
        Assert.Contains("\"lng\"", json, StringComparison.Ordinal);
        Assert.Contains("\"gpsId\"", json, StringComparison.Ordinal);
    }
}
