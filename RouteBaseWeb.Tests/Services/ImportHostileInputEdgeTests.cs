using System.Text;
using Dynastream.Fit;
using Microsoft.Extensions.Logging.Abstractions;
using RouteBaseWeb.Models.Gpx;
using RouteBaseWeb.Services;
using Xunit;
using DateTime = System.DateTime;

namespace RouteBaseWeb.Tests.Services;

/// <summary>
/// Hostile-input suite, second file (import fuzzing, 2026-07-24) — continues
/// <see cref="ImportHostileInputTests"/>, which sits at the test-file size cap. Same contract:
/// every test pins clean rejection (<c>Success=false</c> + message) or clean degradation
/// (points kept, poisoned channel dropped), never an unhandled crash, never partial/corrupt
/// data persisting. This wave: timestamp extremes and parse-overflow, locale-shaped decimals,
/// multi-activity TCX, chained-FIT corruption, degenerate FIT structure, sniffer head corners.
/// Tests marked "pins current behavior" document a known oddity on purpose — read the
/// test's own comment before changing them.
/// </summary>
public class ImportHostileInputEdgeTests
{
    private const double SemicirclesPerDegree = 2147483648.0 / 180.0;

    private static TrackFileParserRouter RealRouter() => new(
        new GpxParsingService(NullLogger<GpxParsingService>.Instance),
        new TcxParsingService(NullLogger<TcxParsingService>.Instance),
        new FitParsingService(NullLogger<FitParsingService>.Instance));

    private static GpxParseResult RouteText(string content, string fileName) =>
        RealRouter().Parse(new MemoryStream(Encoding.UTF8.GetBytes(content)), fileName).Single();

    private static GpxParseResult ParseGpx(string xml) =>
        new GpxParsingService(NullLogger<GpxParsingService>.Instance)
            .Parse(new MemoryStream(Encoding.UTF8.GetBytes(xml)), "hostile.gpx").Single();

    private static GpxParseResult ParseTcx(string xml) =>
        new TcxParsingService(NullLogger<TcxParsingService>.Instance)
            .Parse(new MemoryStream(Encoding.UTF8.GetBytes(xml)), "hostile.tcx").Single();

    private static IReadOnlyList<GpxParseResult> ParseTcxAll(string xml, string fileName = "hostile.tcx") =>
        new TcxParsingService(NullLogger<TcxParsingService>.Instance)
            .Parse(new MemoryStream(Encoding.UTF8.GetBytes(xml)), fileName);

    private static IReadOnlyList<GpxParseResult> ParseFitAll(byte[] bytes, string fileName = "hostile.fit") =>
        new FitParsingService(NullLogger<FitParsingService>.Instance)
            .Parse(new MemoryStream(bytes), fileName);

    /// <summary>Minimal valid Activity FIT via the SDK encoder (same shape as the first file's helper).</summary>
    private static byte[] EncodeFitRide(int recordCount, DateTime startUtc)
    {
        using var ms = new MemoryStream();
        var encode = new Encode(ProtocolVersion.V20);
        encode.Open(ms);

        var fileId = new FileIdMesg();
        fileId.SetType(Dynastream.Fit.File.Activity);
        fileId.SetManufacturer(Manufacturer.Garmin);
        fileId.SetTimeCreated(new Dynastream.Fit.DateTime(startUtc));
        encode.Write(fileId);

        for (int i = 0; i < recordCount; i++)
        {
            var rec = new RecordMesg();
            rec.SetTimestamp(new Dynastream.Fit.DateTime(startUtc.AddSeconds(i)));
            rec.SetPositionLat((int)Math.Round((47.10 + (i % 100) * 1e-5) * SemicirclesPerDegree));
            rec.SetPositionLong((int)Math.Round(8.10 * SemicirclesPerDegree));
            encode.Write(rec);
        }

        var session = new SessionMesg();
        session.SetStartTime(new Dynastream.Fit.DateTime(startUtc));
        session.SetSport(Sport.Cycling);
        encode.Write(session);

        encode.Close();
        return ms.ToArray();
    }

    private static string GpxWithTimes(string firstTime, string lastTime) =>
        "<gpx xmlns=\"http://www.topografix.com/GPX/1/1\"><trk><trkseg>" +
        $"<trkpt lat=\"47.0\" lon=\"11.0\"><time>{firstTime}</time></trkpt>" +
        $"<trkpt lat=\"47.001\" lon=\"11.001\"><time>{lastTime}</time></trkpt>" +
        "</trkseg></trk></gpx>";

    // ── GPX: timestamp extremes ─────────────────────────────────────────────────

    [Fact]
    public void Gpx_ExtremeTimestampSpan_RejectedByRangeSweep()
    {
        // Both <time> values parse fine, but the computed elapsed span (year 1901 → year 9999,
        // ~2.5e11 s) blows past the MaxSeconds ceiling (1e8 ≈ 3.2 years). The OBS-1 tests pin
        // the sweep over DEVICE totals; this pins it over the COMPUTED time path — a corrupt
        // clock can't persist an 8,000-year activity.
        //
        // The start was year 1 until 2026-07-28: implausibly-old stamps now degrade to missing
        // BEFORE any span is computed, so that input would have exercised the new floor instead of
        // this sweep. Both ends are above the floor now, which keeps this test about what it says.
        var r = RouteText(GpxWithTimes("1901-01-02T00:00:00Z", "9999-12-30T00:00:00Z"), "epoch.gpx");

        Assert.False(r.Success);
        Assert.Contains("TotalTime", r.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("range", r.ErrorMessage, StringComparison.Ordinal);
        Assert.Empty(r.GpsPoints);
    }

    [Theory]
    [InlineData("0000-01-01T00:00:00Z")]           // year 0 does not exist in the proleptic calendar
    [InlineData("9999-12-31T23:59:59-14:00")]      // UTC equivalent overflows DateTime.MaxValue
    [InlineData("2026-13-45T99:99:99Z")]           // out-of-range calendar fields
    [InlineData("not-a-time")]
    public void Gpx_UnparseableOrOverflowingTime_TreatedAsMissingTimestamp(string time)
    {
        // DateTime.TryParse must absorb every one of these — including the offset-induced
        // MaxValue overflow — by returning false, never throwing. The file still imports with
        // the missing-timestamps degradation: points kept, time stats skipped, no wall-clock id.
        var r = ParseGpx(GpxWithTimes(time, time));

        Assert.True(r.Success, r.ErrorMessage);
        Assert.Equal(2, r.GpsPoints.Count);
        Assert.Null(r.Summary!.StartDateUtc);
        Assert.Contains(r.Warnings, w => w.Contains("missing timestamps", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("0001-01-01T00:00:00+14:00")]  // offset underflows DateTime.MinValue → .NET CLAMPS to year 1
    [InlineData("0001-01-02T00:00:00Z")]       // …and a year-1 stamp that needed no clamping at all
    [InlineData("1899-12-31T23:59:59Z")]       // just under the plausibility floor
    public void Gpx_ImplausiblyOldTime_DegradesToMissingTimestamp(string time)
    {
        // Decided 2026-07-28 (was pinned as current behaviour by the 07-24 import-fuzz job, OBS-2).
        // An offset pushing the instant below DateTime.MinValue does NOT fail TryParse — .NET clamps
        // to year 1 and reports success — so a hostile file could mint a year-0001 activity, while the
        // MaxValue side (which does fail TryParse) already degraded to missing. Both ends now behave
        // the same: no timestamp is better than a fictional one.
        var r = ParseGpx(GpxWithTimes(time, time));

        Assert.True(r.Success, r.ErrorMessage);
        Assert.Equal(2, r.GpsPoints.Count);          // the ride still imports — only its clock is dropped
        Assert.Null(r.Summary!.StartDateUtc);
        Assert.Contains(r.Warnings, w => w.Contains("missing timestamps", StringComparison.Ordinal));
    }

    [Fact]
    public void Gpx_AnOldButRealRide_KeepsItsTimestamps()
    {
        // The boundary must not eat legitimate history: GPS-era rides are far above the 1900 floor,
        // and this is what stops the guard from being over-eager.
        var r = ParseGpx(GpxWithTimes("1990-05-01T06:00:00Z", "1990-05-01T06:30:00Z"));

        Assert.True(r.Success, r.ErrorMessage);
        Assert.Equal(1990, r.Summary!.StartDateUtc!.Value.Year);
        Assert.DoesNotContain(r.Warnings, w => w.Contains("missing timestamps", StringComparison.Ordinal));
    }

    // ── GPX: locale-shaped decimals ─────────────────────────────────────────────

    [Fact]
    public void Gpx_CommaDecimalCoordinates_SkippedNotMisparsed()
    {
        // European-locale export ("47,5"): NumberStyles.Float excludes AllowThousands, so the
        // comma can neither parse as a decimal separator nor collapse "47,5" into 475 — the
        // points are skipped wholesale and the file is cleanly rejected, not imported with
        // silently wrong coordinates.
        var xml = "<gpx xmlns=\"http://www.topografix.com/GPX/1/1\"><trk><trkseg>" +
                  "<trkpt lat=\"47,5\" lon=\"11,5\"/>" +
                  "<trkpt lat=\"47,6\" lon=\"11,6\"/>" +
                  "</trkseg></trk></gpx>";
        var r = ParseGpx(xml);

        Assert.False(r.Success);
        Assert.Equal("GPX file contains no valid track points.", r.ErrorMessage);
        Assert.Empty(r.GpsPoints);
    }

    // ── TCX: multi-activity files ───────────────────────────────────────────────

    [Fact]
    public void Tcx_EveryActivityIsImported_NotJustTheFirst()
    {
        // Decided 2026-07-28 (was pinned as current behaviour by the 07-24 import-fuzz job, OBS-1).
        // A TCX can carry several <Activity> elements — Garmin Training Center "export all" bundles do
        // — and the parser used to take FirstOrDefault() and drop the rest with NO warning, unlike
        // multi-track GPX (warns) and chained FIT (splits). It now splits like the FIT path.
        var xml =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
            "<TrainingCenterDatabase xmlns=\"http://www.garmin.com/xmlschemas/TrainingCenterDatabase/v2\">" +
            "<Activities>" +
            "<Activity Sport=\"Running\"><Id>2026-02-02T09:00:00Z</Id>" +
            "<Lap StartTime=\"2026-02-02T09:00:00Z\"><Track>" +
            "<Trackpoint><Time>2026-02-02T09:00:00Z</Time><Position>" +
            "<LatitudeDegrees>47.20</LatitudeDegrees><LongitudeDegrees>8.20</LongitudeDegrees></Position></Trackpoint>" +
            "<Trackpoint><Time>2026-02-02T09:00:10Z</Time><Position>" +
            "<LatitudeDegrees>47.21</LatitudeDegrees><LongitudeDegrees>8.21</LongitudeDegrees></Position></Trackpoint>" +
            "</Track></Lap></Activity>" +
            "<Activity Sport=\"Biking\"><Id>2026-03-03T10:00:00Z</Id>" +
            "<Lap StartTime=\"2026-03-03T10:00:00Z\"><Track>" +
            "<Trackpoint><Time>2026-03-03T10:00:00Z</Time><Position>" +
            "<LatitudeDegrees>48.50</LatitudeDegrees><LongitudeDegrees>9.50</LongitudeDegrees></Position></Trackpoint>" +
            "<Trackpoint><Time>2026-03-03T10:00:10Z</Time><Position>" +
            "<LatitudeDegrees>48.51</LatitudeDegrees><LongitudeDegrees>9.51</LongitudeDegrees></Position></Trackpoint>" +
            "<Trackpoint><Time>2026-03-03T10:00:20Z</Time><Position>" +
            "<LatitudeDegrees>48.52</LatitudeDegrees><LongitudeDegrees>9.52</LongitudeDegrees></Position></Trackpoint>" +
            "</Track></Lap></Activity>" +
            "</Activities></TrainingCenterDatabase>";
        var results = ParseTcxAll(xml, "bundle.tcx");

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.True(r.Success, r.ErrorMessage));

        // Activity #1: February, 2 points, keeps the plain filename.
        Assert.Equal(2, results[0].GpsPoints.Count);
        Assert.Equal(2, results[0].Summary!.StartDateUtc!.Value.Month);
        Assert.Equal("bundle", results[0].Summary!.Name);

        // Activity #2: March, 3 points — the ride that used to vanish — with the FIT split's naming.
        Assert.Equal(3, results[1].GpsPoints.Count);
        Assert.Equal(3, results[1].Summary!.StartDateUtc!.Value.Month);
        Assert.Equal("bundle (2)", results[1].Summary!.Name);

        // Distinct external ids: siblings sharing one id would dedupe against each other on re-import.
        Assert.NotEqual(results[0].Summary!.ExternalId, results[1].Summary!.ExternalId);
    }

    [Fact]
    public void Tcx_OneUnusableSessionDoesNotSinkTheRidesAroundIt()
    {
        // A bundle whose middle session has no fixes (indoor trainer) must still import the rest —
        // the FIT split's rule, now shared.
        var xml =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
            "<TrainingCenterDatabase xmlns=\"http://www.garmin.com/xmlschemas/TrainingCenterDatabase/v2\">" +
            "<Activities>" +
            "<Activity Sport=\"Running\"><Id>2026-02-02T09:00:00Z</Id>" +
            "<Lap StartTime=\"2026-02-02T09:00:00Z\"><Track>" +
            "<Trackpoint><Time>2026-02-02T09:00:00Z</Time><Position>" +
            "<LatitudeDegrees>47.20</LatitudeDegrees><LongitudeDegrees>8.20</LongitudeDegrees></Position></Trackpoint>" +
            "</Track></Lap></Activity>" +
            "<Activity Sport=\"Biking\"><Id>2026-03-03T10:00:00Z</Id>" +
            "<Lap StartTime=\"2026-03-03T10:00:00Z\"><Track>" +
            "<Trackpoint><Time>2026-03-03T10:00:00Z</Time></Trackpoint>" +   // indoor: no Position
            "</Track></Lap></Activity>" +
            "</Activities></TrainingCenterDatabase>";

        var results = ParseTcxAll(xml);

        var kept = Assert.Single(results);
        Assert.True(kept.Success, kept.ErrorMessage);
        Assert.Equal(2, kept.Summary!.StartDateUtc!.Value.Month);
    }

    [Fact]
    public void Tcx_WhenNothingParses_TheErrorStillSpeaks()
    {
        // …but a file where every session is unusable must not silently import as "nothing".
        var xml =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
            "<TrainingCenterDatabase xmlns=\"http://www.garmin.com/xmlschemas/TrainingCenterDatabase/v2\">" +
            "<Activities>" +
            "<Activity Sport=\"Biking\"><Id>2026-03-03T10:00:00Z</Id>" +
            "<Lap StartTime=\"2026-03-03T10:00:00Z\"><Track>" +
            "<Trackpoint><Time>2026-03-03T10:00:00Z</Time></Trackpoint>" +
            "</Track></Lap></Activity>" +
            "</Activities></TrainingCenterDatabase>";

        var r = Assert.Single(ParseTcxAll(xml));

        Assert.False(r.Success);
        Assert.Equal("TCX file contains no valid track points.", r.ErrorMessage);
    }

    [Fact]
    public void Tcx_ExtremeLapMaximumSpeed_RejectedByRangeSweep()
    {
        // A finite-but-impossible lap MaximumSpeed (1e6 m/s) sails through the per-value finite
        // check and the parser's lap-total sum guard (MaximumSpeed is Max'd, not summed) — the
        // router's range sweep is the only line that catches it. Extends OBS-1 coverage to the
        // speed tier.
        var xml =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
            "<TrainingCenterDatabase xmlns=\"http://www.garmin.com/xmlschemas/TrainingCenterDatabase/v2\">" +
            "<Activities><Activity Sport=\"Running\"><Id>2026-02-02T09:00:00Z</Id>" +
            "<Lap StartTime=\"2026-02-02T09:00:00Z\"><MaximumSpeed>1000000</MaximumSpeed><Track>" +
            "<Trackpoint><Time>2026-02-02T09:00:00Z</Time><Position>" +
            "<LatitudeDegrees>47.20</LatitudeDegrees><LongitudeDegrees>8.20</LongitudeDegrees></Position></Trackpoint>" +
            "<Trackpoint><Time>2026-02-02T09:00:10Z</Time><Position>" +
            "<LatitudeDegrees>47.21</LatitudeDegrees><LongitudeDegrees>8.21</LongitudeDegrees></Position></Trackpoint>" +
            "</Track></Lap></Activity></Activities></TrainingCenterDatabase>";
        var r = RouteText(xml, "warp.tcx");

        Assert.False(r.Success);
        Assert.Contains("MaxSpeed", r.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("range", r.ErrorMessage, StringComparison.Ordinal);
        Assert.Empty(r.GpsPoints);
    }

    // ── FIT: chained-stream corruption + degenerate structure ───────────────────

    [Fact]
    public void Fit_ChainedValidThenTruncatedSecond_WholeStreamRejected()
    {
        // Chain integrity is all-or-nothing: a complete valid file followed by a truncated
        // second file must reject the WHOLE stream (Decode.Read returns false), never import
        // the intact first activity and silently drop the corrupt tail — the sibling of the
        // two-valid-files split test in the first suite.
        var a = EncodeFitRide(5, new DateTime(2026, 5, 1, 6, 0, 0, DateTimeKind.Utc));
        var b = EncodeFitRide(20, new DateTime(2026, 6, 1, 6, 0, 0, DateTimeKind.Utc));
        var chained = a.Concat(b[..(b.Length * 6 / 10)]).ToArray();

        var results = ParseFitAll(chained, "chain-truncated.fit");

        var r = Assert.Single(results);
        Assert.False(r.Success);
        Assert.NotNull(r.ErrorMessage);
        Assert.Empty(r.GpsPoints);
    }

    [Fact]
    public void Fit_RecordsBeforeAnyFileId_ImplicitSegmentHandledCleanly()
    {
        // Spec-invalid but decodable: records with no FileId message at all. The parser's
        // defensive implicit-segment path (Current()) must bucket them rather than drop or
        // crash — the ride imports with every point, named from the filename.
        using var ms = new MemoryStream();
        var encode = new Encode(ProtocolVersion.V20);
        encode.Open(ms);
        var start = new DateTime(2026, 5, 1, 6, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < 3; i++)
        {
            var rec = new RecordMesg();
            rec.SetTimestamp(new Dynastream.Fit.DateTime(start.AddSeconds(i)));
            rec.SetPositionLat((int)Math.Round(47.10 * SemicirclesPerDegree));
            rec.SetPositionLong((int)Math.Round(8.10 * SemicirclesPerDegree));
            encode.Write(rec);
        }
        encode.Close();

        var results = ParseFitAll(ms.ToArray(), "headless.fit");

        var r = Assert.Single(results);
        Assert.True(r.Success, r.ErrorMessage);
        Assert.Equal(3, r.GpsPoints.Count);
        Assert.Equal("headless", r.Summary!.Name);
    }

    [Fact]
    public void Fit_AllPositionsOutOfRange_RejectedNoGpsPoints()
    {
        // Every record carries a semicircle latitude decoding to ~180° (> 90): all points are
        // dropped by the per-point range check and the file fails with the no-track message —
        // the all-points-poisoned sibling of the single-point exclusion test in
        // FitParsingServiceTests.
        using var ms = new MemoryStream();
        var encode = new Encode(ProtocolVersion.V20);
        encode.Open(ms);
        var start = new DateTime(2026, 5, 1, 6, 0, 0, DateTimeKind.Utc);
        var fileId = new FileIdMesg();
        fileId.SetType(Dynastream.Fit.File.Activity);
        fileId.SetManufacturer(Manufacturer.Garmin);
        fileId.SetTimeCreated(new Dynastream.Fit.DateTime(start));
        encode.Write(fileId);
        for (int i = 0; i < 2; i++)
        {
            var rec = new RecordMesg();
            rec.SetTimestamp(new Dynastream.Fit.DateTime(start.AddSeconds(i)));
            rec.SetPositionLat(int.MaxValue);   // ≈ +180° latitude
            rec.SetPositionLong((int)Math.Round(8.10 * SemicirclesPerDegree));
            encode.Write(rec);
        }
        encode.Close();

        var results = ParseFitAll(ms.ToArray());

        var r = Assert.Single(results);
        Assert.False(r.Success);
        Assert.Equal("FIT file contains no GPS track points.", r.ErrorMessage);
        Assert.Empty(r.GpsPoints);
    }

    [Fact]
    public void Fit_SentinelTimestamps_PointsKeptTimeStatsSkipped()
    {
        // FIT's 0 timestamp is a sentinel, not a real instant (it would decode to 1989-12-31).
        // The parser must treat it as MISSING — points kept, time stats skipped — never as a
        // wall-clock value that becomes the activity's start date and external id.
        using var ms = new MemoryStream();
        var encode = new Encode(ProtocolVersion.V20);
        encode.Open(ms);
        var fileId = new FileIdMesg();
        fileId.SetType(Dynastream.Fit.File.Activity);
        fileId.SetManufacturer(Manufacturer.Garmin);
        encode.Write(fileId);
        for (int i = 0; i < 2; i++)
        {
            var rec = new RecordMesg();
            rec.SetTimestamp(new Dynastream.Fit.DateTime(0));
            rec.SetPositionLat((int)Math.Round((47.10 + i * 1e-4) * SemicirclesPerDegree));
            rec.SetPositionLong((int)Math.Round(8.10 * SemicirclesPerDegree));
            encode.Write(rec);
        }
        encode.Close();

        var results = ParseFitAll(ms.ToArray());

        var r = Assert.Single(results);
        Assert.True(r.Success, r.ErrorMessage);
        Assert.Equal(2, r.GpsPoints.Count);
        Assert.Null(r.Summary!.StartDateUtc);
        Assert.Contains(r.Warnings, w => w.Contains("missing timestamps", StringComparison.Ordinal));
    }

    // ── Sniffer: degenerate heads ───────────────────────────────────────────────

    [Theory]
    [InlineData(new byte[] { 0xEF, 0xBB, 0xBF })]                        // UTF-8 BOM, nothing after it
    [InlineData(new byte[] { (byte)'<' })]                               // lone '<', no root element
    [InlineData(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 })] // all-NUL head
    public void Detect_DegenerateHeads_Unknown(byte[] head)
    {
        // Head shapes that flirt with the XML / UTF-16-heuristic branches but carry no root
        // element must all fall through to Unknown (→ clean upload reject), not throw in the
        // decode paths.
        Assert.Equal(TrackFileFormat.Unknown, TrackFileSniffer.Detect(head));
    }
}
