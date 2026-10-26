using Dynastream.Fit;
using RouteBaseWeb.Models.Gpx;
using RouteBaseWeb.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using DateTime = System.DateTime;

namespace RouteBaseWeb.Tests.Services;

/// <summary>
/// Step 5: <see cref="FitParsingService"/> against a FIT file synthesized in-process by the SDK encoder.
/// Verifies semicircle→degree conversion, FIT-epoch timestamps, sensor channels, and session totals.
/// </summary>
public class FitParsingServiceTests
{
    private const double SemicirclesPerDegree = 2147483648.0 / 180.0;

    // These fixtures are all single (non-chained) FIT files → one result; unwrap the list contract.
    private static GpxParseResult Parse(byte[] fit, string fileName = "ride.fit") => ParseAll(fit, fileName).Single();

    private static IReadOnlyList<GpxParseResult> ParseAll(byte[] fit, string fileName = "ride.fit")
    {
        using var stream = new MemoryStream(fit);
        return new FitParsingService(NullLogger<FitParsingService>.Instance).Parse(stream, fileName);
    }

    /// <summary>Encodes a minimal Activity FIT: FileId + N GPS RecordMesgs + one SessionMesg.</summary>
    private static byte[] EncodeRide(
        (double lat, double lng, float alt, byte hr, ushort power)[] pts,
        DateTime startUtc,
        Sport sport = Sport.Cycling,
        float totalDistance = 1234.5f,
        int? recordCount = null)
    {
        using var ms = new MemoryStream();
        var encode = new Encode(ProtocolVersion.V20);
        encode.Open(ms);

        var fileId = new FileIdMesg();
        fileId.SetType(Dynastream.Fit.File.Activity);
        fileId.SetManufacturer(Manufacturer.Garmin);
        fileId.SetTimeCreated(new Dynastream.Fit.DateTime(startUtc));
        encode.Write(fileId);

        int n = recordCount ?? pts.Length;
        for (int i = 0; i < n; i++)
        {
            var p = pts[i % pts.Length];
            var rec = new RecordMesg();
            rec.SetTimestamp(new Dynastream.Fit.DateTime(startUtc.AddSeconds(i)));
            rec.SetPositionLat((int)Math.Round(p.lat * SemicirclesPerDegree));
            rec.SetPositionLong((int)Math.Round(p.lng * SemicirclesPerDegree));
            rec.SetAltitude(p.alt);
            rec.SetHeartRate(p.hr);
            rec.SetPower(p.power);
            encode.Write(rec);
        }

        var session = new SessionMesg();
        session.SetStartTime(new Dynastream.Fit.DateTime(startUtc));
        session.SetSport(sport);
        session.SetTotalDistance(totalDistance);
        session.SetTotalTimerTime((float)(n - 1));
        session.SetTotalCalories(321);
        session.SetTotalAscent(50);
        encode.Write(session);

        encode.Close();
        return ms.ToArray();
    }

    [Fact]
    public void Parse_ValidRide_MapsPositionsSensorsAndSessionTotals()
    {
        var start = new DateTime(2026, 5, 1, 6, 30, 0, DateTimeKind.Utc);
        var pts = new (double, double, float, byte, ushort)[]
        {
            (47.10, 8.10, 500f, 120, 200),
            (47.11, 8.11, 505f, 130, 220),
            (47.12, 8.12, 503f, 140, 240),
        };
        var r = Parse(EncodeRide(pts, start));

        Assert.True(r.Success, r.ErrorMessage);
        var s = r.Summary!;
        Assert.Equal("fit", s.Source);
        Assert.Equal("Ride", s.ActivityType);       // Sport.Cycling
        Assert.Equal(3, r.GpsPoints.Count);

        // Semicircle round-trip lands within ~1e-4 deg.
        Assert.Equal(47.10, r.GpsPoints[0].Lat, 4);
        Assert.Equal(8.10, r.GpsPoints[0].Lng, 4);

        // FIT-epoch timestamp decoded to the correct UTC instant.
        Assert.Equal(start, r.GpsPoints[0].TimestampUtc!.Value.ToUniversalTime());

        // Sensor channels flowed through.
        Assert.Equal(120, r.GpsPoints[0].HeartRate!.Value, 0);
        Assert.Equal(240, r.GpsPoints[2].Power!.Value, 0);

        // Session totals preferred over computed.
        Assert.Equal(1234.5, s.Distance!.Value, 1);
        Assert.Equal(321, s.Calories!.Value, 0);
        Assert.Equal(50, s.ElevationGain!.Value, 0);
        Assert.Equal(140, s.MaxHeartRate!.Value, 0);
    }

    [Fact]
    public void Parse_SportRunning_MapsToRun()
    {
        var start = new DateTime(2026, 6, 1, 7, 0, 0, DateTimeKind.Utc);
        var pts = new (double, double, float, byte, ushort)[] { (40.0, -105.0, 1600f, 150, 0), (40.001, -105.001, 1602f, 155, 0) };
        var r = Parse(EncodeRide(pts, start, sport: Sport.Running));
        Assert.True(r.Success, r.ErrorMessage);
        Assert.Equal("Run", r.Summary!.ActivityType);
    }

    [Fact]
    public void Parse_MultisportSingleFile_StaysOneActivity()
    {
        // OBS-2: a multisport single file has ONE file header but several sessions (e.g. a triathlon).
        // It must stay ONE activity — never shattered per session — with first-session totals and NO
        // spurious "multiple sessions" warning (only truly-chained files, which split, would warn — and
        // they don't warn either because each piece is a clean single activity).
        var start = new DateTime(2026, 5, 1, 6, 30, 0, DateTimeKind.Utc);
        using var ms = new MemoryStream();
        var encode = new Encode(ProtocolVersion.V20);
        encode.Open(ms);

        var fileId = new FileIdMesg();
        fileId.SetType(Dynastream.Fit.File.Activity);
        fileId.SetManufacturer(Manufacturer.Garmin);
        fileId.SetTimeCreated(new Dynastream.Fit.DateTime(start));
        encode.Write(fileId);

        for (int i = 0; i < 4; i++)
        {
            var rec = new RecordMesg();
            rec.SetTimestamp(new Dynastream.Fit.DateTime(start.AddSeconds(i)));
            rec.SetPositionLat((int)Math.Round((47.10 + i * 1e-4) * SemicirclesPerDegree));
            rec.SetPositionLong((int)Math.Round(8.10 * SemicirclesPerDegree));
            encode.Write(rec);
        }

        foreach (var sport in new[] { Sport.Running, Sport.Cycling })
        {
            var session = new SessionMesg();
            session.SetStartTime(new Dynastream.Fit.DateTime(start));
            session.SetSport(sport);
            encode.Write(session);
        }
        encode.Close();

        var results = ParseAll(ms.ToArray());

        Assert.Single(results);                              // not shattered into two per-session activities
        var r = results[0];
        Assert.True(r.Success, r.ErrorMessage);
        Assert.Equal(4, r.GpsPoints.Count);                 // all points kept in the one activity
        Assert.Equal("Run", r.Summary!.ActivityType);       // first session (Running) supplies the sport
        Assert.DoesNotContain(r.Warnings, w => w.Contains("Multiple sessions", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_NotAFitFile_ReturnsFailure()
    {
        var r = Parse(new byte[] { 0x3C, 0x67, 0x70, 0x78 }, "fake.fit"); // "<gpx"
        Assert.False(r.Success);
    }

    [Fact]
    public void Parse_BadFileCrc_ReturnsFailure()
    {
        var start = new DateTime(2026, 5, 1, 6, 30, 0, DateTimeKind.Utc);
        var pts = new (double, double, float, byte, ushort)[]
        {
            (47.10, 8.10, 500f, 120, 200),
            (47.11, 8.11, 505f, 130, 220),
        };
        var bytes = EncodeRide(pts, start);
        bytes[^1] ^= 0xFF; // corrupt the trailing file CRC

        var r = Parse(bytes);
        Assert.False(r.Success);
        Assert.Contains("integrity", r.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_OutOfRangePosition_ExcludesPoint()
    {
        var start = new DateTime(2026, 5, 1, 6, 30, 0, DateTimeKind.Utc);
        var pts = new (double, double, float, byte, ushort)[]
        {
            (47.10, 8.10, 500f, 120, 200),
            (91.5, 8.11, 505f, 130, 220), // lat > 90 after semicircle conversion → excluded
        };
        var r = Parse(EncodeRide(pts, start));

        Assert.True(r.Success, r.ErrorMessage);
        Assert.Single(r.GpsPoints);
        Assert.Equal(47.10, r.GpsPoints[0].Lat, 4);
    }

    [Fact]
    public void Parse_GarbledMidFile_ReturnsFailure()
    {
        var start = new DateTime(2026, 5, 1, 6, 30, 0, DateTimeKind.Utc);
        var pts = new (double, double, float, byte, ushort)[]
        {
            (47.10, 8.10, 500f, 120, 200),
            (47.11, 8.11, 505f, 130, 220),
            (47.12, 8.12, 503f, 140, 240),
        };
        var bytes = EncodeRide(pts, start);
        bytes[(int)(bytes.Length * 0.6)] ^= 0xA5; // flip a byte mid-file

        var r = Parse(bytes);
        Assert.False(r.Success);
    }
}
