using System.Xml;
using System.Xml.Linq;
using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Gpx;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Models.Sync;

namespace RouteBaseWeb.Services;

/// <summary>
/// Parses GPX 1.1 files from Garmin devices, Strava exports, and other standard sources.
/// Handles both minimal GPX (Strava-style: lat/lon/ele/time) and Garmin-extended GPX
/// (track-level stats + per-point HR/cadence/power).
/// </summary>
public class GpxParsingService : IGpxParsingService
{
    // Standard namespaces
    private static readonly XNamespace GpxNs    = "http://www.topografix.com/GPX/1/1";
    private static readonly XNamespace GpxtpxNs = "http://www.garmin.com/xmlschemas/TrackPointExtension/v1";
    private static readonly XNamespace GpxtrkxNs = "http://www.garmin.com/xmlschemas/TrackStatsExtension/v1";

    // Cap points defensively for parity with the TCX/FIT parsers (upload size already bounds this).
    private const int MaxPoints = 1_000_000;

    private readonly ILogger<GpxParsingService> _logger;

    public GpxParsingService(ILogger<GpxParsingService> logger)
    {
        _logger = logger;
    }

    // GPX is single-session: always one result. The list contract matches the FIT parser's split shape.
    public IReadOnlyList<GpxParseResult> Parse(Stream gpxStream, string fileName, bool useFilenameAsId = false)
        => [ParseSingle(gpxStream, fileName, useFilenameAsId)];

    private GpxParseResult ParseSingle(Stream gpxStream, string fileName, bool useFilenameAsId)
    {
        var result = new GpxParseResult();

        XDocument doc;
        try
        {
            // Explicit XXE/DTD hardening — keep in sync with TcxParsingService.
            using var reader = XmlReader.Create(gpxStream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver   = null,
            });
            doc = XDocument.Load(reader);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse GPX XML from {FileName}", fileName);
            result.ErrorMessage = $"Invalid XML: {ex.Message}";
            return result;
        }

        var root = doc.Root;
        if (root == null)
        {
            result.ErrorMessage = "Empty GPX file.";
            return result;
        }

        // Find <trk> elements, supporting both namespaced and namespace-free GPX
        var tracks = FindElements(root, "trk").ToList();
        if (tracks.Count == 0)
        {
            result.ErrorMessage = "No tracks found in GPX file.";
            return result;
        }
        if (tracks.Count > 1)
            result.Warnings.Add($"Multiple tracks found ({tracks.Count}), imported first only.");

        var trk = tracks[0];

        // --- Track-level metadata ---
        // <name> is sanitized here to decide the <name>-vs-filename fallback; the filename fallback
        // itself is sanitized once at the router seam (BUG-1), covering TCX/FIT identically.
        var name = ImportNameSanitizer.Sanitize(FindElement(trk, "name")?.Value)
                   ?? Path.GetFileNameWithoutExtension(fileName);
        var type = FindElement(trk, "type")?.Value?.Trim();

        // --- Garmin track-level extensions ---
        double? extDistance = null, extAscent = null, extDescent = null, extCalories = null;
        double? extAvgHr = null, extAvgCadence = null, extTimerTime = null, extElapsedTime = null;

        var trkExtensions = FindElement(trk, "extensions");
        if (trkExtensions != null)
        {
            var statsExt = trkExtensions.Descendants(GpxtrkxNs + "TrackStatsExtension").FirstOrDefault();
            if (statsExt != null)
            {
                extDistance    = ParseDouble(statsExt.Element(GpxtrkxNs + "Distance")?.Value);
                extAscent      = ParseDouble(statsExt.Element(GpxtrkxNs + "Ascent")?.Value);
                extDescent     = ParseDouble(statsExt.Element(GpxtrkxNs + "Descent")?.Value);
                extCalories    = ParseDouble(statsExt.Element(GpxtrkxNs + "Calories")?.Value);
                extAvgHr       = ParseDouble(statsExt.Element(GpxtrkxNs + "AvgHeartRate")?.Value);
                extAvgCadence  = ParseDouble(statsExt.Element(GpxtrkxNs + "AvgCadence")?.Value);
                extTimerTime   = ParseDouble(statsExt.Element(GpxtrkxNs + "TimerTime")?.Value);
                extElapsedTime = ParseDouble(statsExt.Element(GpxtrkxNs + "TotalElapsedTime")?.Value);
            }
        }

        // --- GPS points (all segments concatenated) ---
        var trksegs = FindElements(trk, "trkseg").ToList();
        var points = new List<ExternalGpsPoint>();
        bool hasTimestamps = true;
        bool truncated = false;

        foreach (var seg in trksegs)
        {
            foreach (var trkpt in FindElements(seg, "trkpt"))
            {
                if (points.Count >= MaxPoints) { truncated = true; break; }

                var latAttr = trkpt.Attribute("lat")?.Value;
                var lonAttr = trkpt.Attribute("lon")?.Value;

                if (!double.TryParse(latAttr, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var lat) ||
                    !double.TryParse(lonAttr, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var lon) ||
                    !double.IsFinite(lat) || !double.IsFinite(lon) ||
                    lat is < -90 or > 90 || lon is < -180 or > 180)
                    continue;

                var pt = new ExternalGpsPoint { Lat = lat, Lng = lon };

                var eleVal = FindElement(trkpt, "ele")?.Value;
                if (!string.IsNullOrEmpty(eleVal))
                    pt.Altitude = ParseDouble(eleVal);

                var timeVal = FindElement(trkpt, "time")?.Value;
                if (TrackTimestamp.TryParseUtc(timeVal, out var ts))
                    pt.TimestampUtc = ts;
                else
                    hasTimestamps = false;

                // Garmin point-level extensions
                var ptExt = FindElement(trkpt, "extensions");
                if (ptExt != null)
                {
                    var tpx = ptExt.Descendants(GpxtpxNs + "TrackPointExtension").FirstOrDefault();
                    if (tpx != null)
                    {
                        pt.HeartRate = ParseDouble(tpx.Element(GpxtpxNs + "hr")?.Value);
                        pt.Cadence   = ParseDouble(tpx.Element(GpxtpxNs + "cad")?.Value);
                        pt.Power     = ParseDouble(tpx.Element(GpxtpxNs + "power")?.Value);
                    }
                }

                points.Add(pt);
            }
            if (truncated) break;
        }

        if (truncated)
            result.Warnings.Add($"Track truncated to the first {MaxPoints} points.");

        if (points.Count == 0)
        {
            result.ErrorMessage = "GPX file contains no valid track points.";
            return result;
        }

        if (!hasTimestamps)
            result.Warnings.Add("Some or all track points are missing timestamps; time-based stats skipped.");

        // --- Shared post-processing: per-point distance/speed + track aggregates ---
        var stats = TrackStatsCalculator.Compute(points, hasTimestamps);

        var firstPt = points.First();
        var lastPt  = points.Last();

        long? filenameId = useFilenameAsId && long.TryParse(
            Path.GetFileNameWithoutExtension(fileName), System.Globalization.CultureInfo.InvariantCulture, out var parsedId) ? parsedId : null;

        var summary = new ExternalActivitySummary
        {
            // GI-07: timestamped files keep startUtc.Ticks (backward compatible with prior imports);
            // timestamp-less files get a deterministic content id so re-importing the same file
            // dedupe-skips instead of duplicating (was DateTime.UtcNow.Ticks — random per run).
            ExternalId   = filenameId ?? stats.StartUtc?.Ticks
                           ?? TrackStatsCalculator.ComputeContentId(points, stats.Distance),
            ProviderId   = filenameId.HasValue ? "strava" : "gpx",
            Source       = filenameId.HasValue ? "strava" : "gpx",
            Name         = name,
            ActivityType = MapActivityType(type),
            StartDateUtc = stats.StartUtc,
            StartDateLocal = stats.StartUtc,   // no timezone info in GPX — use UTC as-is
            StartLat     = firstPt.Lat,
            StartLng     = firstPt.Lng,
            EndLat       = lastPt.Lat,
            EndLng       = lastPt.Lng,

            // Prefer Garmin track-level extensions; fall back to computed values
            Distance      = extDistance    ?? stats.Distance,
            ElevationGain = extAscent      ?? (stats.ElevationGain > 0 ? stats.ElevationGain : null),
            TotalDescent  = extDescent     ?? (stats.ElevationLoss > 0 ? stats.ElevationLoss : null),
            Calories      = extCalories,
            ElapsedTime   = extElapsedTime ?? stats.ElapsedSeconds,
            TotalTime     = extTimerTime   ?? stats.ElapsedSeconds,
            MovingTime    = stats.MovingTime,
            AverageSpeed  = stats.AverageSpeed,
            MaxSpeed      = stats.MaxSpeed,
            AverageHeartRate = extAvgHr    ?? stats.AverageHeartRate,
            MaxHeartRate  = stats.MaxHeartRate,
            AverageCadence = extAvgCadence ?? stats.AverageCadence,
            MaxCadence    = stats.MaxCadence,
            AveragePower  = stats.AveragePower,
            MaxPower      = stats.MaxPower,
        };

        result.Success   = true;
        result.Summary   = summary;
        result.GpsPoints = points;
        return result;
    }

    // --- Helpers ---

    /// <summary>
    /// Finds direct/descendant elements by local name, tolerating both namespaced and
    /// namespace-free GPX (produced by some third-party tools).
    /// </summary>
    private static IEnumerable<XElement> FindElements(XElement parent, string localName)
    {
        // First try with GPX namespace (most files), then fall back to no namespace
        var ns = parent.GetDefaultNamespace();
        var nsResult = parent.Elements(ns + localName);
        if (nsResult.Any()) return nsResult;

        var gpxResult = parent.Elements(GpxNs + localName);
        if (gpxResult.Any()) return gpxResult;

        return parent.Elements().Where(e => e.Name.LocalName == localName);
    }

    private static XElement? FindElement(XElement parent, string localName)
        => FindElements(parent, localName).FirstOrDefault();

    private static double? ParseDouble(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return double.TryParse(value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var d)
            && double.IsFinite(d) ? d : null;
    }

    private static string? MapActivityType(string? gpxType)
    {
        if (string.IsNullOrWhiteSpace(gpxType)) return "Walk";
        return gpxType.ToUpperInvariant() switch
        {
            "RUNNING" or "RUN" => "Run",
            "CYCLING" or "RIDE" or "BIKING" => "Ride",
            "HIKING" or "HIKE" => "Hike",
            "WALKING" or "WALK" => "Walk",
            "SWIMMING" or "SWIM" => "Swim",
            _ => SanitizeType(gpxType)
        };
    }

    // Passthrough <type> is attacker-controlled: strip control chars, trim, cap length.
    private static string SanitizeType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "Walk";
        var cleaned = new string(value.Where(c => c >= 0x20).ToArray()).Trim();
        if (cleaned.Length == 0) return "Walk";
        return cleaned.Length > 50 ? cleaned[..50] : cleaned;
    }
}
