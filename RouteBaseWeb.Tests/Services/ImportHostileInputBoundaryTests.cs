using System.Text;
using Dynastream.Fit;
using Microsoft.Extensions.Logging.Abstractions;
using RouteBaseWeb.Models.Gpx;
using RouteBaseWeb.Services;
using Xunit;
using DateTime = System.DateTime;

namespace RouteBaseWeb.Tests.Services;

/// <summary>
/// Hostile-input suite, fourth file (import fuzzing, 2026-07-30) — continues
/// <see cref="ImportHostileInputTests"/>, <see cref="ImportHostileInputEdgeTests"/> and
/// <see cref="ImportHostileInputDeltaTests"/>, which sit at the test-file size cap. Same contract:
/// clean rejection (<c>Success=false</c> + message) or clean skip/degradation (poisoned points
/// dropped, valid ones kept), never an unhandled crash, never partial/corrupt data persisting.
///
/// This wave closes coverage the earlier waves left open, re-derived from the parser source rather
/// than a prior summary:
///  • coordinate BOUNDARY inclusivity for all three parsers — ±90 / ±180 exact are accepted, and a
///    value one step past the boundary is SKIPPED, never clamped onto it (the classic off-by-one
///    the earlier NaN/out-of-range tests never pinned);
///  • the router's per-result <c>GuardResult</c> isolation — a poisoned activity in a multi-result
///    parse must fail ALONE, its clean siblings surviving (the code comment claims this; nothing
///    exercised it);
///  • the sniffer's no-BOM UTF-16 BIG-ENDIAN branch (only the LE branch was exercised);
///  • TCX deep-nesting parity with the GPX no-crash test, and a filename that sanitizes to nothing.
/// </summary>
public class ImportHostileInputBoundaryTests
{
    private const double SemicirclesPerDegree = 2147483648.0 / 180.0;

    // Exact ±90° in semicircles: 90 / (180 / 2^31) = 2^31 / 2. Representable to the unit; 180° is NOT
    // (it needs 2^31, one past int.MaxValue), so the longitude poles are tested via the XML parsers.
    private const int NinetyDegLatSemicircles = 1073741824; // 2^31 / 2

    private static TrackFileParserRouter RealRouter() => new(
        new GpxParsingService(NullLogger<GpxParsingService>.Instance),
        new TcxParsingService(NullLogger<TcxParsingService>.Instance),
        new FitParsingService(NullLogger<FitParsingService>.Instance));

    private static GpxParseResult RouteText(string content, string fileName) =>
        RealRouter().Parse(new MemoryStream(Encoding.UTF8.GetBytes(content)), fileName).Single();

    private static IReadOnlyList<GpxParseResult> RouteAll(string content, string fileName) =>
        RealRouter().Parse(new MemoryStream(Encoding.UTF8.GetBytes(content)), fileName);

    private static GpxParseResult ParseGpx(string xml) =>
        new GpxParsingService(NullLogger<GpxParsingService>.Instance)
            .Parse(new MemoryStream(Encoding.UTF8.GetBytes(xml)), "hostile.gpx").Single();

    private static GpxParseResult ParseTcx(string xml) =>
        new TcxParsingService(NullLogger<TcxParsingService>.Instance)
            .Parse(new MemoryStream(Encoding.UTF8.GetBytes(xml)), "hostile.tcx").Single();

    private static IReadOnlyList<GpxParseResult> ParseFitAll(byte[] bytes, string fileName = "hostile.fit") =>
        new FitParsingService(NullLogger<FitParsingService>.Instance)
            .Parse(new MemoryStream(bytes), fileName);

    /// <summary>Encodes a valid Activity FIT with one Record per supplied latitude (raw semicircles),
    /// each at a fixed valid longitude — so a test can place points exactly on / one step past the
    /// ±90° latitude boundary and see which the parser keeps.</summary>
    private static byte[] EncodeFitWithLatSemicircles(int[] latSemicircles, DateTime startUtc)
    {
        using var ms = new MemoryStream();
        var encode = new Encode(ProtocolVersion.V20);
        encode.Open(ms);

        var fileId = new FileIdMesg();
        fileId.SetType(Dynastream.Fit.File.Activity);
        fileId.SetManufacturer(Manufacturer.Garmin);
        fileId.SetTimeCreated(new Dynastream.Fit.DateTime(startUtc));
        encode.Write(fileId);

        for (int i = 0; i < latSemicircles.Length; i++)
        {
            var rec = new RecordMesg();
            rec.SetTimestamp(new Dynastream.Fit.DateTime(startUtc.AddSeconds(i)));
            rec.SetPositionLat(latSemicircles[i]);
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

    // ── GPX: coordinate boundary inclusivity ─────────────────────────────────────

    [Fact]
    public void Gpx_CoordinatesExactlyAtPoles_Accepted()
    {
        // GpxParsingService rejects `lat < -90 || lat > 90` (and the lon mirror), so ±90 / ±180 are
        // INCLUSIVE. A file sitting exactly on the boundary must import, not be dropped as invalid.
        var xml = "<gpx xmlns=\"http://www.topografix.com/GPX/1/1\"><trk><trkseg>" +
                  "<trkpt lat=\"90\" lon=\"180\"/>" +
                  "<trkpt lat=\"-90\" lon=\"-180\"/>" +
                  "</trkseg></trk></gpx>";
        var r = ParseGpx(xml);

        Assert.True(r.Success, r.ErrorMessage);
        Assert.Equal(2, r.GpsPoints.Count);
        Assert.Equal(90, r.GpsPoints[0].Lat);
        Assert.Equal(180, r.GpsPoints[0].Lng);
        Assert.Equal(-90, r.GpsPoints[1].Lat);
        Assert.Equal(-180, r.GpsPoints[1].Lng);
    }

    [Fact]
    public void Gpx_CoordinatesJustPastPoles_SkippedNotClamped()
    {
        // One step past each pole must be SKIPPED, never clamped onto the boundary — if the parser
        // clamped, a survivor would sit at exactly 90/180. The two in-range points survive and no
        // survivor is a pole, proving skip-not-clamp.
        var xml = "<gpx xmlns=\"http://www.topografix.com/GPX/1/1\"><trk><trkseg>" +
                  "<trkpt lat=\"90.0001\" lon=\"0\"/>" +
                  "<trkpt lat=\"0\" lon=\"180.0001\"/>" +
                  "<trkpt lat=\"-90.0001\" lon=\"0\"/>" +
                  "<trkpt lat=\"0\" lon=\"-180.0001\"/>" +
                  "<trkpt lat=\"45\" lon=\"45\"/>" +
                  "<trkpt lat=\"46\" lon=\"46\"/>" +
                  "</trkseg></trk></gpx>";
        var r = ParseGpx(xml);

        Assert.True(r.Success, r.ErrorMessage);
        Assert.Equal(2, r.GpsPoints.Count);
        Assert.All(r.GpsPoints, p =>
        {
            Assert.InRange(p.Lat, -90, 90);
            Assert.InRange(p.Lng, -180, 180);
            Assert.True(Math.Abs(p.Lat) < 90 && Math.Abs(p.Lng) < 180, "a skipped pole was clamped in");
        });
    }

    // ── TCX: coordinate boundary inclusivity ─────────────────────────────────────

    [Fact]
    public void Tcx_CoordinatesExactlyAtPoles_Accepted()
    {
        // TcxParsingService keeps a point when `lat is (>= -90 and <= 90)` and the lon mirror — also
        // INCLUSIVE. Pins the same boundary the GPX test pins, on the other coordinate-range parser.
        var xml =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
            "<TrainingCenterDatabase xmlns=\"http://www.garmin.com/xmlschemas/TrainingCenterDatabase/v2\">" +
            "<Activities><Activity Sport=\"Running\"><Id>2026-02-02T09:00:00Z</Id>" +
            "<Lap StartTime=\"2026-02-02T09:00:00Z\"><Track>" +
            "<Trackpoint><Time>2026-02-02T09:00:00Z</Time><Position>" +
            "<LatitudeDegrees>90</LatitudeDegrees><LongitudeDegrees>180</LongitudeDegrees></Position></Trackpoint>" +
            "<Trackpoint><Time>2026-02-02T09:00:10Z</Time><Position>" +
            "<LatitudeDegrees>-90</LatitudeDegrees><LongitudeDegrees>-180</LongitudeDegrees></Position></Trackpoint>" +
            "</Track></Lap></Activity></Activities></TrainingCenterDatabase>";
        var r = ParseTcx(xml);

        Assert.True(r.Success, r.ErrorMessage);
        Assert.Equal(2, r.GpsPoints.Count);
        Assert.Equal(90, r.GpsPoints[0].Lat);
        Assert.Equal(180, r.GpsPoints[0].Lng);
        Assert.Equal(-90, r.GpsPoints[1].Lat);
        Assert.Equal(-180, r.GpsPoints[1].Lng);
    }

    [Fact]
    public void Tcx_CoordinatesJustPastPoles_Skipped()
    {
        // The out-of-range trackpoints are dropped (indoor-sample contract) and the two in-range
        // points survive — never clamped onto ±90 / ±180.
        var xml =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
            "<TrainingCenterDatabase xmlns=\"http://www.garmin.com/xmlschemas/TrainingCenterDatabase/v2\">" +
            "<Activities><Activity Sport=\"Running\"><Id>2026-02-02T09:00:00Z</Id>" +
            "<Lap StartTime=\"2026-02-02T09:00:00Z\"><Track>" +
            "<Trackpoint><Time>2026-02-02T09:00:00Z</Time><Position>" +
            "<LatitudeDegrees>90.0001</LatitudeDegrees><LongitudeDegrees>8.20</LongitudeDegrees></Position></Trackpoint>" +
            "<Trackpoint><Time>2026-02-02T09:00:01Z</Time><Position>" +
            "<LatitudeDegrees>8.20</LatitudeDegrees><LongitudeDegrees>180.0001</LongitudeDegrees></Position></Trackpoint>" +
            "<Trackpoint><Time>2026-02-02T09:00:02Z</Time><Position>" +
            "<LatitudeDegrees>47.20</LatitudeDegrees><LongitudeDegrees>8.20</LongitudeDegrees></Position></Trackpoint>" +
            "<Trackpoint><Time>2026-02-02T09:00:03Z</Time><Position>" +
            "<LatitudeDegrees>47.21</LatitudeDegrees><LongitudeDegrees>8.21</LongitudeDegrees></Position></Trackpoint>" +
            "</Track></Lap></Activity></Activities></TrainingCenterDatabase>";
        var r = ParseTcx(xml);

        Assert.True(r.Success, r.ErrorMessage);
        Assert.Equal(2, r.GpsPoints.Count);
        Assert.All(r.GpsPoints, p =>
        {
            Assert.True(Math.Abs(p.Lat) < 90 && Math.Abs(p.Lng) < 180, "a skipped pole was clamped in");
        });
    }

    // ── FIT: semicircle boundary inclusivity ─────────────────────────────────────

    [Fact]
    public void Fit_PositionAtExactPoles_AcceptedOneStepPastSkipped()
    {
        // FitParsingService decodes semicircles → degrees, then rejects `lat < -90 || lat > 90`.
        // Exactly ±90° (2^31 / 2 semicircles) is inclusive; one semicircle past +90° decodes to
        // 90.0000000838° (> 90) and is dropped. Three records → only the two exact poles survive,
        // proving the boundary is inclusive AND that the just-past point is skipped, not clamped.
        var lats = new[]
        {
            NinetyDegLatSemicircles,       // +90.0° exactly → kept
            NinetyDegLatSemicircles + 1,   // +90.0000000838° → dropped
            -NinetyDegLatSemicircles,      // -90.0° exactly → kept
        };
        var r = ParseFitAll(
            EncodeFitWithLatSemicircles(lats, new DateTime(2026, 5, 1, 6, 0, 0, DateTimeKind.Utc))).Single();

        Assert.True(r.Success, r.ErrorMessage);
        Assert.Equal(2, r.GpsPoints.Count);
        Assert.Equal(90, r.GpsPoints[0].Lat, 6);
        Assert.Equal(-90, r.GpsPoints[1].Lat, 6);
    }

    // ── Router: per-result GuardResult isolation across a multi-result parse ──────

    [Fact]
    public void Router_MultiActivityTcx_PoisonedSiblingFailsAlone_CleanOneSurvives()
    {
        // The router guards each result INDEPENDENTLY (`results.Select(GuardResult)`), so a corrupt
        // activity in a multi-activity file must fail on its own without sinking its clean siblings —
        // the code comment claims this ("fails only the offending activity, not its chained siblings")
        // but nothing exercised it. Here the March activity carries a physically-impossible descent
        // (4e8 m via a huge altitude drop) that trips the router's out-of-range sweep, while the
        // February activity is clean. Expected: two results, the first imported, the second rejected.
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
            "<LatitudeDegrees>48.50</LatitudeDegrees><LongitudeDegrees>9.50</LongitudeDegrees></Position>" +
            "<AltitudeMeters>400000000</AltitudeMeters></Trackpoint>" +
            "<Trackpoint><Time>2026-03-03T10:00:10Z</Time><Position>" +
            "<LatitudeDegrees>48.51</LatitudeDegrees><LongitudeDegrees>9.51</LongitudeDegrees></Position>" +
            "<AltitudeMeters>0</AltitudeMeters></Trackpoint>" +
            "</Track></Lap></Activity>" +
            "</Activities></TrainingCenterDatabase>";

        var results = RouteAll(xml, "bundle.tcx");

        Assert.Equal(2, results.Count);

        // February: clean, imported.
        Assert.True(results[0].Success, results[0].ErrorMessage);
        Assert.Equal(2, results[0].Summary!.StartDateUtc!.Value.Month);
        Assert.Equal(2, results[0].GpsPoints.Count);

        // March: rejected alone by the range sweep — its failure did not corrupt February.
        Assert.False(results[1].Success);
        Assert.Contains("TotalDescent", results[1].ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("range", results[1].ErrorMessage, StringComparison.Ordinal);
        Assert.Empty(results[1].GpsPoints);
    }

    // ── Sniffer: no-BOM UTF-16 big-endian branch ─────────────────────────────────

    [Fact]
    public void Detect_NoBomUtf16BigEndian_DetectsGpx()
    {
        // The earlier waves exercised the UTF-16 LE no-BOM heuristic and the BE-with-BOM path; the BE
        // no-BOM branch of TryDecodeUtf16 (high byte at EVEN offsets == 0x00) was never hit directly.
        // BigEndianUnicode without its preamble gives 0x00,0x3C,... for "<gpx" — the heuristic must
        // recognise it and the sniffer must decode + route to GPX.
        var bytes = Encoding.BigEndianUnicode.GetBytes("<gpx xmlns=\"http://www.topografix.com/GPX/1/1\"><trk></trk></gpx>");
        Assert.Equal(0x00, bytes[0]); // guard: this really is the no-BOM BE shape the branch keys on

        Assert.Equal(TrackFileFormat.Gpx, TrackFileSniffer.Detect(bytes));
    }

    // ── TCX: structural DoS parity + degenerate name ─────────────────────────────

    [Fact]
    public void Tcx_DeeplyNestedElements_HandledWithoutCrash()
    {
        // Parity with Gpx_DeeplyNestedElements_HandledWithoutCrash on the other XML parser: 2000-deep
        // junk inside <Track> must neither stack-overflow nor hang. The <x> elements are not
        // Trackpoints, so a lap is found but yields no valid points — a clean rejection.
        var xml =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
            "<TrainingCenterDatabase xmlns=\"http://www.garmin.com/xmlschemas/TrainingCenterDatabase/v2\">" +
            "<Activities><Activity Sport=\"Running\"><Id>2026-02-02T09:00:00Z</Id>" +
            "<Lap StartTime=\"2026-02-02T09:00:00Z\"><Track>" +
            string.Concat(Enumerable.Repeat("<x>", 2000)) +
            string.Concat(Enumerable.Repeat("</x>", 2000)) +
            "</Track></Lap></Activity></Activities></TrainingCenterDatabase>";
        var r = ParseTcx(xml);

        Assert.False(r.Success);
        Assert.Equal("TCX file contains no valid track points.", r.ErrorMessage);
        Assert.Empty(r.GpsPoints);
    }

    [Fact]
    public void Tcx_FilenameSanitizesToNothing_StillImportsWithNullName()
    {
        // TCX has no in-file name, so the activity name is always the upload filename. A filename whose
        // base name is ENTIRELY control characters reduces to null under ImportNameSanitizer (all chars
        // stripped, then null on empty). The import must still succeed — a name is not required — with a
        // null name rather than an empty string or a crash. Built with raw (char)1..3 so the source
        // stays escape-clean. (The 07-22 tests pinned the CAP; this pins the total-erasure corner.)
        var fileName = new string(new[] { (char)1, (char)2, (char)3 }) + ".tcx";
        var r = RouteText(
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
            "<TrainingCenterDatabase xmlns=\"http://www.garmin.com/xmlschemas/TrainingCenterDatabase/v2\">" +
            "<Activities><Activity Sport=\"Running\"><Id>2026-02-02T09:00:00Z</Id>" +
            "<Lap StartTime=\"2026-02-02T09:00:00Z\"><Track>" +
            "<Trackpoint><Time>2026-02-02T09:00:00Z</Time><Position>" +
            "<LatitudeDegrees>47.20</LatitudeDegrees><LongitudeDegrees>8.20</LongitudeDegrees></Position></Trackpoint>" +
            "<Trackpoint><Time>2026-02-02T09:00:10Z</Time><Position>" +
            "<LatitudeDegrees>47.21</LatitudeDegrees><LongitudeDegrees>8.21</LongitudeDegrees></Position></Trackpoint>" +
            "</Track></Lap></Activity></Activities></TrainingCenterDatabase>", fileName);

        Assert.True(r.Success, r.ErrorMessage);
        Assert.Null(r.Summary!.Name);
        Assert.Equal(2, r.GpsPoints.Count);
    }
}
