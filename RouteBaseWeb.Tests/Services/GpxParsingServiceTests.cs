using System.Globalization;
using System.Text;
using RouteBaseWeb.Models.Gpx;
using RouteBaseWeb.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace RouteBaseWeb.Tests.Services;

/// <summary>
/// GI-14: characterization suite pinning the CURRENT behavior of <see cref="GpxParsingService"/>.
/// Hard prerequisite for GI-02 (funnel rework), GI-07 (deterministic ExternalId) and GI-01
/// (timezone resolver): those batches edit these expectations only where their specs say so.
/// Any assertion tagged "current-behavior oddity" pins a known-wrong value on purpose.
/// </summary>
public class GpxParsingServiceTests
{
    private static GpxParsingService NewService() =>
        new(NullLogger<GpxParsingService>.Instance);

    // GPX always yields a single result (single-element list contract) — unwrap for the assertions.
    private static GpxParseResult ParseFixture(string name, bool useFilenameAsId = false, string? fileNameOverride = null)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", "Gpx", name);
        using var stream = File.OpenRead(path);
        return NewService().Parse(stream, fileNameOverride ?? name, useFilenameAsId).Single();
    }

    private static GpxParseResult ParseXml(string xml, string fileName = "inline.gpx", bool useFilenameAsId = false)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        return NewService().Parse(stream, fileName, useFilenameAsId).Single();
    }

    // --- Garmin extended file: track-level stats preferred over computed values ---

    [Fact]
    public void Parse_GarminExtensions_PreferredOverComputed()
    {
        var r = ParseFixture("garmin-ext.gpx");

        Assert.True(r.Success);
        Assert.Null(r.ErrorMessage);
        var s = r.Summary!;

        Assert.Equal("Morning Run", s.Name);
        Assert.Equal("Run", s.ActivityType);
        Assert.Equal("gpx", s.ProviderId);
        Assert.Equal("gpx", s.Source);

        // Track-level TrackStatsExtension values win over point-computed ones.
        Assert.Equal(5000, s.Distance);        // extDistance, not computed haversine
        Assert.Equal(120, s.ElevationGain);    // extAscent, not computed 20
        Assert.Equal(80, s.TotalDescent);      // extDescent, not computed 0
        Assert.Equal(350, s.Calories);
        Assert.Equal(1900, s.ElapsedTime);     // TotalElapsedTime
        Assert.Equal(1800, s.TotalTime);       // TimerTime
        Assert.Equal(148, s.AverageHeartRate); // extAvgHr, computed avg is 145
        Assert.Equal(172, s.AverageCadence);   // extAvgCadence, computed avg is 170

        // Point-computed values with no ext override.
        Assert.Equal(150, s.MaxHeartRate);
        Assert.Equal(175, s.MaxCadence);
        Assert.Equal(210, s.AveragePower);
        Assert.Equal(220, s.MaxPower);

        Assert.Equal(3, r.GpsPoints.Count);
        Assert.Equal(47.2692, s.StartLat);
        Assert.Equal(11.4041, s.StartLng);
        Assert.Equal(47.2710, s.EndLat);
        Assert.Equal(11.4060, s.EndLng);
    }

    [Fact]
    public void Parse_TimestampedFile_ExternalIdIsStartTicks()
    {
        var r = ParseFixture("garmin-ext.gpx");
        var s = r.Summary!;

        var expectedStart = new DateTime(2023, 6, 15, 8, 0, 0, DateTimeKind.Utc);
        Assert.Equal(expectedStart, s.StartDateUtc);
        Assert.Equal(expectedStart.Ticks, s.ExternalId); // GI-07 will keep Ticks for timestamped files
    }

    [Fact]
    public void Parse_AverageSpeed_IsComputedDistanceOverElapsed()
    {
        var r = ParseFixture("garmin-ext.gpx");
        var s = r.Summary!;

        // AverageSpeed uses the point-computed cumulative distance, NOT extDistance,
        // divided by elapsed wall time (600 s here).
        var computedDistance = r.GpsPoints[^1].Distance!.Value;
        Assert.Equal(computedDistance / 600.0, s.AverageSpeed!.Value, 6);
    }

    // --- GI-02: moving time + max speed computed from smoothed per-point speeds ---

    [Fact]
    public void Parse_ComputesMovingTimeAndMaxSpeed()
    {
        // 6 moving points (~7.6 m/s) then 6 stationary points, 1 s apart → 11 s elapsed.
        // The stationary tail smooths to ~0 m/s (< 0.5 threshold) so it is not "moving".
        var sb = new StringBuilder();
        sb.Append("<gpx xmlns=\"http://www.topografix.com/GPX/1/1\"><trk><trkseg>");
        var start = new DateTime(2023, 6, 15, 8, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < 12; i++)
        {
            double lng = i < 6 ? 11.0 + i * 0.0001 : 11.0 + 5 * 0.0001; // freeze after point 5
            var t = start.AddSeconds(i).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            sb.Append(CultureInfo.InvariantCulture, $"<trkpt lat=\"47.0\" lon=\"{lng.ToString(System.Globalization.CultureInfo.InvariantCulture)}\">" +
                      $"<ele>500</ele><time>{t}</time></trkpt>");
        }
        sb.Append("</trkseg></trk></gpx>");

        var r = ParseXml(sb.ToString());
        Assert.True(r.Success);
        var s = r.Summary!;

        Assert.Equal(11, s.ElapsedTime); // 11 s wall time
        Assert.NotNull(s.MovingTime);
        Assert.True(s.MovingTime > 0, "some moving time expected");
        Assert.True(s.MovingTime < s.ElapsedTime, "stationary tail should reduce moving time below elapsed");

        Assert.NotNull(s.MaxSpeed);
        Assert.Equal(r.GpsPoints.Max(p => p.Speed!.Value), s.MaxSpeed!.Value, 6);
        Assert.True(s.MaxSpeed > 0.5);
    }

    [Fact]
    public void Parse_NoTimestamps_MovingTimeAndMaxSpeedNull()
    {
        var r = ParseFixture("no-timestamps.gpx");
        var s = r.Summary!;

        Assert.Null(s.MovingTime);
        Assert.Null(s.MaxSpeed);
    }

    // --- Cumulative per-point distance ---

    [Fact]
    public void Parse_PerPointDistance_IsMonotonicFromZero()
    {
        var r = ParseFixture("garmin-ext.gpx");
        var pts = r.GpsPoints;

        Assert.Equal(0, pts[0].Distance);
        for (int i = 1; i < pts.Count; i++)
            Assert.True(pts[i].Distance > pts[i - 1].Distance);
    }

    // --- Minimal (Strava-style) file: computed values, no extensions ---

    [Fact]
    public void Parse_Minimal_UsesComputedValues()
    {
        var r = ParseFixture("minimal.gpx");

        Assert.True(r.Success);
        var s = r.Summary!;

        Assert.Equal("Evening Ride", s.Name);
        Assert.Equal("Ride", s.ActivityType);
        Assert.Equal(r.GpsPoints[^1].Distance, s.Distance); // computed cumulative distance
        Assert.Equal(30, s.ElevationGain);   // computed gain 10+20
        Assert.Null(s.TotalDescent);         // no elevation loss -> null
        Assert.Null(s.Calories);
        Assert.Null(s.AverageHeartRate);
        Assert.Equal(240, s.ElapsedTime);    // 4 min wall time
        Assert.Equal(240, s.TotalTime);      // no TimerTime ext -> falls back to elapsed
    }

    [Fact]
    public void Parse_Minimal_StartLocalEqualsUtc_TimezoneNull()
    {
        // GI-01/M4: the parser stays pure — it emits UTC-as-local with a null TimezoneOffset
        // because a GPX file carries no timezone. The timezone offset is applied downstream at
        // the orchestrator seam (IActivityTimezoneResolver + TrackImportLocalTime.ApplyLocalTime),
        // covered by TrackImportLocalTimeTests / GeoTimeZoneActivityResolverTests — not here.
        var r = ParseFixture("minimal.gpx");
        var s = r.Summary!;

        var expectedStart = new DateTime(2023, 6, 15, 18, 0, 0, DateTimeKind.Utc);
        Assert.Equal(expectedStart, s.StartDateUtc);
        Assert.Equal(s.StartDateUtc, s.StartDateLocal);
        Assert.Null(s.TimezoneOffset);
        Assert.Equal(expectedStart.Ticks, s.ExternalId);
    }

    // --- Filename-as-id: numeric filename maps to strava source ---

    [Fact]
    public void Parse_NumericFilename_WithUseFilenameAsId_IsStravaSource()
    {
        var r = ParseFixture("minimal.gpx", useFilenameAsId: true, fileNameOverride: "987654.gpx");
        var s = r.Summary!;

        Assert.Equal("strava", s.ProviderId);
        Assert.Equal("strava", s.Source);
        Assert.Equal(987654, s.ExternalId);
        Assert.Equal("Evening Ride", s.Name); // name still comes from <trk>, not filename
    }

    [Fact]
    public void Parse_NonNumericFilename_WithUseFilenameAsId_FallsBackToGpxSource()
    {
        var r = ParseFixture("minimal.gpx", useFilenameAsId: true, fileNameOverride: "route.gpx");
        var s = r.Summary!;

        Assert.Equal("gpx", s.ProviderId);
        Assert.Equal("gpx", s.Source);
    }

    // --- No timestamps: time-based stats skipped, warning added ---

    [Fact]
    public void Parse_NoTimestamps_SkipsTimeStats_AddsWarning()
    {
        var r = ParseFixture("no-timestamps.gpx");

        Assert.True(r.Success);
        var s = r.Summary!;

        Assert.Equal("Hike", s.ActivityType);
        Assert.Null(s.StartDateUtc);
        Assert.Null(s.StartDateLocal);
        Assert.Null(s.ElapsedTime);
        Assert.Null(s.TotalTime);
        Assert.Null(s.AverageSpeed);
        Assert.Contains(r.Warnings, w => w.Contains("missing timestamps", StringComparison.Ordinal));

        // Computed distance still present; speeds all zero (no timestamps to smooth).
        Assert.True(s.Distance > 0);
        Assert.All(r.GpsPoints, p => Assert.Equal(0, p.Speed));
        // ExternalId here is now a deterministic content hash (GI-07); see the two tests below.
    }

    // --- GI-07: deterministic content-hash ExternalId for timestamp-less GPX ---

    [Fact]
    public void Parse_NoTimestamps_SameFileTwice_SameExternalId()
    {
        var first = ParseFixture("no-timestamps.gpx");
        var second = ParseFixture("no-timestamps.gpx");

        Assert.True(first.Success && second.Success);
        Assert.Null(first.Summary!.StartDateUtc); // confirm we're on the content-hash branch
        Assert.Equal(first.Summary!.ExternalId, second.Summary!.ExternalId);
    }

    [Fact]
    public void Parse_NoTimestamps_DifferentTracks_DifferentExternalId()
    {
        const string trackA = """
            <gpx xmlns="http://www.topografix.com/GPX/1/1"><trk><trkseg>
            <trkpt lat="47.10" lon="11.10"><ele>500</ele></trkpt>
            <trkpt lat="47.20" lon="11.20"><ele>510</ele></trkpt>
            </trkseg></trk></gpx>
            """;
        const string trackB = """
            <gpx xmlns="http://www.topografix.com/GPX/1/1"><trk><trkseg>
            <trkpt lat="48.10" lon="12.10"><ele>500</ele></trkpt>
            <trkpt lat="48.20" lon="12.20"><ele>510</ele></trkpt>
            </trkseg></trk></gpx>
            """;

        var a = ParseXml(trackA);
        var b = ParseXml(trackB);

        Assert.True(a.Success && b.Success);
        Assert.Null(a.Summary!.StartDateUtc);
        Assert.NotEqual(a.Summary!.ExternalId, b.Summary!.ExternalId);
    }

    // --- Namespace-free GPX (some third-party tools) ---

    [Fact]
    public void Parse_NoNamespace_ParsesPointsAndType()
    {
        var r = ParseFixture("no-namespace.gpx");

        Assert.True(r.Success);
        var s = r.Summary!;

        Assert.Equal("Namespace Free Walk", s.Name);
        Assert.Equal("Walk", s.ActivityType);
        Assert.Equal(3, r.GpsPoints.Count);
        Assert.NotNull(s.StartDateUtc);
    }

    // --- Multi-track: first track only + warning ---

    [Fact]
    public void Parse_MultiTrack_ImportsFirstTrackOnly_WithWarning()
    {
        var r = ParseFixture("multi-track.gpx");

        Assert.True(r.Success);
        var s = r.Summary!;

        Assert.Contains(r.Warnings, w => w.Contains("Multiple tracks found (2)", StringComparison.Ordinal));
        Assert.Equal(2, r.GpsPoints.Count); // first track has 2 points; second track ignored
        Assert.Equal("First Track", s.Name);
        Assert.Equal("Run", s.ActivityType);
    }

    // --- Error paths ---

    [Fact]
    public void Parse_InvalidXml_ReturnsErrorMessage()
    {
        var r = ParseXml("<gpx><trk><not-closed>");

        Assert.False(r.Success);
        Assert.NotNull(r.ErrorMessage);
        Assert.StartsWith("Invalid XML", r.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_DoctypeEntity_IsRejected()
    {
        var xml = "<?xml version=\"1.0\"?>" +
            "<!DOCTYPE gpx [<!ENTITY e \"boom\"><!ENTITY % p SYSTEM \"file:///etc/passwd\">]>" +
            "<gpx xmlns=\"http://www.topografix.com/GPX/1/1\"><trk><name>&e;</name><trkseg>" +
            "<trkpt lat=\"47.0\" lon=\"11.0\"><ele>500</ele></trkpt></trkseg></trk></gpx>";
        var r = ParseXml(xml);

        Assert.False(r.Success);
        Assert.Contains("Invalid XML", r.ErrorMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_NoTracks_ReturnsErrorMessage()
    {
        var r = ParseXml("<gpx xmlns=\"http://www.topografix.com/GPX/1/1\"></gpx>");

        Assert.False(r.Success);
        Assert.Equal("No tracks found in GPX file.", r.ErrorMessage);
    }

    [Fact]
    public void Parse_NonFiniteAndOutOfRangeCoords_ExcludedAndStatsFinite()
    {
        var xml = "<gpx xmlns=\"http://www.topografix.com/GPX/1/1\"><trk><trkseg>" +
                  "<trkpt lat=\"47.0\" lon=\"11.0\"><ele>1e309</ele></trkpt>" +   // good pos, non-finite ele
                  "<trkpt lat=\"NaN\" lon=\"11.1\"><ele>500</ele></trkpt>" +      // NaN lat → excluded
                  "<trkpt lat=\"91.5\" lon=\"11.2\"><ele>500</ele></trkpt>" +     // out-of-range → excluded
                  "<trkpt lat=\"47.2\" lon=\"11.2\"><ele>510</ele></trkpt>" +
                  "</trkseg></trk></gpx>";
        var r = ParseXml(xml);

        Assert.True(r.Success, r.ErrorMessage);
        Assert.Equal(2, r.GpsPoints.Count);              // NaN + out-of-range points dropped
        Assert.Null(r.GpsPoints[0].Altitude);            // 1e309 → null
        Assert.True(double.IsFinite(r.Summary!.Distance!.Value));
    }

    [Fact]
    public void Parse_NoValidPoints_ReturnsErrorMessage()
    {
        var xml = "<gpx xmlns=\"http://www.topografix.com/GPX/1/1\"><trk><trkseg>" +
                  "<trkpt lat=\"abc\" lon=\"xyz\"/>" +
                  "</trkseg></trk></gpx>";
        var r = ParseXml(xml);

        Assert.False(r.Success);
        Assert.Equal("GPX file contains no valid track points.", r.ErrorMessage);
    }

    // --- Activity-type mapping ---

    [Theory]
    [InlineData("running", "Run")]
    [InlineData("run", "Run")]
    [InlineData("cycling", "Ride")]
    [InlineData("ride", "Ride")]
    [InlineData("biking", "Ride")]
    [InlineData("hiking", "Hike")]
    [InlineData("walking", "Walk")]
    [InlineData("swimming", "Swim")]
    [InlineData("swim", "Swim")]
    [InlineData("kayaking", "kayaking")] // unknown type passes through unchanged
    public void Parse_ActivityType_MapsKnownAndPassesUnknown(string gpxType, string expected)
    {
        var r = ParseXml(BuildTypedGpx(gpxType));
        Assert.Equal(expected, r.Summary!.ActivityType);
    }

    [Fact]
    public void Parse_MissingType_DefaultsToWalk()
    {
        var xml = "<gpx xmlns=\"http://www.topografix.com/GPX/1/1\"><trk><trkseg>" +
                  "<trkpt lat=\"47.0\" lon=\"11.0\"><ele>500</ele></trkpt>" +
                  "<trkpt lat=\"47.1\" lon=\"11.1\"><ele>510</ele></trkpt>" +
                  "</trkseg></trk></gpx>";
        var r = ParseXml(xml);
        Assert.Equal("Walk", r.Summary!.ActivityType);
    }

    private static string BuildTypedGpx(string type) =>
        "<gpx xmlns=\"http://www.topografix.com/GPX/1/1\"><trk>" +
        $"<type>{type}</type><trkseg>" +
        "<trkpt lat=\"47.0\" lon=\"11.0\"><ele>500</ele></trkpt>" +
        "<trkpt lat=\"47.1\" lon=\"11.1\"><ele>510</ele></trkpt>" +
        "</trkseg></trk></gpx>";

    // --- GI-12: timestamp parse must not depend on the running thread's culture ---

    [Fact]
    public void Parse_TrTrThreadCulture_TimestampsStillParsed()
    {
        var original = System.Threading.Thread.CurrentThread.CurrentCulture;
        System.Threading.Thread.CurrentThread.CurrentCulture =
            new System.Globalization.CultureInfo("tr-TR");
        try
        {
            var r = ParseFixture("minimal.gpx");
            Assert.True(r.Success);
            Assert.NotNull(r.Summary!.StartDateUtc);
        }
        finally
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = original;
        }
    }
}
