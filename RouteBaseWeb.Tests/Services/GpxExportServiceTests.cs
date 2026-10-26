using System.Globalization;
using System.Xml.Linq;
using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace RouteBaseWeb.Tests.Services;

/// <summary>
/// GI-13: <c>&lt;gpxtpx:speed/grade/distance&gt;</c> aren't part of TrackPointExtension v1 and
/// our own importer ignores them (recomputed from lat/lon/time) — export must not emit them.
/// Power is non-schema too but kept as de-facto standard.
/// </summary>
public class GpxExportServiceTests
{
    private static Activity NewActivity() => new()
    {
        TrackId = new TrackId(1),
        TrackName = "Test Track",
        ActivityType = "Run",
        StartDateTimeUtc = new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc),
    };

    private static GpsData NewPoint(double lat, double lng, DateTime utc) => new()
    {
        GpsLat = lat,
        GpsLong = lng,
        DateTimeUtc = utc,
        HeartRate = 140,
        Cadence = 85,
        Power = 200,
        Speed = 3.5,
        GradeSmooth = 2.1,
        Distance = 10.0,
    };

    [Fact]
    public void BuildGpx_NoNonSchemaExtensionElements()
    {
        var points = new List<GpsData>
        {
            NewPoint(47.0, 11.0, new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc)),
            NewPoint(47.1, 11.1, new DateTime(2026, 1, 1, 8, 0, 10, DateTimeKind.Utc)),
        };

        var doc = GpxExportService.BuildGpx(NewActivity(), points);
        var xml = doc.ToString();

        Assert.DoesNotContain("speed", xml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("grade", xml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("distance", xml, StringComparison.OrdinalIgnoreCase);

        // Schema-valid ones (incl. de-facto-standard power) survive.
        Assert.Contains("<gpxtpx:hr>140</gpxtpx:hr>", xml, StringComparison.Ordinal);
        Assert.Contains("<gpxtpx:cad>85</gpxtpx:cad>", xml, StringComparison.Ordinal);
        Assert.Contains("<gpxtpx:power>200</gpxtpx:power>", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildGpx_UnderThaiBuddhistCulture_TimesStayGregorian()
    {
        // th-TH defaults to the Buddhist calendar: 2026 CE formats as year 2569 unless the
        // provider is named. CurrentCulture flows with the async context, so this cannot bleed
        // into parallel tests.
        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("th-TH");
        try
        {
            var points = new List<GpsData>
            {
                NewPoint(47.0, 11.0, new DateTime(2026, 1, 1, 8, 0, 10, DateTimeKind.Utc)),
            };
            var xml = GpxExportService.BuildGpx(NewActivity(), points).ToString();

            Assert.Contains(">2026-01-01T08:00:00Z<", xml, StringComparison.Ordinal); // <metadata><time>
            Assert.Contains(">2026-01-01T08:00:10Z<", xml, StringComparison.Ordinal); // <trkpt><time>
            Assert.DoesNotContain("2569", xml, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void BuildGpx_Pressure_EmittedUnderRbNamespace()
    {
        var points = new List<GpsData>
        {
            new() { GpsLat = 47.0, GpsLong = 11.0, DateTimeUtc = new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc), Pressure = 1013.25 },
            new() { GpsLat = 47.1, GpsLong = 11.1, DateTimeUtc = new DateTime(2026, 1, 1, 8, 0, 10, DateTimeKind.Utc) }, // no pressure → no extension
        };

        var xml = GpxExportService.BuildGpx(NewActivity(), points).ToString();

        Assert.Contains("xmlns:rb=", xml, StringComparison.Ordinal);
        Assert.Contains("<rb:pressure>1013.25</rb:pressure>", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildGpx_NoPressure_NoRbNamespaceOrElement()
    {
        var points = new List<GpsData>
        {
            new() { GpsLat = 47.0, GpsLong = 11.0, DateTimeUtc = new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc) },
        };

        var xml = GpxExportService.BuildGpx(NewActivity(), points).ToString();

        Assert.DoesNotContain("xmlns:rb=", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("pressure", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void Export_Roundtrip_SamePointCount_HeartRatePreserved()
    {
        var points = new List<GpsData>
        {
            NewPoint(47.0, 11.0, new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc)),
            NewPoint(47.1, 11.1, new DateTime(2026, 1, 1, 8, 0, 10, DateTimeKind.Utc)),
            NewPoint(47.2, 11.2, new DateTime(2026, 1, 1, 8, 0, 20, DateTimeKind.Utc)),
        };

        var doc = GpxExportService.BuildGpx(NewActivity(), points);

        using var ms = new MemoryStream();
        doc.Save(ms);
        ms.Position = 0;

        var parser = new GpxParsingService(NullLogger<GpxParsingService>.Instance);
        var result = parser.Parse(ms, "roundtrip.gpx").Single();

        Assert.True(result.Success);
        Assert.Equal(points.Count, result.GpsPoints.Count);
        Assert.All(result.GpsPoints, p => Assert.Equal(140, p.HeartRate));
    }

    // EX-03: XML-illegal control chars in a name must be stripped, not thrown, at doc.Save.
    [Fact]
    public void BuildGpx_ControlCharInName_SavesAndStrips()
    {
        var activity = NewActivity();
        activity.TrackName = "badname";

        var doc = GpxExportService.BuildGpx(activity, new List<GpsData>());

        var ex = Record.Exception(() => doc.Save(new MemoryStream()));
        Assert.Null(ex);

        XNamespace gpx = "http://www.topografix.com/GPX/1/1";
        var name = doc.Descendants(gpx + "name").First().Value;
        Assert.Equal("badname", name);
    }

    // EX-01: hostile track names must not survive into ZIP entry names / Content-Disposition.
    [Fact]
    public void SanitizeFileName_BackslashTraversal_NoSeparatorsNoLeadingDot()
    {
        var s = GpxExportService.SanitizeFileName(@"..\..\evil");
        Assert.DoesNotContain('/', s);
        Assert.DoesNotContain('\\', s);
        Assert.False(s.StartsWith('.'));
    }

    [Fact]
    public void SanitizeFileName_ForwardSlashTraversal_Neutralized()
    {
        var s = GpxExportService.SanitizeFileName("../../evil");
        Assert.DoesNotContain('/', s);
        Assert.DoesNotContain('\\', s);
        Assert.False(s.StartsWith('.'));
    }

    [Fact]
    public void SanitizeFileName_ControlChars_Replaced()
    {
        var s = GpxExportService.SanitizeFileName("a\r\nb");
        Assert.DoesNotContain('\r', s);
        Assert.DoesNotContain('\n', s);
    }

    [Fact]
    public void SanitizeFileName_DotsOnly_Empty()
    {
        Assert.Equal(string.Empty, GpxExportService.SanitizeFileName("..."));
    }

    [Fact]
    public void SanitizeFileName_LongName_Capped()
    {
        var s = GpxExportService.SanitizeFileName(new string('a', 300));
        Assert.True(s.Length <= 100);
    }
}
