using System.Text;
using Dynastream.Fit;
using Microsoft.Extensions.Logging.Abstractions;
using RouteBaseWeb.Models.Gpx;
using RouteBaseWeb.Services;
using Xunit;
using DateTime = System.DateTime;

namespace RouteBaseWeb.Tests.Services;

/// <summary>
/// Hostile-input suite, third file (import fuzzing, 2026-07-29) — continues
/// <see cref="ImportHostileInputTests"/> and <see cref="ImportHostileInputEdgeTests"/>. This wave
/// closes coverage gaps left by the 2026-07-28 import commit (<c>6b04eb43</c>, "split multi-activity
/// TCX, drop impossible timestamps"): the timestamp-degradation tests it added were GPX-only, yet the
/// change's whole thesis is that GPX and TCX now share ONE <see cref="TrackTimestamp"/> helper "so they
/// cannot drift on what 'unusable' means" — untested on the TCX side. Also pins the multi-activity TCX
/// split's <c>emitted</c>-counter naming when the LEADING session is unusable (only the trailing-unusable
/// case was pinned), and two degenerate-structure corners (FIT with no records, compressed uploads).
/// Same contract throughout: clean rejection (<c>Success=false</c> + message) or clean degradation
/// (points kept, clock dropped), never an unhandled crash, never partial/corrupt data persisting.
/// </summary>
public class ImportHostileInputDeltaTests
{
    private const double SemicirclesPerDegree = 2147483648.0 / 180.0;

    private static TrackFileParserRouter RealRouter() => new(
        new GpxParsingService(NullLogger<GpxParsingService>.Instance),
        new TcxParsingService(NullLogger<TcxParsingService>.Instance),
        new FitParsingService(NullLogger<FitParsingService>.Instance));

    private static GpxParseResult RouteBytes(byte[] bytes, string fileName) =>
        RealRouter().Parse(new MemoryStream(bytes), fileName).Single();

    private static GpxParseResult ParseTcx(string xml) =>
        new TcxParsingService(NullLogger<TcxParsingService>.Instance)
            .Parse(new MemoryStream(Encoding.UTF8.GetBytes(xml)), "hostile.tcx").Single();

    private static IReadOnlyList<GpxParseResult> ParseTcxAll(string xml, string fileName = "hostile.tcx") =>
        new TcxParsingService(NullLogger<TcxParsingService>.Instance)
            .Parse(new MemoryStream(Encoding.UTF8.GetBytes(xml)), fileName);

    private static IReadOnlyList<GpxParseResult> ParseFitAll(byte[] bytes, string fileName = "hostile.fit") =>
        new FitParsingService(NullLogger<FitParsingService>.Instance)
            .Parse(new MemoryStream(bytes), fileName);

    /// <summary>Single-Activity TCX with two positioned trackpoints stamped <paramref name="firstTime"/>
    /// and <paramref name="lastTime"/> — the TCX mirror of the edge suite's GpxWithTimes helper.</summary>
    private static string TcxWithTimes(string firstTime, string lastTime) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
        "<TrainingCenterDatabase xmlns=\"http://www.garmin.com/xmlschemas/TrainingCenterDatabase/v2\">" +
        "<Activities><Activity Sport=\"Running\"><Id>2026-02-02T09:00:00Z</Id>" +
        "<Lap StartTime=\"2026-02-02T09:00:00Z\"><Track>" +
        $"<Trackpoint><Time>{firstTime}</Time><Position>" +
        "<LatitudeDegrees>47.20</LatitudeDegrees><LongitudeDegrees>8.20</LongitudeDegrees></Position></Trackpoint>" +
        $"<Trackpoint><Time>{lastTime}</Time><Position>" +
        "<LatitudeDegrees>47.21</LatitudeDegrees><LongitudeDegrees>8.21</LongitudeDegrees></Position></Trackpoint>" +
        "</Track></Lap></Activity></Activities></TrainingCenterDatabase>";

    // ── TCX: shared TrackTimestamp helper (the GPX↔TCX anti-drift contract) ───────

    [Theory]
    [InlineData("0001-01-01T00:00:00+14:00")]  // offset underflows DateTime.MinValue → .NET CLAMPS to year 1
    [InlineData("0001-01-02T00:00:00Z")]       // a year-1 stamp that needed no clamping at all
    [InlineData("1899-12-31T23:59:59Z")]       // just under the plausibility floor
    public void Tcx_ImplausiblyOldTime_DegradesToMissingTimestamp(string time)
    {
        // The TCX mirror of Gpx_ImplausiblyOldTime_DegradesToMissingTimestamp (6b04eb43, 2026-07-28).
        // TCX's inline DateTime.TryParse was replaced with the shared TrackTimestamp.TryParseUtc so the
        // two parsers cannot disagree about what "unusable" means — but the change shipped with tests on
        // the GPX side only. This proves TCX applies the same 1900 floor (and absorbs the year-1
        // offset-underflow clamp that TryParse reports as success): the ride still imports, its clock is
        // dropped, no fictional year-0001 start date or wall-clock id is minted.
        var r = ParseTcx(TcxWithTimes(time, time));

        Assert.True(r.Success, r.ErrorMessage);
        Assert.Equal(2, r.GpsPoints.Count);
        Assert.Null(r.Summary!.StartDateUtc);
        Assert.Contains(r.Warnings, w => w.Contains("missing timestamps", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("not-a-time")]
    [InlineData("2026-13-45T99:99:99Z")]   // out-of-range calendar fields
    public void Tcx_UnparseableTime_TreatedAsMissingTimestamp(string time)
    {
        // Companion to the old-year theory: an unparseable <Time> must also route through the shared
        // helper (TryParse returns false, never throws) so a garbled clock degrades to missing rather
        // than crashing the parse. Pins that TCX's Time path is the shared helper end-to-end, not just
        // for the sub-1900 branch.
        var r = ParseTcx(TcxWithTimes(time, time));

        Assert.True(r.Success, r.ErrorMessage);
        Assert.Equal(2, r.GpsPoints.Count);
        Assert.Null(r.Summary!.StartDateUtc);
        Assert.Contains(r.Warnings, w => w.Contains("missing timestamps", StringComparison.Ordinal));
    }

    [Fact]
    public void Tcx_AnOldButRealRide_KeepsItsTimestamps()
    {
        // The floor must not eat legitimate history on the TCX side either (mirrors the GPX guard):
        // a 1990 ride is far above the 1900 floor and keeps its clock.
        var r = ParseTcx(TcxWithTimes("1990-05-01T06:00:00Z", "1990-05-01T06:30:00Z"));

        Assert.True(r.Success, r.ErrorMessage);
        Assert.Equal(1990, r.Summary!.StartDateUtc!.Value.Year);
        Assert.DoesNotContain(r.Warnings, w => w.Contains("missing timestamps", StringComparison.Ordinal));
    }

    // ── TCX: multi-activity split, LEADING session unusable ─────────────────────

    [Fact]
    public void Tcx_LeadingUnusableSession_SurvivorKeepsBaseNameNotSuffix()
    {
        // The 2026-07-28 split names activities by `emitted` (successful count so far), NOT raw index —
        // so a dropped LEADING session must not push the survivor to " (2)". The edge suite pins the
        // trailing-unusable case (Tcx_OneUnusableSessionDoesNotSinkTheRidesAroundIt); this pins the
        // harder mirror: activity #1 is indoor (no Position → 0 points → dropped), activity #2 is the
        // only ride, and it must claim index 0 — plain base name, filename id eligible.
        var xml =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
            "<TrainingCenterDatabase xmlns=\"http://www.garmin.com/xmlschemas/TrainingCenterDatabase/v2\">" +
            "<Activities>" +
            "<Activity Sport=\"Running\"><Id>2026-02-02T09:00:00Z</Id>" +
            "<Lap StartTime=\"2026-02-02T09:00:00Z\"><Track>" +
            "<Trackpoint><Time>2026-02-02T09:00:00Z</Time></Trackpoint>" +   // indoor: no Position
            "</Track></Lap></Activity>" +
            "<Activity Sport=\"Biking\"><Id>2026-03-03T10:00:00Z</Id>" +
            "<Lap StartTime=\"2026-03-03T10:00:00Z\"><Track>" +
            "<Trackpoint><Time>2026-03-03T10:00:00Z</Time><Position>" +
            "<LatitudeDegrees>48.50</LatitudeDegrees><LongitudeDegrees>9.50</LongitudeDegrees></Position></Trackpoint>" +
            "<Trackpoint><Time>2026-03-03T10:00:10Z</Time><Position>" +
            "<LatitudeDegrees>48.51</LatitudeDegrees><LongitudeDegrees>9.51</LongitudeDegrees></Position></Trackpoint>" +
            "</Track></Lap></Activity>" +
            "</Activities></TrainingCenterDatabase>";

        var kept = Assert.Single(ParseTcxAll(xml, "bundle.tcx"));

        Assert.True(kept.Success, kept.ErrorMessage);
        Assert.Equal(3, kept.Summary!.StartDateUtc!.Value.Month);   // the March ride, not the dropped Feb one
        Assert.Equal("bundle", kept.Summary!.Name);                 // index 0 → base name, NOT "bundle (2)"
    }

    // ── FIT: degenerate structure — a valid file carrying no records ─────────────

    [Fact]
    public void Fit_HeaderAndSessionButNoRecords_RejectedNoGpsPoints()
    {
        // A structurally valid FIT (FileId + session, integrity intact) that simply carries no Record
        // messages — a device that logged only summary data, or a hostile minimal file. No segment has
        // points, so the parser fails with the no-track message rather than emitting a point-less
        // activity or dereferencing points.First() on an empty list.
        using var ms = new MemoryStream();
        var encode = new Encode(ProtocolVersion.V20);
        encode.Open(ms);

        var start = new DateTime(2026, 5, 1, 6, 0, 0, DateTimeKind.Utc);
        var fileId = new FileIdMesg();
        fileId.SetType(Dynastream.Fit.File.Activity);
        fileId.SetManufacturer(Manufacturer.Garmin);
        fileId.SetTimeCreated(new Dynastream.Fit.DateTime(start));
        encode.Write(fileId);

        var session = new SessionMesg();
        session.SetStartTime(new Dynastream.Fit.DateTime(start));
        session.SetSport(Sport.Cycling);
        encode.Write(session);
        encode.Close();

        var r = Assert.Single(ParseFitAll(ms.ToArray()));

        Assert.False(r.Success);
        Assert.Equal("FIT file contains no GPS track points.", r.ErrorMessage);
        Assert.Empty(r.GpsPoints);
    }

    // ── Router: compressed uploads (realistic mistaken input) ────────────────────

    [Theory]
    [InlineData(new byte[] { 0x1F, 0x8B, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03, 0x00, 0x00 })] // gzip (.gpx.gz)
    [InlineData(new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x14, 0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x00 })] // zip  (bulk export)
    public void Router_CompressedContent_RejectedAsUnrecognized(byte[] header)
    {
        // Strava's bulk export ships .gpx.gz, and users routinely upload the whole export .zip. Neither
        // is a track container the router understands, so the sniffer must return Unknown and the router
        // reject cleanly — never crash a decompressor or hand raw compressed bytes to a parser.
        var r = RouteBytes(header, "export.gpx");

        Assert.False(r.Success);
        Assert.Equal("Unrecognized file format.", r.ErrorMessage);
    }
}
