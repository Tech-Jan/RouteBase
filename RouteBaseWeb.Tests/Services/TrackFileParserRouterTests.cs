using System.Text;
using RouteBaseWeb.Models.Gpx;
using RouteBaseWeb.Models.Sync;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Interfaces;
using Xunit;

namespace RouteBaseWeb.Tests.Services;

/// <summary>
/// Content-sniff routing (Step 2): <see cref="TrackFileSniffer"/> decides format from magic bytes,
/// and <see cref="TrackFileParserRouter"/> delegates GPX to the real parser, rejects unknown content,
/// and leaves the seekable stream positioned for the parser. Extension is never trusted.
/// </summary>
public class TrackFileParserRouterTests
{
    // ── Sniffer (pure) ─────────────────────────────────────────────────────────

    [Fact]
    public void Detect_PlainGpx_ReturnsGpx() =>
        Assert.Equal(TrackFileFormat.Gpx, Sniff("<gpx version=\"1.1\"><trk></trk></gpx>"));

    [Fact]
    public void Detect_GpxWithPrologAndBom_ReturnsGpx()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }
            .Concat(Encoding.UTF8.GetBytes("\n<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<gpx xmlns=\"http://www.topografix.com/GPX/1/1\">"))
            .ToArray();
        Assert.Equal(TrackFileFormat.Gpx, TrackFileSniffer.Detect(bytes));
    }

    [Fact]
    public void Detect_Tcx_ReturnsTcx() =>
        Assert.Equal(TrackFileFormat.Tcx,
            Sniff("<?xml version=\"1.0\"?>\n<TrainingCenterDatabase xmlns=\"http://www.garmin.com/xmlschemas/TrainingCenterDatabase/v2\">"));

    [Fact]
    public void Detect_FitSignature_ReturnsFit()
    {
        // 12-byte FIT header: size, protocol, profile(2), datasize(4), ".FIT" at bytes 8–11.
        var header = new byte[] { 0x0C, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            (byte)'.', (byte)'F', (byte)'I', (byte)'T' };
        Assert.Equal(TrackFileFormat.Fit, TrackFileSniffer.Detect(header));
    }

    [Fact]
    public void Detect_SpoofedBinary_ReturnsUnknown() =>
        // An .exe (MZ header) renamed evil.gpx must not sniff as GPX.
        Assert.Equal(TrackFileFormat.Unknown, TrackFileSniffer.Detect(new byte[] { 0x4D, 0x5A, 0x90, 0x00 }));

    [Fact]
    public void Detect_NonGpxXml_ReturnsUnknown() =>
        Assert.Equal(TrackFileFormat.Unknown, Sniff("<html><body>not a track</body></html>"));

    // ── Router ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_GpxContent_DelegatesToGpxParserWithResetStream()
    {
        var gpx = new RecordingParser();
        var router = new TrackFileParserRouter(gpx, new RecordingParser(), new RecordingParser());

        var result = router.Parse(StreamOf("<gpx version=\"1.1\"><trk></trk></gpx>"), "ride.gpx").Single();

        Assert.True(result.Success);
        Assert.Equal(0, gpx.StreamPositionAtParse); // sniff peeked but rewound before delegating
    }

    [Fact]
    public void Parse_SpoofedExtension_RejectedBeforeParsing()
    {
        var gpx = new RecordingParser();
        var tcx = new RecordingParser();
        var fit = new RecordingParser();
        var router = new TrackFileParserRouter(gpx, tcx, fit);

        // Binary content with a .gpx name — must be rejected without ever calling a parser.
        var result = router.Parse(new MemoryStream(new byte[] { 0x4D, 0x5A, 0x90, 0x00 }), "evil.gpx").Single();

        Assert.False(result.Success);
        Assert.Equal("Unrecognized file format.", result.ErrorMessage);
        Assert.False(gpx.WasCalled);
        Assert.False(tcx.WasCalled);
        Assert.False(fit.WasCalled);
    }

    [Fact]
    public void Parse_TcxContent_DelegatesToTcxParserNotGpx()
    {
        var gpx = new RecordingParser();
        var tcx = new RecordingParser();
        var router = new TrackFileParserRouter(gpx, tcx, new RecordingParser());

        var result = router.Parse(StreamOf("<TrainingCenterDatabase></TrainingCenterDatabase>"), "run.tcx").Single();

        Assert.True(result.Success);
        Assert.True(tcx.WasCalled);
        Assert.False(gpx.WasCalled);
        Assert.Equal(0, tcx.StreamPositionAtParse); // rewound before delegating
    }

    [Fact]
    public void Parse_FitContent_DelegatesToFitParser()
    {
        var gpx = new RecordingParser();
        var tcx = new RecordingParser();
        var fit = new RecordingParser();
        var router = new TrackFileParserRouter(gpx, tcx, fit);

        // Minimal FIT header: size, protocol, profile(2), datasize(4), ".FIT" at bytes 8–11.
        var fitHeader = new byte[] { 0x0C, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            (byte)'.', (byte)'F', (byte)'I', (byte)'T', 0x00, 0x00 };
        var result = router.Parse(new MemoryStream(fitHeader), "ride.fit").Single();

        Assert.True(result.Success);
        Assert.True(fit.WasCalled);
        Assert.False(gpx.WasCalled);
        Assert.False(tcx.WasCalled);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static TrackFileFormat Sniff(string content) =>
        TrackFileSniffer.Detect(Encoding.UTF8.GetBytes(content));

    private static MemoryStream StreamOf(string content) =>
        new(Encoding.UTF8.GetBytes(content));

    // Satisfies all format-parser interfaces (identical signature) so it can stand in for any slot.
    private sealed class RecordingParser : IGpxParsingService, ITcxParsingService, IFitParsingService
    {
        public bool WasCalled { get; private set; }
        public long StreamPositionAtParse { get; private set; } = -1;

        public IReadOnlyList<GpxParseResult> Parse(Stream stream, string fileName, bool useFilenameAsId = false)
        {
            WasCalled = true;
            StreamPositionAtParse = stream.CanSeek ? stream.Position : -1;
            return [new GpxParseResult
            {
                Success = true,
                Summary = new ExternalActivitySummary { Name = fileName },
                GpsPoints = [],
            }];
        }
    }
}
