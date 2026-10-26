using RouteBaseWeb.Models;
using System.Text;
using System.Xml.Linq;
using RouteBaseWeb.Services.Interfaces;

namespace RouteBaseWeb.Services;

/// <summary>
/// Builds a GPX 1.1 document from a stored activity and its GPS points.
/// </summary>
public class GpxExportService : IGpxExportService
{
    private static readonly XNamespace GpxNs    = "http://www.topografix.com/GPX/1/1";
    private static readonly XNamespace XsiNs     = "http://www.w3.org/2001/XMLSchema-instance";
    private static readonly XNamespace GpxtpxNs  = "http://www.garmin.com/xmlschemas/TrackPointExtension/v1";
    // Raw barometric pressure (hPa) has no standard GPX/gpxtpx element, so it goes under our own foreign
    // namespace. GPX 1.1 <extensions> is open content, so strict gpxtpx validators ignore it.
    private static readonly XNamespace RbNs     = "http://routebase/xmlschemas/RecordingExtension/v1";

    private readonly IActivityQueryService _activityQuery;
    private readonly IActivityGpsService _activityGps;
    private readonly ILogger<GpxExportService> _logger;

    public GpxExportService(
        IActivityQueryService activityQuery,
        IActivityGpsService activityGps,
        ILogger<GpxExportService> logger)
    {
        _activityQuery = activityQuery;
        _activityGps   = activityGps;
        _logger        = logger;
    }

    public async Task<(Stream? Stream, string FileName)?> ExportAsync(TrackId trackId, CancellationToken ct = default)
    {
        var activity = await _activityQuery.GetActivityMetadataByIdAsync(trackId.Value);
        if (activity == null) return null;

        var gpsData = await _activityGps.GetGpsDataForTrackAsync(trackId, ct);

        var doc = BuildGpx(activity, gpsData);

        var ms = new MemoryStream();
        doc.Save(ms, SaveOptions.None);
        ms.Position = 0;

        var safeName = SanitizeFileName(activity.TrackName ?? $"activity_{trackId}");
        if (safeName.Length == 0) safeName = $"activity_{trackId}";
        var fileName = $"{safeName}.gpx";

        return (ms, fileName);
    }

    // internal (not private): GI-13 tests build the XDocument directly without a DB round-trip.
    internal static XDocument BuildGpx(Models.Activity activity, List<Models.GpsData> gpsData)
    {
        // GI-13: only hr/cad/power are in TrackPointExtension v1 (power is non-schema but
        // de-facto standard, kept); speed/grade/distance aren't and our own importer ignores
        // them anyway (recomputed from lat/lon/time) — drop them for export/import symmetry
        // and compatibility with strict validators / Garmin Connect.
        bool hasExtensions = gpsData.Any(p =>
            p.HeartRate.HasValue || p.Cadence.HasValue || p.Power.HasValue);
        // Barometric pressure is independent of the gpxtpx channels — our recordings carry pressure but
        // no hr/cad/power, so it gets its own presence flag + namespace declaration.
        bool hasPressure = gpsData.Any(p => p.Pressure.HasValue);

        var gpx = new XElement(GpxNs + "gpx",
            new XAttribute("version", "1.1"),
            new XAttribute("creator", "RouteBase"),
            new XAttribute(XNamespace.Xmlns + "xsi", XsiNs.NamespaceName),
            new XAttribute(XsiNs + "schemaLocation",
                "http://www.topografix.com/GPX/1/1 http://www.topografix.com/GPX/1/1/gpx.xsd"),
            hasExtensions
                ? new XAttribute(XNamespace.Xmlns + "gpxtpx", GpxtpxNs.NamespaceName)
                : null,       // XElement ignores null content — no attribute is emitted
            hasPressure
                ? new XAttribute(XNamespace.Xmlns + "rb", RbNs.NamespaceName)
                : null
        );

        // <metadata>
        if (activity.StartDateTimeUtc.HasValue)
        {
            gpx.Add(new XElement(GpxNs + "metadata",
                new XElement(GpxNs + "time",
                    activity.StartDateTimeUtc.Value.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture))));
        }

        // <trk>
        var trk = new XElement(GpxNs + "trk",
            new XElement(GpxNs + "name", XmlSafe(activity.TrackName ?? string.Empty)));

#pragma warning disable CA1308 // GPX <type> is a wire value, conventionally lowercase
        if (!string.IsNullOrEmpty(activity.ActivityType))
            trk.Add(new XElement(GpxNs + "type", XmlSafe(activity.ActivityType).ToLowerInvariant()));
#pragma warning restore CA1308

        // <trkseg>
        var trkseg = new XElement(GpxNs + "trkseg");
        foreach (var pt in gpsData)
        {
            if (!pt.GpsLat.HasValue || !pt.GpsLong.HasValue) continue;

            var trkpt = new XElement(GpxNs + "trkpt",
                new XAttribute("lat", pt.GpsLat.Value.ToString("G17", System.Globalization.CultureInfo.InvariantCulture)),
                new XAttribute("lon", pt.GpsLong.Value.ToString("G17", System.Globalization.CultureInfo.InvariantCulture)));

            if (pt.Altitude.HasValue)
                trkpt.Add(new XElement(GpxNs + "ele",
                    pt.Altitude.Value.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)));

            if (pt.DateTimeUtc.HasValue)
                trkpt.Add(new XElement(GpxNs + "time",
                    pt.DateTimeUtc.Value.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture)));

            bool ptHasTpx = pt.HeartRate.HasValue || pt.Cadence.HasValue || pt.Power.HasValue;
            if (ptHasTpx || pt.Pressure.HasValue)
            {
                var extensions = new XElement(GpxNs + "extensions");
                if (ptHasTpx)
                {
                    var tpx = new XElement(GpxtpxNs + "TrackPointExtension");
                    if (pt.HeartRate.HasValue)
                        tpx.Add(new XElement(GpxtpxNs + "hr", (int)pt.HeartRate.Value));
                    if (pt.Cadence.HasValue)
                        tpx.Add(new XElement(GpxtpxNs + "cad", (int)pt.Cadence.Value));
                    if (pt.Power.HasValue)
                        tpx.Add(new XElement(GpxtpxNs + "power", (int)pt.Power.Value));
                    extensions.Add(tpx);
                }
                if (pt.Pressure.HasValue)
                    extensions.Add(new XElement(RbNs + "pressure",
                        pt.Pressure.Value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)));

                trkpt.Add(extensions);
            }

            trkseg.Add(trkpt);
        }

        trk.Add(trkseg);
        gpx.Add(trk);

        return new XDocument(new XDeclaration("1.0", "utf-8", null), gpx);
    }

    // EX-03: XmlWriter throws on chars XML 1.0 forbids (control chars other than tab/LF/CR).
    // Strava/FIT/filename-derived names can carry them → one poisoned name would 500 its single
    // export and kill the whole bulk ZIP mid-stream. Strip them before doc.Save.
    private static string XmlSafe(string s) => string.Concat(s.Where(System.Xml.XmlConvert.IsXmlChar));

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    // internal static: EX-01 direct unit tests + platform-independent sanitizing.
    // Path.GetInvalidFileNameChars() is only slash+NUL on Linux (the prod box), letting backslash
    // zip-slip, control-char header crashes (Content-Disposition), and Windows device names survive.
    // Empty result → caller falls back to activity_{id}; in-zip collisions handled by UniqueName.
    internal static string SanitizeFileName(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            bool bad = c < 0x20
                || c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' or '\0';
            sb.Append(bad ? '_' : c);
        }

        var result = sb.ToString().Trim().Trim('.', ' ');
        if (result.Length > 100) result = result[..100];
        if (ReservedDeviceNames.Contains(result)) result = "_" + result;

        return result;
    }
}
