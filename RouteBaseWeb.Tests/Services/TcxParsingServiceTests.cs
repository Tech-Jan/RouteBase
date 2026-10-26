using System.Text;
using RouteBaseWeb.Models.Gpx;
using RouteBaseWeb.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace RouteBaseWeb.Tests.Services;

/// <summary>
/// Step 4: <see cref="TcxParsingService"/> — single/multi-lap, with/without HR+power. Laps concatenate
/// into one activity; lap totals override computed values; source/id scheme matches GPX (GI-07 dedup).
/// </summary>
public class TcxParsingServiceTests
{
    // TCX always yields a single result (single-element list contract) — unwrap for the assertions.
    private static GpxParseResult ParseFixture(string name, bool useFilenameAsId = false)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", "Tcx", name);
        using var stream = File.OpenRead(path);
        return new TcxParsingService(NullLogger<TcxParsingService>.Instance).Parse(stream, name, useFilenameAsId).Single();
    }

    private static GpxParseResult ParseXml(string xml, string fileName = "inline.tcx", bool useFilenameAsId = false)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        return new TcxParsingService(NullLogger<TcxParsingService>.Instance)
            .Parse(stream, fileName, useFilenameAsId).Single();
    }

    private static string Tcx(string trackpoints) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
        "<TrainingCenterDatabase xmlns=\"http://www.garmin.com/xmlschemas/TrainingCenterDatabase/v2\">" +
        "<Activities><Activity Sport=\"Running\"><Id>2026-02-02T09:00:00Z</Id>" +
        "<Lap StartTime=\"2026-02-02T09:00:00Z\"><Track>" + trackpoints +
        "</Track></Lap></Activity></Activities></TrainingCenterDatabase>";

    [Fact]
    public void Parse_SingleLap_HrPowerCadence_Mapped()
    {
        var r = ParseFixture("single-lap.tcx");
        Assert.True(r.Success, r.ErrorMessage);
        var s = r.Summary!;

        Assert.Equal("tcx", s.Source);
        Assert.Equal("tcx", s.ProviderId);
        Assert.Equal("Ride", s.ActivityType);          // Sport="Biking" → Ride
        Assert.Equal(3, r.GpsPoints.Count);
        Assert.Equal(140.0, s.Distance!.Value, 3);      // lap DistanceMeters preferred over haversine
        Assert.Equal(12, s.Calories!.Value, 3);
        Assert.Equal(6.0, s.MaxSpeed!.Value, 3);        // lap MaximumSpeed
        Assert.Equal(140, s.MaxHeartRate!.Value, 0);
        Assert.Equal(240, s.MaxPower!.Value, 0);
        Assert.Equal(new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc), s.StartDateUtc!.Value.ToUniversalTime());

        // Per-point sensor channels flowed through.
        Assert.Equal(120, r.GpsPoints[0].HeartRate!.Value, 0);
        Assert.Equal(200, r.GpsPoints[0].Power!.Value, 0);
        Assert.Equal(80, r.GpsPoints[0].Cadence!.Value, 0);
    }

    [Fact]
    public void Parse_MultiLap_ConcatenatesPoints_AndSumsLapTotals()
    {
        var r = ParseFixture("multi-lap.tcx");
        Assert.True(r.Success, r.ErrorMessage);
        var s = r.Summary!;

        Assert.Equal("Run", s.ActivityType);            // Sport="Running"
        Assert.Equal(4, r.GpsPoints.Count);             // 2 + 2 across two laps
        Assert.Equal(220.0, s.Distance!.Value, 3);      // 100 + 120 lap DistanceMeters
        Assert.Equal(25, s.Calories!.Value, 3);         // 10 + 15
        Assert.Equal(7.0, s.MaxSpeed!.Value, 3);        // max(5, 7)
        Assert.Equal(65, s.ElapsedTime!.Value, 3);      // FP-07: wall-clock span 09:00:00→09:01:05
        Assert.Equal(45, s.TotalTime!.Value, 3);        // 20 + 25 lap TotalTimeSeconds (timer time)
        Assert.Equal(175, s.MaxHeartRate!.Value, 0);
    }

    [Fact]
    public void Parse_NoSensors_StillSucceeds_WithNullChannels()
    {
        var r = ParseFixture("no-sensors.tcx");
        Assert.True(r.Success, r.ErrorMessage);
        var s = r.Summary!;

        Assert.Equal("Walk", s.ActivityType);
        Assert.Equal(2, r.GpsPoints.Count);
        Assert.Null(s.MaxHeartRate);
        Assert.Null(s.MaxPower);
        Assert.Null(s.AverageCadence);
    }

    [Fact]
    public void Parse_Invalid_ReturnsFailure()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("<TrainingCenterDatabase></TrainingCenterDatabase>"));
        var r = new TcxParsingService(NullLogger<TcxParsingService>.Instance).Parse(stream, "empty.tcx").Single();
        Assert.False(r.Success);
    }

    [Fact]
    public void Parse_NonFiniteAndOutOfRangeCoords_ExcludedAndStatsFinite()
    {
        var r = ParseXml(Tcx(
            // good position but non-finite altitude
            "<Trackpoint><Time>2026-02-02T09:00:00Z</Time><Position>" +
            "<LatitudeDegrees>47.20</LatitudeDegrees><LongitudeDegrees>8.20</LongitudeDegrees></Position>" +
            "<AltitudeMeters>1e309</AltitudeMeters></Trackpoint>" +
            // NaN latitude → excluded
            "<Trackpoint><Time>2026-02-02T09:00:10Z</Time><Position>" +
            "<LatitudeDegrees>NaN</LatitudeDegrees><LongitudeDegrees>8.21</LongitudeDegrees></Position></Trackpoint>" +
            // out-of-range latitude → excluded
            "<Trackpoint><Time>2026-02-02T09:00:15Z</Time><Position>" +
            "<LatitudeDegrees>91.5</LatitudeDegrees><LongitudeDegrees>8.21</LongitudeDegrees></Position></Trackpoint>" +
            // good position
            "<Trackpoint><Time>2026-02-02T09:00:20Z</Time><Position>" +
            "<LatitudeDegrees>47.21</LatitudeDegrees><LongitudeDegrees>8.21</LongitudeDegrees></Position>" +
            "<AltitudeMeters>404</AltitudeMeters></Trackpoint>"));

        Assert.True(r.Success, r.ErrorMessage);
        Assert.Equal(2, r.GpsPoints.Count);              // NaN + out-of-range points dropped
        Assert.Null(r.GpsPoints[0].Altitude);            // 1e309 → null
        Assert.True(double.IsFinite(r.Summary!.Distance!.Value));
    }

    [Fact]
    public void Parse_ZonelessTimestamps_AssumedUtc_HostIndependent()
    {
        var r = ParseXml(Tcx(
            "<Trackpoint><Time>2026-02-02T09:00:00</Time><Position>" +
            "<LatitudeDegrees>47.20</LatitudeDegrees><LongitudeDegrees>8.20</LongitudeDegrees></Position></Trackpoint>" +
            "<Trackpoint><Time>2026-02-02T09:00:20</Time><Position>" +
            "<LatitudeDegrees>47.21</LatitudeDegrees><LongitudeDegrees>8.21</LongitudeDegrees></Position></Trackpoint>"));

        Assert.True(r.Success, r.ErrorMessage);
        // Zone-less wall time interpreted as UTC, independent of the server timezone.
        Assert.Equal(new DateTime(2026, 2, 2, 9, 0, 0, DateTimeKind.Utc), r.Summary!.StartDateUtc);
    }

    [Fact]
    public void Parse_LongSportAttribute_CappedAndSanitized()
    {
        // Attacker-controlled Sport passthrough must be length-capped and control-char-free.
        var longSport = "trail_" + new string('x', 200);
        var r = ParseXml(
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
            "<TrainingCenterDatabase xmlns=\"http://www.garmin.com/xmlschemas/TrainingCenterDatabase/v2\">" +
            $"<Activities><Activity Sport=\"{longSport}\"><Id>2026-02-02T09:00:00Z</Id>" +
            "<Lap StartTime=\"2026-02-02T09:00:00Z\"><Track>" +
            "<Trackpoint><Time>2026-02-02T09:00:00Z</Time><Position>" +
            "<LatitudeDegrees>47.20</LatitudeDegrees><LongitudeDegrees>8.20</LongitudeDegrees></Position></Trackpoint>" +
            "<Trackpoint><Time>2026-02-02T09:00:20Z</Time><Position>" +
            "<LatitudeDegrees>47.21</LatitudeDegrees><LongitudeDegrees>8.21</LongitudeDegrees></Position></Trackpoint>" +
            "</Track></Lap></Activity></Activities></TrainingCenterDatabase>");

        Assert.True(r.Success, r.ErrorMessage);
        Assert.True(r.Summary!.ActivityType!.Length <= 50);
        Assert.All(r.Summary!.ActivityType!, c => Assert.True(c >= 0x20));
    }

    [Fact]
    public void Parse_DoctypeEntity_IsRejected()
    {
        var xml = "<?xml version=\"1.0\"?>" +
            "<!DOCTYPE TrainingCenterDatabase [<!ENTITY e \"boom\"><!ENTITY % p SYSTEM \"file:///etc/passwd\">]>" +
            "<TrainingCenterDatabase xmlns=\"http://www.garmin.com/xmlschemas/TrainingCenterDatabase/v2\">" +
            "<Activities><Activity Sport=\"Running\"><Id>&e;</Id></Activity></Activities></TrainingCenterDatabase>";
        var r = ParseXml(xml);

        Assert.False(r.Success);
        Assert.Contains("Invalid XML", r.ErrorMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_WithoutFilenameId_UsesTcxSourceAndStartTimeId()
    {
        var r = ParseFixture("single-lap.tcx", useFilenameAsId: false);
        // Without filename-id the deterministic scheme keys on start time.
        Assert.Equal("tcx", r.Summary!.Source);
        Assert.Equal("tcx", r.Summary!.ProviderId);
    }

    [Fact]
    public void Parse_UseFilenameAsId_UsesStravaSourceAndId()
    {
        // GI-07: a Strava-export re-import (numeric filename = the Strava activity id) must carry that id
        // through as the ExternalId so a second import dedupe-skips instead of duplicating the ride.
        var tps =
            "<Trackpoint><Time>2026-02-02T09:00:00Z</Time><Position>" +
            "<LatitudeDegrees>47.00</LatitudeDegrees><LongitudeDegrees>8.00</LongitudeDegrees></Position></Trackpoint>" +
            "<Trackpoint><Time>2026-02-02T09:00:20Z</Time><Position>" +
            "<LatitudeDegrees>47.01</LatitudeDegrees><LongitudeDegrees>8.01</LongitudeDegrees></Position></Trackpoint>";
        var r = ParseXml(Tcx(tps), fileName: "987654321.tcx", useFilenameAsId: true);

        Assert.True(r.Success, r.ErrorMessage);
        Assert.Equal("strava", r.Summary!.Source);
        Assert.Equal("strava", r.Summary!.ProviderId);
        Assert.Equal(987654321L, r.Summary!.ExternalId);
    }
}
