using System.Globalization;
using System.Text;
using Dynastream.Fit;
using Microsoft.Extensions.Logging.Abstractions;
using RouteBaseWeb.Models.Gpx;
using RouteBaseWeb.Services;
using Xunit;
using DateTime = System.DateTime;

namespace RouteBaseWeb.Tests.Services;

/// <summary>
/// Hostile-input suite for the file-import trust boundary (import fuzzing 2026-07-17;
/// extended 2026-07-20 and 2026-07-22): sniffer + router + upload policy + all three parsers
/// against attacker-shaped files. Every test pins the rejection CONTRACT — a clean
/// <c>Success=false</c> with an error message (which the orchestrator counts as "failed" and never
/// persists), never an unhandled crash, never a partial parse. Tests marked "pins current
/// behavior" document a known oddity on purpose — read the test's own comment before
/// changing them.
/// </summary>
public class ImportHostileInputTests
{
    private const double SemicirclesPerDegree = 2147483648.0 / 180.0;

    private const string MinimalGpx =
        "<gpx xmlns=\"http://www.topografix.com/GPX/1/1\"><trk><name>T</name><type>ride</type><trkseg>" +
        "<trkpt lat=\"47.0\" lon=\"11.0\"><ele>500</ele><time>2026-01-01T08:00:00Z</time></trkpt>" +
        "<trkpt lat=\"47.001\" lon=\"11.001\"><ele>505</ele><time>2026-01-01T08:00:10Z</time></trkpt>" +
        "</trkseg></trk></gpx>";

    private const string MinimalTcx =
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
        "<TrainingCenterDatabase xmlns=\"http://www.garmin.com/xmlschemas/TrainingCenterDatabase/v2\">" +
        "<Activities><Activity Sport=\"Running\"><Id>2026-02-02T09:00:00Z</Id>" +
        "<Lap StartTime=\"2026-02-02T09:00:00Z\"><Track>" +
        "<Trackpoint><Time>2026-02-02T09:00:00Z</Time><Position>" +
        "<LatitudeDegrees>47.20</LatitudeDegrees><LongitudeDegrees>8.20</LongitudeDegrees></Position></Trackpoint>" +
        "<Trackpoint><Time>2026-02-02T09:00:10Z</Time><Position>" +
        "<LatitudeDegrees>47.21</LatitudeDegrees><LongitudeDegrees>8.21</LongitudeDegrees></Position></Trackpoint>" +
        "</Track></Lap></Activity></Activities></TrainingCenterDatabase>";

    private static TrackFileParserRouter RealRouter() => new(
        new GpxParsingService(NullLogger<GpxParsingService>.Instance),
        new TcxParsingService(NullLogger<TcxParsingService>.Instance),
        new FitParsingService(NullLogger<FitParsingService>.Instance));

    // These helpers cover single-activity files → one result; unwrap the list contract. A chained FIT
    // (>1 result) is exercised via ParseFitAll below.
    private static GpxParseResult RouteBytes(byte[] bytes, string fileName) =>
        RealRouter().Parse(new MemoryStream(bytes), fileName).Single();

    private static GpxParseResult RouteText(string content, string fileName) =>
        RouteBytes(Encoding.UTF8.GetBytes(content), fileName);

    private static GpxParseResult ParseGpx(string xml) =>
        new GpxParsingService(NullLogger<GpxParsingService>.Instance)
            .Parse(new MemoryStream(Encoding.UTF8.GetBytes(xml)), "hostile.gpx").Single();

    private static GpxParseResult ParseTcx(string xml) =>
        new TcxParsingService(NullLogger<TcxParsingService>.Instance)
            .Parse(new MemoryStream(Encoding.UTF8.GetBytes(xml)), "hostile.tcx").Single();

    private static IReadOnlyList<GpxParseResult> ParseFitAll(byte[] bytes, string fileName = "hostile.fit") =>
        new FitParsingService(NullLogger<FitParsingService>.Instance)
            .Parse(new MemoryStream(bytes), fileName);

    private static GpxParseResult ParseFit(byte[] bytes) => ParseFitAll(bytes).Single();

    /// <summary>Minimal valid Activity FIT (FileId + records + one session) via the SDK encoder.
    /// <paramref name="totalDistance"/>, when set, stamps the session's device total so per-file totals
    /// are distinguishable across a chain.</summary>
    private static byte[] EncodeFitRide(int recordCount, DateTime startUtc, float? totalDistance = null)
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
        if (totalDistance.HasValue) session.SetTotalDistance(totalDistance.Value);
        encode.Write(session);

        encode.Close();
        return ms.ToArray();
    }

    // ── Router / sniffer: degenerate and mislabeled files ───────────────────────

    [Fact]
    public void Router_EmptyFile_RejectedAsUnrecognized()
    {
        var r = RouteBytes(Array.Empty<byte>(), "empty.gpx");
        Assert.False(r.Success);
        Assert.Equal("Unrecognized file format.", r.ErrorMessage);
    }

    [Fact]
    public void Router_WhitespaceOnlyFile_RejectedAsUnrecognized()
    {
        var r = RouteText("   \r\n\t  \n", "blank.gpx");
        Assert.False(r.Success);
        Assert.Equal("Unrecognized file format.", r.ErrorMessage);
    }

    [Fact]
    public void Detect_HeaderShorterThanFitHeader_Unknown()
    {
        // 8 bytes — too short to carry the ".FIT" signature at offset 8.
        Assert.Equal(TrackFileFormat.Unknown,
            TrackFileSniffer.Detect(new byte[] { 0x0C, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 }));
    }

    [Fact]
    public void Router_FitBytesNamedGpx_ParsedAsFit()
    {
        // Wrong-extension attack/mistake: content sniff must win over the .gpx name.
        var fit = EncodeFitRide(3, new DateTime(2026, 5, 1, 6, 0, 0, DateTimeKind.Utc));
        var r = RouteBytes(fit, "mislabeled.gpx");

        Assert.True(r.Success, r.ErrorMessage);
        Assert.Equal("fit", r.Summary!.Source);
        Assert.Equal(3, r.GpsPoints.Count);
    }

    [Fact]
    public void Router_GpxBytesNamedFit_ParsedAsGpx()
    {
        var r = RouteText(MinimalGpx, "mislabeled.fit");

        Assert.True(r.Success, r.ErrorMessage);
        Assert.Equal("gpx", r.Summary!.Source);
        Assert.Equal(2, r.GpsPoints.Count);
    }

    [Fact]
    public void Router_NonSeekableStream_BufferedAndParsed()
    {
        var inner = new MemoryStream(Encoding.UTF8.GetBytes(MinimalGpx));
        var r = RealRouter().Parse(new NonSeekableStream(inner), "forward-only.gpx").Single();

        Assert.True(r.Success, r.ErrorMessage);
        Assert.Equal(2, r.GpsPoints.Count);
    }

    [Fact]
    public void Router_RootElementBeyondOldSniffWindow_NowDetected()
    {
        // GPX-P3 (2026-07-17): widened SniffBytes/HeaderBytes (512 → 4096) so a root element sitting
        // past a long comment/prolog is still caught — this padding cleared the old 512-byte window.
        var padded = "<!-- " + new string('x', 600) + " -->" + MinimalGpx;
        var r = RouteText(padded, "padded.gpx");

        Assert.True(r.Success, r.ErrorMessage);
        Assert.Equal("gpx", r.Summary!.Source);
        Assert.Equal(2, r.GpsPoints.Count);
    }

    [Fact]
    public void Router_Utf16EncodedGpx_NowDetected()
    {
        // GPX-P3 (2026-07-17): the sniffer now decodes UTF-16 (BOM'd or, via the XML 1.0 Appendix F
        // null-byte-alternation heuristic, BOM-less) so UTF-16-encoded GPX is routed correctly.
        var utf16 = Encoding.Unicode.GetBytes("<?xml version=\"1.0\"?>" + MinimalGpx);
        var r = RouteBytes(utf16, "utf16.gpx");

        Assert.True(r.Success, r.ErrorMessage);
        Assert.Equal("gpx", r.Summary!.Source);
        Assert.Equal(2, r.GpsPoints.Count);
    }

    [Fact]
    public void Router_SniffPassingGarbage_RejectedByGpxParserCleanly()
    {
        // Crafted to pass the sniff ("<gpx" in the head) but be undecodable XML — the format
        // parser is the second line of defense and must fail closed, not crash.
        var garbage = Encoding.UTF8.GetBytes("<gpx ").Concat(new byte[] { 0x00, 0xFF, 0xFE, 0x01, 0x9C, 0x00 }).ToArray();
        var r = RouteBytes(garbage, "garbage.gpx");

        Assert.False(r.Success);
        Assert.StartsWith("Invalid XML", r.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Router_TcxBytesNamedGpx_ParsedAsTcx()
    {
        // Third wrong-extension combination (FIT↔GPX are pinned above): content sniff wins.
        var r = RouteText(MinimalTcx, "mislabeled.gpx");

        Assert.True(r.Success, r.ErrorMessage);
        Assert.Equal("tcx", r.Summary!.Source);
        Assert.Equal(2, r.GpsPoints.Count);
    }

    [Fact]
    public void Router_GpxWithTcxRootStringInComment_MisroutedToTcx_CleanlyRejected()
    {
        // Pins current behavior (OBS-1, import fuzzing 2026-07-20). The sniffer
        // scans the decoded head for "<TrainingCenterDatabase" BEFORE "<gpx", so a valid GPX whose
        // leading comment mentions the TCX root element is routed to the TCX parser and rejected
        // (false negative, confusing message — but clean: no crash, nothing persists).
        var content = "<!-- converted from a <TrainingCenterDatabase export -->" + MinimalGpx;
        var r = RouteText(content, "commented.gpx");

        Assert.False(r.Success);
        Assert.Equal("No activity found in TCX file.", r.ErrorMessage);
        Assert.Empty(r.GpsPoints);
    }

    [Fact]
    public void Router_Utf16BigEndianBomGpx_Detected()
    {
        // Big-endian sibling of the UTF-16 LE case above — pins TryDecodeUtf16's BE branch
        // end-to-end (the sniffer decodes the BOM'd head; XmlReader handles the BOM itself).
        var enc = Encoding.BigEndianUnicode;
        var bytes = enc.GetPreamble().Concat(enc.GetBytes("<?xml version=\"1.0\"?>" + MinimalGpx)).ToArray();
        var r = RouteBytes(bytes, "utf16be.gpx");

        Assert.True(r.Success, r.ErrorMessage);
        Assert.Equal("gpx", r.Summary!.Source);
        Assert.Equal(2, r.GpsPoints.Count);
    }

    [Fact]
    public void Router_RootElementBeyondSniffWindow_RejectedAsUnrecognized()
    {
        // Pins the sniff window's failure MODE (2026-07-22 import-fuzz): a root element past the
        // 4096-byte head — here behind a 5000-char comment — is not detected, and the file is
        // cleanly rejected as unrecognized rather than routed to a guessed parser. The bound
        // itself is a deliberate DoS guard; only the rejection contract is pinned.
        var padded = "<!-- " + new string('x', 5000) + " -->" + MinimalGpx;
        var r = RouteText(padded, "deep-root.gpx");

        Assert.False(r.Success);
        Assert.Equal("Unrecognized file format.", r.ErrorMessage);
    }

    [Fact]
    public void Router_FitSignatureInsideXmlComment_FitPrecedenceCleanlyRejected()
    {
        // ".FIT" landing exactly at bytes 8–11 inside an XML comment: the FIT magic-byte check
        // runs before the XML scan, so this valid GPX is routed to the FIT decoder and cleanly
        // rejected — pins the precedence and the fail-closed outcome (no crash, no points).
        var r = RouteText("<!--1234.FIT-->" + MinimalGpx, "fit-in-comment.gpx");

        Assert.False(r.Success);
        Assert.NotNull(r.ErrorMessage);
        Assert.Empty(r.GpsPoints);
    }

    // ── GPX parser: malformed / truncated / expanding XML ───────────────────────

    [Fact]
    public void Gpx_EmptyStream_InvalidXml()
    {
        var r = ParseGpx("");
        Assert.False(r.Success);
        Assert.StartsWith("Invalid XML", r.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Gpx_TruncatedMidPoint_WholeFileRejected_NoPartialParse()
    {
        // 50 valid points, then the file ends mid-element (upload cut off / disk corruption).
        // XDocument loads all-or-nothing, so not even the 50 intact points survive — no partial import.
        var sb = new StringBuilder("<gpx xmlns=\"http://www.topografix.com/GPX/1/1\"><trk><trkseg>");
        for (int i = 0; i < 50; i++)
            sb.Append(CultureInfo.InvariantCulture, $"<trkpt lat=\"47.{i:D2}\" lon=\"11.0\"><ele>500</ele></trkpt>");
        var whole = sb.Append("</trkseg></trk></gpx>").ToString();
        var truncated = whole[..(whole.Length * 6 / 10)];

        var r = ParseGpx(truncated);

        Assert.False(r.Success);
        Assert.StartsWith("Invalid XML", r.ErrorMessage, StringComparison.Ordinal);
        Assert.Empty(r.GpsPoints);
    }

    [Fact]
    public void Gpx_BillionLaughsEntityExpansion_Rejected()
    {
        // DtdProcessing.Prohibit fails at the DOCTYPE token, before any expansion can start.
        var xml = "<?xml version=\"1.0\"?><!DOCTYPE gpx [" +
                  "<!ENTITY a \"aaaaaaaaaaaaaaaaaaaa\">" +
                  "<!ENTITY b \"&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;\">" +
                  "<!ENTITY c \"&b;&b;&b;&b;&b;&b;&b;&b;&b;&b;\">]>" +
                  "<gpx xmlns=\"http://www.topografix.com/GPX/1/1\"><trk><name>&c;</name><trkseg>" +
                  "<trkpt lat=\"47.0\" lon=\"11.0\"/></trkseg></trk></gpx>";
        var r = ParseGpx(xml);

        Assert.False(r.Success);
        Assert.StartsWith("Invalid XML", r.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Gpx_ExternalDtdReference_Rejected()
    {
        // XXE via external DTD: prohibited DTD processing + null resolver — must fail closed
        // without attempting any fetch.
        var xml = "<?xml version=\"1.0\"?><!DOCTYPE gpx SYSTEM \"http://127.0.0.1:9/evil.dtd\">" + MinimalGpx;
        var r = ParseGpx(xml);

        Assert.False(r.Success);
        Assert.StartsWith("Invalid XML", r.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Gpx_DeeplyNestedElements_HandledWithoutCrash()
    {
        // 2000-deep nesting inside <trkseg> — must neither stack-overflow nor hang; the junk
        // elements are simply not trackpoints, so the parser reports no valid points.
        var xml = "<gpx xmlns=\"http://www.topografix.com/GPX/1/1\"><trk><trkseg>" +
                  string.Concat(Enumerable.Repeat("<x>", 2000)) +
                  string.Concat(Enumerable.Repeat("</x>", 2000)) +
                  "</trkseg></trk></gpx>";
        var r = ParseGpx(xml);

        Assert.False(r.Success);
        Assert.Equal("GPX file contains no valid track points.", r.ErrorMessage);
    }

    [Fact]
    public void Gpx_HugeTrackName_CappedAndSanitized()
    {
        // GPX-P3 (2026-07-17): <name> is attacker-controlled and is now capped/sanitized the same
        // way <type> already was — a 200k-char name is truncated to the 200-char app-wide limit.
        var xml = "<gpx xmlns=\"http://www.topografix.com/GPX/1/1\"><trk><name>" +
                  new string('x', 200_000) +
                  "</name><trkseg>" +
                  "<trkpt lat=\"47.0\" lon=\"11.0\"><ele>500</ele></trkpt>" +
                  "<trkpt lat=\"47.1\" lon=\"11.1\"><ele>510</ele></trkpt>" +
                  "</trkseg></trk></gpx>";
        var r = ParseGpx(xml);

        Assert.True(r.Success, r.ErrorMessage);
        Assert.Equal(200, r.Summary!.Name!.Length);
    }

    [Fact]
    public void Gpx_NameWithControlCharsAndWhitespace_StrippedAndTrimmed()
    {
        // Parity with <type>'s SanitizeType: chars < 0x20 are stripped (tab/newline via numeric
        // character refs, since raw XML text disallows most other control chars), result trimmed.
        var xml = "<gpx xmlns=\"http://www.topografix.com/GPX/1/1\"><trk><name>&#9;Solo&#9;Trip&#10;</name><trkseg>" +
                  "<trkpt lat=\"47.0\" lon=\"11.0\"><ele>500</ele></trkpt>" +
                  "<trkpt lat=\"47.1\" lon=\"11.1\"><ele>510</ele></trkpt>" +
                  "</trkseg></trk></gpx>";
        var r = ParseGpx(xml);

        Assert.True(r.Success, r.ErrorMessage);
        Assert.Equal("SoloTrip", r.Summary!.Name);
    }

    [Fact]
    public void Gpx_50kPoints_ParsesFullyWithinCap()
    {
        // Large-but-legitimate volume (bounded well under MaxPoints): all points survive, no
        // truncation warning — pins that the new 1M cap (GPX-P3) doesn't bite normal rides,
        // mirroring Fit_50kRecords_ParsesFullyWithinCap.
        var sb = new StringBuilder("<gpx xmlns=\"http://www.topografix.com/GPX/1/1\"><trk><trkseg>");
        for (int i = 0; i < 50_000; i++)
            sb.Append(CultureInfo.InvariantCulture, $"<trkpt lat=\"47.{i % 100:D2}\" lon=\"11.0\"><ele>500</ele></trkpt>");
        sb.Append("</trkseg></trk></gpx>");

        var r = ParseGpx(sb.ToString());

        Assert.True(r.Success, r.ErrorMessage);
        Assert.Equal(50_000, r.GpsPoints.Count);
        Assert.DoesNotContain(r.Warnings, w => w.Contains("truncated", StringComparison.Ordinal));
    }

    [Fact]
    public void Gpx_NonFiniteAndOutOfRangeCoordinates_SkippedPointwise()
    {
        // Hostile lat/lon values (NaN, Infinity literal, overflow-to-Inf exponent, out-of-range,
        // missing attribute) are skipped point-by-point; the two sane points survive. A NaN <ele>
        // fails ParseDouble's finite check and becomes a null altitude, not a poisoned value.
        var xml = "<gpx xmlns=\"http://www.topografix.com/GPX/1/1\"><trk><trkseg>" +
                  "<trkpt lat=\"NaN\" lon=\"11.0\"/>" +
                  "<trkpt lat=\"Infinity\" lon=\"11.0\"/>" +
                  "<trkpt lat=\"1e99999\" lon=\"11.0\"/>" +
                  "<trkpt lat=\"95\" lon=\"11.0\"/>" +
                  "<trkpt lat=\"47.0\" lon=\"-200\"/>" +
                  "<trkpt lon=\"11.0\"/>" +
                  "<trkpt lat=\"47.0\" lon=\"11.0\"><ele>NaN</ele></trkpt>" +
                  "<trkpt lat=\"47.001\" lon=\"11.001\"/>" +
                  "</trkseg></trk></gpx>";
        var r = ParseGpx(xml);

        Assert.True(r.Success, r.ErrorMessage);
        Assert.Equal(2, r.GpsPoints.Count);
        Assert.Null(r.GpsPoints[0].Altitude);
        Assert.All(r.GpsPoints, p =>
        {
            Assert.InRange(p.Lat, -90, 90);
            Assert.InRange(p.Lng, -180, 180);
        });
    }

    [Fact]
    public void Gpx_AllCoordinatesInvalid_RejectedAsNoValidPoints()
    {
        var xml = "<gpx xmlns=\"http://www.topografix.com/GPX/1/1\"><trk><trkseg>" +
                  "<trkpt lat=\"NaN\" lon=\"11.0\"/>" +
                  "<trkpt lat=\"91\" lon=\"11.0\"/>" +
                  "</trkseg></trk></gpx>";
        var r = ParseGpx(xml);

        Assert.False(r.Success);
        Assert.Equal("GPX file contains no valid track points.", r.ErrorMessage);
        Assert.Empty(r.GpsPoints);
    }

    [Fact]
    public void Gpx_OppositeExtremeAltitudes_RejectedNonFiniteDescent()
    {
        // A1 (2026-07-20): rewrites the former "pins current behavior" BUG-1 pin.
        // Each <ele> passes ParseDouble's per-value finite check, but ComputeElevation sums raw
        // deltas: +MaxValue → -MaxValue overflows the descent total to +Inf. The router's
        // aggregate sweep now rejects the file like any other parse failure, naming the stat.
        const string max = "1.7976931348623157E308";
        var xml = "<gpx xmlns=\"http://www.topografix.com/GPX/1/1\"><trk><trkseg>" +
                  $"<trkpt lat=\"47.0\" lon=\"11.0\"><ele>{max}</ele></trkpt>" +
                  $"<trkpt lat=\"47.001\" lon=\"11.001\"><ele>-{max}</ele></trkpt>" +
                  "</trkseg></trk></gpx>";
        var r = RouteText(xml, "hostile.gpx");

        Assert.False(r.Success);
        Assert.Contains("TotalDescent", r.ErrorMessage, StringComparison.Ordinal);
        Assert.Empty(r.GpsPoints);
    }

    [Fact]
    public void Gpx_ExtremeHeartRateValues_RejectedNonFiniteAverage()
    {
        // A1 (2026-07-20): rewrites the former BUG-1 pin. Per-point HR passes the
        // finite check, but the average's SUM overflows to +Inf across two MaxValue samples —
        // the router's aggregate sweep rejects the file instead of letting the Infinity persist.
        const string max = "1.7976931348623157E308";
        var xml = "<gpx xmlns=\"http://www.topografix.com/GPX/1/1\"" +
                  " xmlns:gpxtpx=\"http://www.garmin.com/xmlschemas/TrackPointExtension/v1\">" +
                  "<trk><trkseg>" +
                  $"<trkpt lat=\"47.0\" lon=\"11.0\"><extensions><gpxtpx:TrackPointExtension>" +
                  $"<gpxtpx:hr>{max}</gpxtpx:hr></gpxtpx:TrackPointExtension></extensions></trkpt>" +
                  $"<trkpt lat=\"47.001\" lon=\"11.001\"><extensions><gpxtpx:TrackPointExtension>" +
                  $"<gpxtpx:hr>{max}</gpxtpx:hr></gpxtpx:TrackPointExtension></extensions></trkpt>" +
                  "</trkseg></trk></gpx>";
        var r = RouteText(xml, "hostile.gpx");

        Assert.False(r.Success);
        Assert.Contains("AverageHeartRate", r.ErrorMessage, StringComparison.Ordinal);
        Assert.Empty(r.GpsPoints);
    }

    [Fact]
    public void Gpx_SaneElevationAndHeartRate_NotRejectedByAggregateSweep()
    {
        // Happy-path guard for the A1 sweep: a normal ride (finite gain/descent/HR) must pass the
        // router's non-finite-aggregate check unchanged — no false rejection.
        var xml = "<gpx xmlns=\"http://www.topografix.com/GPX/1/1\"" +
                  " xmlns:gpxtpx=\"http://www.garmin.com/xmlschemas/TrackPointExtension/v1\">" +
                  "<trk><trkseg>" +
                  "<trkpt lat=\"47.0\" lon=\"11.0\"><ele>500</ele><time>2026-01-01T08:00:00Z</time>" +
                  "<extensions><gpxtpx:TrackPointExtension><gpxtpx:hr>120</gpxtpx:hr></gpxtpx:TrackPointExtension></extensions></trkpt>" +
                  "<trkpt lat=\"47.001\" lon=\"11.001\"><ele>510</ele><time>2026-01-01T08:00:10Z</time>" +
                  "<extensions><gpxtpx:TrackPointExtension><gpxtpx:hr>130</gpxtpx:hr></gpxtpx:TrackPointExtension></extensions></trkpt>" +
                  "<trkpt lat=\"47.002\" lon=\"11.002\"><ele>505</ele><time>2026-01-01T08:00:20Z</time>" +
                  "<extensions><gpxtpx:TrackPointExtension><gpxtpx:hr>125</gpxtpx:hr></gpxtpx:TrackPointExtension></extensions></trkpt>" +
                  "</trkseg></trk></gpx>";
        var r = RouteText(xml, "ride.gpx");

        Assert.True(r.Success, r.ErrorMessage);
        Assert.Equal(3, r.GpsPoints.Count);
        Assert.Equal(10.0, r.Summary!.ElevationGain);
        Assert.Equal(5.0, r.Summary.TotalDescent);
        Assert.Equal(125.0, r.Summary.AverageHeartRate);
    }

    [Fact]
    public void Gpx_DeclaredEncodingLiesUtf16_RejectedCleanly()
    {
        // Encoding lie: UTF-8 bytes whose declaration claims UTF-16. The sniffer decodes the head
        // as UTF-8 and routes to GPX; XmlReader then hits the declaration/stream mismatch and
        // fails — clean "Invalid XML", no crash.
        var xml = "<?xml version=\"1.0\" encoding=\"UTF-16\"?>" + MinimalGpx;
        var r = RouteText(xml, "lying-encoding.gpx");

        Assert.False(r.Success);
        Assert.StartsWith("Invalid XML", r.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Gpx_AllPointsIdenticalTimestamp_ZeroSpeedsNoDivisionByZero()
    {
        // Devices really emit duplicate timestamps (1 Hz bursts, clock stalls). dt = 0 must not
        // divide: SmoothSpeedsCore guards dt > 0, so speeds are 0, MaxSpeed 0, AverageSpeed null
        // (elapsed 0 is not > 0), MovingTime 0 — nothing non-finite for the A1 sweep to catch.
        var xml = "<gpx xmlns=\"http://www.topografix.com/GPX/1/1\"><trk><trkseg>" +
                  "<trkpt lat=\"47.0\" lon=\"11.0\"><time>2026-01-01T08:00:00Z</time></trkpt>" +
                  "<trkpt lat=\"47.001\" lon=\"11.001\"><time>2026-01-01T08:00:00Z</time></trkpt>" +
                  "<trkpt lat=\"47.002\" lon=\"11.002\"><time>2026-01-01T08:00:00Z</time></trkpt>" +
                  "</trkseg></trk></gpx>";
        var r = RouteText(xml, "same-ts.gpx");

        Assert.True(r.Success, r.ErrorMessage);
        Assert.Equal(3, r.GpsPoints.Count);
        Assert.All(r.GpsPoints, p => Assert.Equal(0.0, p.Speed));
        Assert.Equal(0.0, r.Summary!.ElapsedTime);
        Assert.Equal(0.0, r.Summary.MovingTime);
        Assert.Equal(0.0, r.Summary.MaxSpeed);
        Assert.Null(r.Summary.AverageSpeed);
    }

    [Fact]
    public void Gpx_ReversedTimestamps_NoNegativeTimeStats()
    {
        // Timestamps running backwards (corrupt clock / spliced file): elapsed would be negative
        // and is nulled instead; backwards deltas add no moving time; backwards speeds clamp to
        // 0. Nothing negative or non-finite persists.
        var xml = "<gpx xmlns=\"http://www.topografix.com/GPX/1/1\"><trk><trkseg>" +
                  "<trkpt lat=\"47.0\" lon=\"11.0\"><time>2026-01-01T08:00:20Z</time></trkpt>" +
                  "<trkpt lat=\"47.001\" lon=\"11.001\"><time>2026-01-01T08:00:10Z</time></trkpt>" +
                  "<trkpt lat=\"47.002\" lon=\"11.002\"><time>2026-01-01T08:00:00Z</time></trkpt>" +
                  "</trkseg></trk></gpx>";
        var r = RouteText(xml, "reversed-ts.gpx");

        Assert.True(r.Success, r.ErrorMessage);
        Assert.Null(r.Summary!.ElapsedTime);
        Assert.Null(r.Summary.TotalTime);
        Assert.Equal(0.0, r.Summary.MovingTime);
        Assert.Equal(0.0, r.Summary.MaxSpeed);
        Assert.Null(r.Summary.AverageSpeed);
    }

    [Fact]
    public void Gpx_ExtremeAndNegativeDeviceTotals_RejectedByRangeSweep()
    {
        // OBS-1 fix (2026-07-22): device (extension) totals were only finite-checked, never
        // range-checked — a claimed distance of double.MaxValue metres and a NEGATIVE ascent
        // imported cleanly and persisted as-is. The router's range sweep now rejects impossible
        // finite/negative stats, naming the first offender (Distance, checked before ElevationGain),
        // same contract as the non-finite path. (Replaced "…PersistUnchecked_PinsCurrentBehavior".)
        var xml = "<gpx xmlns=\"http://www.topografix.com/GPX/1/1\"" +
                  " xmlns:gpxtrkx=\"http://www.garmin.com/xmlschemas/TrackStatsExtension/v1\">" +
                  "<trk><extensions><gpxtrkx:TrackStatsExtension>" +
                  "<gpxtrkx:Distance>1.7976931348623157E308</gpxtrkx:Distance>" +
                  "<gpxtrkx:Ascent>-5000</gpxtrkx:Ascent>" +
                  "</gpxtrkx:TrackStatsExtension></extensions><trkseg>" +
                  "<trkpt lat=\"47.0\" lon=\"11.0\"><time>2026-01-01T08:00:00Z</time></trkpt>" +
                  "<trkpt lat=\"47.001\" lon=\"11.001\"><time>2026-01-01T08:00:10Z</time></trkpt>" +
                  "</trkseg></trk></gpx>";
        var r = RouteText(xml, "brag.gpx");

        Assert.False(r.Success);
        Assert.Contains("Distance", r.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("range", r.ErrorMessage, StringComparison.Ordinal);
        Assert.Empty(r.GpsPoints);
    }

    [Fact]
    public void Gpx_ExtremeButRealDeviceTotals_NotRejectedByRangeSweep()
    {
        // OBS-1 happy-path: an ultra-endurance ride with large-but-physical device totals (300 km,
        // 12,000 m ascent) must still import — the ceilings are generous (calibrated well above the
        // live data's worst real values) so only clearly-corrupt (MaxValue/negative) stats are cut.
        var xml = "<gpx xmlns=\"http://www.topografix.com/GPX/1/1\"" +
                  " xmlns:gpxtrkx=\"http://www.garmin.com/xmlschemas/TrackStatsExtension/v1\">" +
                  "<trk><extensions><gpxtrkx:TrackStatsExtension>" +
                  "<gpxtrkx:Distance>300000</gpxtrkx:Distance>" +
                  "<gpxtrkx:Ascent>12000</gpxtrkx:Ascent>" +
                  "</gpxtrkx:TrackStatsExtension></extensions><trkseg>" +
                  "<trkpt lat=\"47.0\" lon=\"11.0\"><time>2026-01-01T08:00:00Z</time></trkpt>" +
                  "<trkpt lat=\"47.001\" lon=\"11.001\"><time>2026-01-01T08:00:10Z</time></trkpt>" +
                  "</trkseg></trk></gpx>";
        var r = RouteText(xml, "ultra.gpx");

        Assert.True(r.Success, r.ErrorMessage);
        Assert.Equal(300000.0, r.Summary!.Distance);
        Assert.Equal(12000.0, r.Summary.ElevationGain);
    }

    [Fact]
    public void Gpx_NamelessTrack_FilenameFallbackSanitizedAtRouter()
    {
        // BUG-1 fix (2026-07-22): the <name>-less fallback used the raw upload FILENAME, bypassing
        // the sanitizer <name> values already got. The router now sanitizes the finalized name for
        // every source — a 500-char filename with a leading control char is control-stripped and
        // capped to the 200-char limit. (Replaced the former "…BypassesSanitizer_PinsCurrentBehavior".)
        var xml = "<gpx xmlns=\"http://www.topografix.com/GPX/1/1\"><trk><trkseg>" +
                  "<trkpt lat=\"47.0\" lon=\"11.0\"/><trkpt lat=\"47.001\" lon=\"11.001\"/>" +
                  "</trkseg></trk></gpx>";
        var fileName = "\u0001" + new string('x', 499) + ".gpx";
        var r = RouteText(xml, fileName);

        Assert.True(r.Success, r.ErrorMessage);
        Assert.Equal(200, r.Summary!.Name!.Length);
        Assert.NotEqual('\u0001', r.Summary.Name[0]);
    }

    [Fact]
    public void Gpx_PointsBeyondMaxPoints_TruncatedAtCapWithWarning()
    {
        // Cap-BITES case (the 50k test above pins cap-doesn't-bite): 1,000,010 minimal points —
        // reachable in production, since the 50 MB per-file upload cap fits ~1.9M minimal trkpts.
        // Import succeeds with exactly MaxPoints points plus the truncation warning; the tail is
        // dropped, not crashed on. Bounded: ~25 MB XML, runs in seconds.
        var sb = new StringBuilder(26 * 1024 * 1024);
        sb.Append("<gpx xmlns=\"http://www.topografix.com/GPX/1/1\"><trk><trkseg>");
        for (int i = 0; i < 1_000_010; i++)
            sb.Append("<trkpt lat=\"1\" lon=\"1\"/>");
        sb.Append("</trkseg></trk></gpx>");

        var r = ParseGpx(sb.ToString());

        Assert.True(r.Success, r.ErrorMessage);
        Assert.Equal(1_000_000, r.GpsPoints.Count);
        Assert.Contains(r.Warnings, w => w.Contains("truncated", StringComparison.Ordinal));
    }

    // ── TCX parser: malformed / overflowing values ───────────────────────────────

    [Fact]
    public void Tcx_TruncatedMidTrackpoint_WholeFileRejected()
    {
        var whole =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
            "<TrainingCenterDatabase xmlns=\"http://www.garmin.com/xmlschemas/TrainingCenterDatabase/v2\">" +
            "<Activities><Activity Sport=\"Running\"><Id>2026-02-02T09:00:00Z</Id>" +
            "<Lap StartTime=\"2026-02-02T09:00:00Z\"><Track>" +
            "<Trackpoint><Time>2026-02-02T09:00:00Z</Time><Position>" +
            "<LatitudeDegrees>47.20</LatitudeDegrees><LongitudeDegrees>8.20</LongitudeDegrees></Position></Trackpoint>" +
            "<Trackpoint><Time>2026-02-02T09:00:10Z</Time><Position>" +
            "<LatitudeDegrees>47.21</LatitudeDegrees><LongitudeDegrees>8.21</LongitudeDegrees></Position></Trackpoint>" +
            "</Track></Lap></Activity></Activities></TrainingCenterDatabase>";
        var truncated = whole[..(whole.Length * 6 / 10)];

        var r = ParseTcx(truncated);

        Assert.False(r.Success);
        Assert.StartsWith("Invalid XML", r.ErrorMessage, StringComparison.Ordinal);
        Assert.Empty(r.GpsPoints);
    }

    [Fact]
    public void Tcx_LapDistanceSumOverflow_RejectedCleanly()
    {
        // Each lap's DistanceMeters passes the per-value finite check, but the SUM across laps
        // overflows to +Infinity — the parser must reject the file (2026-07-17
        // fix ①) rather than let a non-finite total reach the DB and 500 on serialization later.
        const string maxDouble = "1.7976931348623157E308";
        var xml =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
            "<TrainingCenterDatabase xmlns=\"http://www.garmin.com/xmlschemas/TrainingCenterDatabase/v2\">" +
            "<Activities><Activity Sport=\"Running\"><Id>2026-02-02T09:00:00Z</Id>" +
            $"<Lap StartTime=\"2026-02-02T09:00:00Z\"><DistanceMeters>{maxDouble}</DistanceMeters><Track>" +
            "<Trackpoint><Time>2026-02-02T09:00:00Z</Time><Position>" +
            "<LatitudeDegrees>47.20</LatitudeDegrees><LongitudeDegrees>8.20</LongitudeDegrees></Position></Trackpoint>" +
            "</Track></Lap>" +
            $"<Lap StartTime=\"2026-02-02T09:01:00Z\"><DistanceMeters>{maxDouble}</DistanceMeters><Track>" +
            "<Trackpoint><Time>2026-02-02T09:01:00Z</Time><Position>" +
            "<LatitudeDegrees>47.21</LatitudeDegrees><LongitudeDegrees>8.21</LongitudeDegrees></Position></Trackpoint>" +
            "</Track></Lap></Activity></Activities></TrainingCenterDatabase>";

        var r = ParseTcx(xml);

        Assert.False(r.Success);
        Assert.Contains("non-finite", r.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Tcx_EmptyStream_InvalidXml()
    {
        var r = ParseTcx("");
        Assert.False(r.Success);
        Assert.StartsWith("Invalid XML", r.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Tcx_BillionLaughsEntityExpansion_Rejected()
    {
        // The TCX reader's DTD hardening is documented as "keep in sync with GpxParsingService" —
        // this pins the TCX half of that pair (the GPX half is pinned above).
        var xml = "<?xml version=\"1.0\"?><!DOCTYPE TrainingCenterDatabase [" +
                  "<!ENTITY a \"aaaaaaaaaaaaaaaaaaaa\">" +
                  "<!ENTITY b \"&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;\">" +
                  "<!ENTITY c \"&b;&b;&b;&b;&b;&b;&b;&b;&b;&b;\">]>" +
                  MinimalTcx.Replace("<?xml version=\"1.0\" encoding=\"UTF-8\"?>", "", StringComparison.Ordinal);
        var r = ParseTcx(xml);

        Assert.False(r.Success);
        Assert.StartsWith("Invalid XML", r.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Tcx_ExternalDtdReference_Rejected()
    {
        var xml = "<?xml version=\"1.0\"?><!DOCTYPE TrainingCenterDatabase SYSTEM \"http://127.0.0.1:9/evil.dtd\">" +
                  MinimalTcx.Replace("<?xml version=\"1.0\" encoding=\"UTF-8\"?>", "", StringComparison.Ordinal);
        var r = ParseTcx(xml);

        Assert.False(r.Success);
        Assert.StartsWith("Invalid XML", r.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Tcx_NonFiniteAndOutOfRangeCoordinates_SkippedPointwise()
    {
        // NaN / out-of-range / missing-Position trackpoints are skipped (indoor-sample contract);
        // the two sane points survive.
        var xml =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
            "<TrainingCenterDatabase xmlns=\"http://www.garmin.com/xmlschemas/TrainingCenterDatabase/v2\">" +
            "<Activities><Activity Sport=\"Running\"><Id>2026-02-02T09:00:00Z</Id>" +
            "<Lap StartTime=\"2026-02-02T09:00:00Z\"><Track>" +
            "<Trackpoint><Time>2026-02-02T09:00:00Z</Time><Position>" +
            "<LatitudeDegrees>NaN</LatitudeDegrees><LongitudeDegrees>8.20</LongitudeDegrees></Position></Trackpoint>" +
            "<Trackpoint><Time>2026-02-02T09:00:01Z</Time><Position>" +
            "<LatitudeDegrees>91</LatitudeDegrees><LongitudeDegrees>8.20</LongitudeDegrees></Position></Trackpoint>" +
            "<Trackpoint><Time>2026-02-02T09:00:02Z</Time><Position>" +
            "<LatitudeDegrees>47.20</LatitudeDegrees><LongitudeDegrees>-181</LongitudeDegrees></Position></Trackpoint>" +
            "<Trackpoint><Time>2026-02-02T09:00:03Z</Time></Trackpoint>" +
            "<Trackpoint><Time>2026-02-02T09:00:04Z</Time><Position>" +
            "<LatitudeDegrees>47.20</LatitudeDegrees><LongitudeDegrees>8.20</LongitudeDegrees></Position></Trackpoint>" +
            "<Trackpoint><Time>2026-02-02T09:00:05Z</Time><Position>" +
            "<LatitudeDegrees>47.21</LatitudeDegrees><LongitudeDegrees>8.21</LongitudeDegrees></Position></Trackpoint>" +
            "</Track></Lap></Activity></Activities></TrainingCenterDatabase>";
        var r = ParseTcx(xml);

        Assert.True(r.Success, r.ErrorMessage);
        Assert.Equal(2, r.GpsPoints.Count);
        Assert.All(r.GpsPoints, p =>
        {
            Assert.InRange(p.Lat, -90, 90);
            Assert.InRange(p.Lng, -180, 180);
        });
    }

    [Fact]
    public void Tcx_OppositeExtremeAltitudes_RejectedNonFiniteDescent()
    {
        // A1 (2026-07-20): rewrites the former BUG-1 pin. The 2026-07-17 fix rejects
        // non-finite LAP-TOTAL sums in the parser; per-point AltitudeMeters feeds the same
        // unguarded delta sum in ComputeElevation, and the router's aggregate sweep now catches
        // that half too — the file is rejected, not persisted with an infinite descent.
        const string max = "1.7976931348623157E308";
        var xml =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
            "<TrainingCenterDatabase xmlns=\"http://www.garmin.com/xmlschemas/TrainingCenterDatabase/v2\">" +
            "<Activities><Activity Sport=\"Running\"><Id>2026-02-02T09:00:00Z</Id>" +
            "<Lap StartTime=\"2026-02-02T09:00:00Z\"><Track>" +
            "<Trackpoint><Time>2026-02-02T09:00:00Z</Time><Position>" +
            "<LatitudeDegrees>47.20</LatitudeDegrees><LongitudeDegrees>8.20</LongitudeDegrees></Position>" +
            $"<AltitudeMeters>{max}</AltitudeMeters></Trackpoint>" +
            "<Trackpoint><Time>2026-02-02T09:00:10Z</Time><Position>" +
            "<LatitudeDegrees>47.21</LatitudeDegrees><LongitudeDegrees>8.21</LongitudeDegrees></Position>" +
            $"<AltitudeMeters>-{max}</AltitudeMeters></Trackpoint>" +
            "</Track></Lap></Activity></Activities></TrainingCenterDatabase>";
        var r = RouteText(xml, "hostile.tcx");

        Assert.False(r.Success);
        Assert.Contains("TotalDescent", r.ErrorMessage, StringComparison.Ordinal);
        Assert.Empty(r.GpsPoints);
    }

    [Fact]
    public void Tcx_NegativeLapTotals_RejectedByRangeSweep()
    {
        // OBS-1 fix (2026-07-22): lap totals are finite-checked (2026-07-17) but were not
        // range-checked — negative DistanceMeters/Calories summed into negative persisted stats.
        // The router's range sweep now rejects them, naming Distance (checked before Calories).
        var xml =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
            "<TrainingCenterDatabase xmlns=\"http://www.garmin.com/xmlschemas/TrainingCenterDatabase/v2\">" +
            "<Activities><Activity Sport=\"Running\"><Id>2026-02-02T09:00:00Z</Id>" +
            "<Lap StartTime=\"2026-02-02T09:00:00Z\"><DistanceMeters>-100000</DistanceMeters>" +
            "<Calories>-500</Calories><Track>" +
            "<Trackpoint><Time>2026-02-02T09:00:00Z</Time><Position>" +
            "<LatitudeDegrees>47.20</LatitudeDegrees><LongitudeDegrees>8.20</LongitudeDegrees></Position></Trackpoint>" +
            "<Trackpoint><Time>2026-02-02T09:00:10Z</Time><Position>" +
            "<LatitudeDegrees>47.21</LatitudeDegrees><LongitudeDegrees>8.21</LongitudeDegrees></Position></Trackpoint>" +
            "</Track></Lap></Activity></Activities></TrainingCenterDatabase>";
        var r = RouteText(xml, "negative.tcx");

        Assert.False(r.Success);
        Assert.Contains("Distance", r.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("range", r.ErrorMessage, StringComparison.Ordinal);
        Assert.Empty(r.GpsPoints);
    }

    [Fact]
    public void Tcx_FilenameName_SanitizedAtRouter()
    {
        // BUG-1 fix (2026-07-22): TCX has no in-file name, so the activity name is always the upload
        // filename — previously taken verbatim (no cap, control chars survived). The router now caps
        // it at 200 and strips control chars for every source (FIT shares this exact path).
        var fileName = new string('y', 300) + "\u0007.tcx";
        var r = RouteText(MinimalTcx, fileName);

        Assert.True(r.Success, r.ErrorMessage);
        Assert.Equal(200, r.Summary!.Name!.Length);
        Assert.NotEqual('\u0007', r.Summary.Name[^1]);
    }

    // ── FIT parser: truncated / lying / random binary ────────────────────────────

    [Fact]
    public void Fit_EmptyStream_CleanFailure()
    {
        var r = ParseFit(Array.Empty<byte>());
        Assert.False(r.Success);
        Assert.NotNull(r.ErrorMessage);
    }

    [Fact]
    public void Fit_TruncatedMidRecord_CleanFailure()
    {
        var whole = EncodeFitRide(20, new DateTime(2026, 5, 1, 6, 0, 0, DateTimeKind.Utc));
        var truncated = whole[..(whole.Length * 6 / 10)];

        var r = ParseFit(truncated);

        Assert.False(r.Success);
        Assert.NotNull(r.ErrorMessage);
    }

    [Fact]
    public void Fit_HeaderDataSizeLies_CleanFailure()
    {
        // Patch the header's DataSize field to claim ~2 GB of data that isn't there. The decoder
        // must reject (header CRC and/or integrity check), not allocate or spin toward the claim.
        var bytes = EncodeFitRide(3, new DateTime(2026, 5, 1, 6, 0, 0, DateTimeKind.Utc));
        BitConverter.GetBytes(0x7FFFFFF0).CopyTo(bytes, 4);

        var r = ParseFit(bytes);

        Assert.False(r.Success);
        Assert.NotNull(r.ErrorMessage);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(1337)]
    public void Fit_RandomBytesWithFitSignature_RejectedWithoutThrow(int seed)
    {
        // Deterministic pseudo-random garbage that passes the sniff (".FIT" at offset 8) so it
        // reaches the real decoder. Contract: clean failure for ANY byte soup, never a crash.
        var bytes = new byte[4096];
        new Random(seed).NextBytes(bytes);
        bytes[0] = 14; // plausible header length
        bytes[8] = (byte)'.'; bytes[9] = (byte)'F'; bytes[10] = (byte)'I'; bytes[11] = (byte)'T';

        var r = ParseFit(bytes);

        Assert.False(r.Success);
        Assert.NotNull(r.ErrorMessage);
    }

    [Fact]
    public void Fit_50kRecords_ParsesFullyWithinCap()
    {
        // Large-but-legitimate volume (bounded well under MaxRecords): all points survive, no
        // truncation warning — pins that the 1M cap doesn't bite normal multi-hour rides.
        var r = ParseFit(EncodeFitRide(50_000, new DateTime(2026, 5, 1, 6, 0, 0, DateTimeKind.Utc)));

        Assert.True(r.Success, r.ErrorMessage);
        Assert.Equal(50_000, r.GpsPoints.Count);
        Assert.DoesNotContain(r.Warnings, w => w.Contains("truncated", StringComparison.Ordinal));
    }

    [Fact]
    public void Fit_CorruptedTrailingCrc_FailsIntegrityCheck()
    {
        // Mismatched-checksum case: records are all intact, only the trailing 2-byte file CRC is
        // wrong. The SDK reports this by returning false from Read (FP-01) — the parser must fail
        // closed with the integrity message and surface no points.
        var bytes = EncodeFitRide(5, new DateTime(2026, 5, 1, 6, 0, 0, DateTimeKind.Utc));
        bytes[^1] ^= 0xFF;

        var r = ParseFit(bytes);

        Assert.False(r.Success);
        Assert.Contains("integrity", r.ErrorMessage, StringComparison.Ordinal);
        Assert.Empty(r.GpsPoints);
    }

    [Fact]
    public void Fit_GarbageBlockMidFile_CleanFailure()
    {
        // Valid header + valid leading records, then a 24-byte 0xFF block mid-stream (bit rot /
        // hostile splice). Whether the decoder throws or fails the CRC, the contract is the same:
        // clean rejection, no partial import of the intact leading records.
        var bytes = EncodeFitRide(30, new DateTime(2026, 5, 1, 6, 0, 0, DateTimeKind.Utc));
        for (int i = 0; i < 24; i++)
            bytes[bytes.Length / 2 + i] = 0xFF;

        var r = ParseFit(bytes);

        Assert.False(r.Success);
        Assert.NotNull(r.ErrorMessage);
        Assert.Empty(r.GpsPoints);
    }

    [Fact]
    public void Fit_TwoChainedFiles_SplitIntoMultipleActivities()
    {
        // OBS-2 (2026-07-22): chained FIT (two complete files back-to-back) is legal per the FIT spec.
        // Each file has its OWN header, so the SDK raises FileIdMesg twice — the parser splits the stream
        // into TWO activities, each with its own points and its own per-file totals (NOT the first
        // session's), named <base> and <base> (2). No spurious multi-session warning survives the split.
        var a = EncodeFitRide(3, new DateTime(2026, 5, 1, 6, 0, 0, DateTimeKind.Utc), totalDistance: 1000f);
        var b = EncodeFitRide(4, new DateTime(2026, 6, 1, 6, 0, 0, DateTimeKind.Utc), totalDistance: 2500f);
        var results = ParseFitAll(a.Concat(b).ToArray(), "chained.fit");

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.True(r.Success, r.ErrorMessage));

        // Per-file points: file a has 3, file b has 4 — never merged into one 7-point activity.
        Assert.Equal(3, results[0].GpsPoints.Count);
        Assert.Equal(4, results[1].GpsPoints.Count);

        // Per-file device totals: each activity carries ITS OWN session distance, not the first file's.
        Assert.Equal(1000, results[0].Summary!.Distance!.Value, 0);
        Assert.Equal(2500, results[1].Summary!.Distance!.Value, 0);

        // Per-file start times prove each result decoded its own file (May vs June).
        Assert.Equal(2026, results[0].Summary!.StartDateUtc!.Value.Year);
        Assert.Equal(5, results[0].Summary!.StartDateUtc!.Value.Month);
        Assert.Equal(6, results[1].Summary!.StartDateUtc!.Value.Month);

        // Editor-split naming: <base>, then <base> (2).
        Assert.Equal("chained", results[0].Summary!.Name);
        Assert.Equal("chained (2)", results[1].Summary!.Name);

        Assert.All(results, r => Assert.DoesNotContain(r.Warnings, w => w.Contains("Multiple sessions", StringComparison.Ordinal)));
    }

    [Fact]
    public void Fit_MultisportSingleFile_StaysOneActivity()
    {
        // OBS-2 counterpart: ONE file header with several sessions (a multisport/triathlon file) is a
        // single file, not a chain — it must stay ONE activity, never split per session.
        using var ms = new MemoryStream();
        var encode = new Encode(ProtocolVersion.V20);
        encode.Open(ms);

        var start = new DateTime(2026, 7, 1, 6, 0, 0, DateTimeKind.Utc);
        var fileId = new FileIdMesg();
        fileId.SetType(Dynastream.Fit.File.Activity);
        fileId.SetManufacturer(Manufacturer.Garmin);
        fileId.SetTimeCreated(new Dynastream.Fit.DateTime(start));
        encode.Write(fileId);

        for (int i = 0; i < 5; i++)
        {
            var rec = new RecordMesg();
            rec.SetTimestamp(new Dynastream.Fit.DateTime(start.AddSeconds(i)));
            rec.SetPositionLat((int)Math.Round((47.10 + i * 1e-4) * SemicirclesPerDegree));
            rec.SetPositionLong((int)Math.Round(8.10 * SemicirclesPerDegree));
            encode.Write(rec);
        }

        foreach (var sport in new[] { Sport.Swimming, Sport.Cycling, Sport.Running })
        {
            var session = new SessionMesg();
            session.SetStartTime(new Dynastream.Fit.DateTime(start));
            session.SetSport(sport);
            encode.Write(session);
        }
        encode.Close();

        var results = ParseFitAll(ms.ToArray(), "triathlon.fit");

        Assert.Single(results);                                 // one header → one activity
        Assert.Equal(5, results[0].GpsPoints.Count);            // all points kept
        Assert.Equal("triathlon", results[0].Summary!.Name);    // no " (2)" suffix
        Assert.DoesNotContain(results[0].Warnings, w => w.Contains("Multiple sessions", StringComparison.Ordinal));
    }

    [Fact]
    public void Fit_UnsupportedProtocolVersion_CleanFailure()
    {
        // Header protocol-version byte claims major version 15 — far beyond the SDK's V2. The
        // decoder must reject cleanly (the SDK throws internally; the parser catches), never crash.
        var bytes = EncodeFitRide(3, new DateTime(2026, 5, 1, 6, 0, 0, DateTimeKind.Utc));
        bytes[1] = 0xF0;

        var r = ParseFit(bytes);

        Assert.False(r.Success);
        Assert.NotNull(r.ErrorMessage);
        Assert.Empty(r.GpsPoints);
    }

    [Fact]
    public void Fit_HeaderSizeByteLie_CleanFailure()
    {
        // The header's own size byte lies (13 — neither legal 12 nor 14), misaligning everything
        // after it. Clean rejection, no crash, no points.
        var bytes = EncodeFitRide(3, new DateTime(2026, 5, 1, 6, 0, 0, DateTimeKind.Utc));
        bytes[0] = 13;

        var r = ParseFit(bytes);

        Assert.False(r.Success);
        Assert.NotNull(r.ErrorMessage);
        Assert.Empty(r.GpsPoints);
    }

    [Fact]
    public void Fit_FilenameName_SanitizedAtRouter()
    {
        // BUG-1 fix (2026-07-22): FIT carries no in-file name — same upload-filename path as TCX. The
        // router caps the name at 200 and strips control chars. Built with a raw (char)2 so the source
        // stays escape-free; the sanitized name has no chars below 0x20 and is capped to the limit.
        var fit = EncodeFitRide(3, new DateTime(2026, 5, 1, 6, 0, 0, DateTimeKind.Utc));
        var fileName = new string('z', 300) + (char)2 + ".fit";
        var r = RouteBytes(fit, fileName);

        Assert.True(r.Success, r.ErrorMessage);
        Assert.Equal(200, r.Summary!.Name!.Length);
        Assert.All(r.Summary.Name!, ch => Assert.True(ch >= ' '));
    }

    // ── Upload policy: degenerate files ──────────────────────────────────────────

    [Fact]
    public void Policy_ZeroByteFile_RejectedByAllowlist()
    {
        var empty = new UploadCandidate("empty.gpx", 0, Array.Empty<byte>());
        var verdict = new TrackUploadPolicy(new TrackUploadLimits()).Evaluate(new[] { empty }, 0, 0);

        Assert.False(verdict.Ok);
        Assert.Contains("GPX, TCX, and FIT", verdict.Error, StringComparison.Ordinal);
        Assert.Contains("empty.gpx", verdict.Files!, StringComparer.Ordinal);
    }

    [Fact]
    public void Policy_DirectoryOnlyFileName_Rejected()
    {
        // "dir/" reduces to an empty leaf name — must hit the degenerate-name reject even though
        // the header sniffs as a perfectly valid GPX (rejection is about the name, not the bytes).
        var cand = new UploadCandidate("dir/", 100, Encoding.UTF8.GetBytes(MinimalGpx));
        var verdict = new TrackUploadPolicy(new TrackUploadLimits()).Evaluate(new[] { cand }, 0, 0);

        Assert.False(verdict.Ok);
        Assert.Contains("Invalid file name", verdict.Error, StringComparison.Ordinal);
        Assert.Contains("dir/", verdict.Files!, StringComparer.Ordinal);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    /// <summary>Forward-only wrapper forcing the router's defensive buffering path.</summary>
    private sealed class NonSeekableStream : Stream
    {
        private readonly Stream _inner;
        public NonSeekableStream(Stream inner) => _inner = inner;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
    }
}
