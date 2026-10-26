using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Sync;
using RouteBaseWeb.Services.Tracks;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Tracks;

/// <summary>
/// Unit tests for <see cref="DecodedTrackExtensions"/> — the single DecodedTrack ⇄ point-object conversion
/// funnel used by the import/finalize write paths and the ActivityGpsService read path. Pure (no DB):
/// NaN ⇔ null, epoch ⇔ UTC DateTime, whole-null channel ⇒ default channel. GPS ids are the ordinal ramp.
/// </summary>
public class DecodedTrackExtensionsTests
{
    private static readonly DateTime T0 = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ToGpsDataList_MapsChannels_OrdinalIds_NaNToNull()
    {
        var track = DecodedTrackExtensions.FromDoubleChannels(
            new[] { (T0 - DateTime.UnixEpoch).TotalSeconds, double.NaN, (T0.AddSeconds(20) - DateTime.UnixEpoch).TotalSeconds },
            new[] { 1.0, double.NaN, 1.2 },
            new[] { 2.0, 2.1, 2.2 },
            new[] { 10.0, 11.0, double.NaN },   // altitude
            null, null, null,                   // distance, speed, heartRate (whole-null)
            null, null, null);                  // gradeSmooth, cadence, power

        var list = track.ToGpsDataList(trackId: new TrackId(7));

        Assert.Equal(3, list.Count);
        Assert.Equal(new[] { 0, 1, 2 }, list.Select(g => g.GpsId));
        Assert.All(list, g => Assert.Equal(7, g.TrackId));
        Assert.Null(list[1].GpsLat);                 // NaN → null
        Assert.Equal(1.2, list[2].GpsLat);
        Assert.Equal(T0, list[0].DateTimeUtc);       // epoch → UTC DateTime
        Assert.Null(list[1].DateTimeUtc);            // NaN time → null
        Assert.Null(list[2].Altitude);               // NaN channel value → null
        Assert.Null(list[0].HeartRate);              // NaN → null
        Assert.Null(list[0].Cadence);                // absent (null array) channel → null
    }

    [Fact]
    public void FromGpsDataList_RoundTrips_ThroughToGpsDataList()
    {
        var points = new List<GpsData>
        {
            new() { GpsLat = 47.001, GpsLong = 11.0, DateTimeUtc = T0,               Altitude = 100, Speed = 2.5 },
            new() { GpsLat = 47.002, GpsLong = 11.0, DateTimeUtc = T0.AddSeconds(10), Altitude = 110, Speed = 3.0 },
        };

        var track = DecodedTrackExtensions.FromGpsDataList(points);
        var back = track.ToGpsDataList(trackId: new TrackId(5));

        Assert.All(back, g => Assert.Equal(5, g.TrackId));   // the id is stamped here, not carried by the decode
        Assert.Equal(2, back.Count);
        for (int i = 0; i < 2; i++)
        {
            // Builder quantizes at build, so outputs equal the quantize→dequantize oracle.
            Assert.Equal(TrackBlobCodec.RoundTripValue(points[i].GpsLat!.Value, TrackBlobCodec.LatScale), back[i].GpsLat);
            Assert.Equal(points[i].DateTimeUtc, back[i].DateTimeUtc);
            Assert.Equal(TrackBlobCodec.RoundTripValue(points[i].Altitude!.Value, TrackBlobCodec.AltitudeScale), back[i].Altitude);
            Assert.Equal(TrackBlobCodec.RoundTripValue(points[i].Speed!.Value, TrackBlobCodec.SpeedScale), back[i].Speed);
        }
        Assert.True(track.Power.IsNull);              // whole-null channel → default channel
        Assert.True(track.Cadence.IsNull);
    }

    [Fact]
    public void FromExternalPoints_MapsTimestamps_AndNullChannels()
    {
        var points = new List<ExternalGpsPoint>
        {
            new() { Lat = 47.0, Lng = 11.0, TimestampUtc = T0, Altitude = 100 },
            new() { Lat = 47.1, Lng = 11.1, TimestampUtc = T0.AddSeconds(30), Altitude = null },
        };

        var track = DecodedTrackExtensions.FromExternalPoints(points);

        Assert.Equal(2, track.Count);
        Assert.Equal(TrackBlobCodec.RoundTripValue(47.0, TrackBlobCodec.LatScale), track.Lat[0]);
        Assert.Equal((T0.AddSeconds(30) - DateTime.UnixEpoch).TotalSeconds, track.TimeEpochS[1]);
        Assert.False(track.Altitude.IsNull);
        Assert.Equal(TrackBlobCodec.RoundTripValue(100, TrackBlobCodec.AltitudeScale), track.Altitude[0]);
        Assert.True(double.IsNaN(track.Altitude[1]));  // null → NaN inside the (present) channel
        Assert.True(track.HeartRate.IsNull);           // whole-null sensor channel → default channel
    }

    [Fact]
    public void FromExternalPoints_EmptyList_ZeroCount()
    {
        var track = DecodedTrackExtensions.FromExternalPoints(new List<ExternalGpsPoint>());
        Assert.Equal(0, track.Count);
    }

    [Fact]
    public void FromExternalPoints_NullTimestamp_MapsToNaNTime()
    {
        var track = DecodedTrackExtensions.FromExternalPoints(new List<ExternalGpsPoint>
        {
            new() { Lat = 47.0, Lng = 11.0, TimestampUtc = null },
        });
        Assert.True(double.IsNaN(track.TimeEpochS[0]));
        Assert.Null(track.ToGpsDataList(trackId: new TrackId(1))[0].DateTimeUtc);
    }

    // ==== mutation-testing additions ====

    // L113-L118: each optional sensor channel in FromExternalPoints is `p.X ?? double.NaN`. The `?? NaN`
    // removal makes the value ALWAYS NaN → a whole-null channel. Every prior test left these unset (null →
    // NaN either way), so a present value on each is what distinguishes them. Pins all six at once.
    [Fact]
    public void FromExternalPoints_CarriesEveryOptionalSensorChannel()
    {
        var track = DecodedTrackExtensions.FromExternalPoints(new List<ExternalGpsPoint>
        {
            new()
            {
                Lat = 47.0, Lng = 11.0, TimestampUtc = T0,
                Speed = 2.5, HeartRate = 120, GradeSmooth = 1.5, Cadence = 85, Power = 200, Pressure = 1013.25,
            },
        });

        Assert.False(track.Speed.IsNull);
        Assert.Equal(TrackBlobCodec.RoundTripValue(2.5, TrackBlobCodec.SpeedScale), track.Speed[0]);
        Assert.False(track.HeartRate.IsNull);
        Assert.Equal(TrackBlobCodec.RoundTripValue(120, TrackBlobCodec.HeartRateScale), track.HeartRate[0]);
        Assert.False(track.GradeSmooth.IsNull);
        Assert.Equal(TrackBlobCodec.RoundTripValue(1.5, TrackBlobCodec.GradeSmoothScale), track.GradeSmooth[0]);
        Assert.False(track.Cadence.IsNull);
        Assert.Equal(TrackBlobCodec.RoundTripValue(85, TrackBlobCodec.CadenceScale), track.Cadence[0]);
        Assert.False(track.Power.IsNull);
        Assert.Equal(TrackBlobCodec.RoundTripValue(200, TrackBlobCodec.PowerScale), track.Power[0]);
        Assert.False(track.Pressure.IsNull);
        Assert.Equal(TrackBlobCodec.RoundTripValue(1013.25, TrackBlobCodec.PressureScale), track.Pressure[0]);
    }

    // L210 Logical (`||`→`&&`) + Statement + L216 String: a value whose quantization exceeds int.MaxValue must
    // throw on build, naming the int32 range. The mutants either never throw (silent truncation) or drop the
    // message. Heart-rate scale is 1.0, so the value IS the quantized magnitude.
    [Fact]
    public void Build_ValueAboveInt32Max_ThrowsNamingRange()
    {
        var ex = Assert.Throws<InvalidDataException>(() => OneSensorTrack(heartRate: 3_000_000_000.0));
        Assert.Contains("int32", ex.Message, StringComparison.Ordinal);
    }

    // L210 Equality (`x <= int.MinValue` → `x < int.MinValue`): a value quantizing to exactly int.MinValue is
    // the reserved NullQ sentinel — build MUST reject it rather than store it as a NULL.
    [Fact]
    public void Build_ValueEqualsInt32Min_Throws()
        => Assert.Throws<InvalidDataException>(() => OneSensorTrack(heartRate: int.MinValue));

    // L210 Equality (`x > int.MaxValue` → `x >= int.MaxValue`): exactly int.MaxValue is a LEGAL quantized
    // value — build must keep it, not throw. Pins the inclusive upper edge.
    [Fact]
    public void Build_ValueEqualsInt32Max_Kept()
    {
        var track = OneSensorTrack(heartRate: int.MaxValue);
        Assert.Equal(int.MaxValue * TrackBlobCodec.HeartRateScale, track.HeartRate[0]);
    }

    private static DecodedTrack OneSensorTrack(double heartRate) =>
        DecodedTrackExtensions.FromDoubleChannels(
            time: new[] { 0.0 }, lat: new[] { 0.0 }, lng: new[] { 0.0 },
            altitude: null, distance: null, speed: null, heartRate: new[] { heartRate },
            gradeSmooth: null, cadence: null, power: null, pressure: null);
}
