using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using RouteBaseWeb.Models.Gpx;
using RouteBaseWeb.Models.Sync;
using RouteBaseWeb.Services.Interfaces;

namespace RouteBaseWeb.Services;

/// <summary>
/// Parses Garmin TCX (Training Center Database v2) files into the shared <see cref="GpxParseResult"/>.
/// A TCX Activity holds sequential Laps, each a Track of Trackpoints — laps concatenate into ONE
/// activity, but a file may hold SEVERAL Activities (Training Center's "export all"), and each of those
/// is its own ride: they are split apart like a chained FIT, one result each. Per-point distance/speed
/// and aggregates come from the
/// shared <see cref="TrackStatsCalculator"/>; Lap-level totals override where present.
/// TPX Speed is intentionally not parsed — <see cref="GeoUtils.SmoothSpeeds"/> overwrites per-point
/// speed for every format, so a parsed value would be clobbered anyway.
/// </summary>
public class TcxParsingService : ITcxParsingService
{
    private static readonly XNamespace Tcx = "http://www.garmin.com/xmlschemas/TrainingCenterDatabase/v2";
    private static readonly XNamespace Ext = "http://www.garmin.com/xmlschemas/ActivityExtension/v2";

    // Cap points defensively for parity with the FIT parser (upload size already bounds this).
    private const int MaxPoints = 1_000_000;

    private readonly ILogger<TcxParsingService> _logger;

    public TcxParsingService(ILogger<TcxParsingService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// One result per <c>&lt;Activity&gt;</c>. A Training Center "export all" bundle used to lose every
    /// activity after the first, silently — asymmetric with multi-track GPX (which warns) and chained FIT
    /// (which splits). Splitting matches the FIT behaviour, including its naming: the first keeps the
    /// base name (and the filename id when asked), later ones take a " (n)" suffix and a content id so
    /// siblings never dedupe against one shared external id.
    /// </summary>
    public IReadOnlyList<GpxParseResult> Parse(Stream stream, string fileName, bool useFilenameAsId = false)
    {
        XDocument doc;
        try
        {
            // Explicit XXE/DTD hardening — keep in sync with GpxParsingService.
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver   = null,
            });
            doc = XDocument.Load(reader);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse TCX XML from {FileName}", fileName);
            return [Failed($"Invalid XML: {ex.Message}")];
        }

        var activities = doc.Root?
            .Element(Tcx + "Activities")?
            .Elements(Tcx + "Activity").ToList() ?? [];
        if (activities.Count == 0)
            return [Failed("No activity found in TCX file.")];

        var results = new List<GpxParseResult>(activities.Count);
        var emitted = 0;
        foreach (var activity in activities)
        {
            var result = ParseSingle(activity, fileName, useFilenameAsId, emitted);
            results.Add(result);
            if (result.Success) emitted++;
        }

        // A bundle's one unusable session (indoor, no fixes) must not sink the rides around it — but a
        // file where NOTHING parsed still has to say why, so the first failure speaks for it.
        var usable = results.Where(r => r.Success).ToList();
        return usable.Count > 0 ? usable : [results[0]];
    }

    private static GpxParseResult Failed(string message) => new() { ErrorMessage = message };

    private GpxParseResult ParseSingle(XElement activity, string fileName, bool useFilenameAsId, int index)
    {
        var result = new GpxParseResult();

        var laps = activity.Elements(Tcx + "Lap").ToList();
        if (laps.Count == 0)
        {
            result.ErrorMessage = "No laps found in TCX file.";
            return result;
        }

        // --- Lap-level totals (summed across laps) ---
        double lapDistance = 0, lapTime = 0, lapCalories = 0, lapMaxSpeed = 0;
        bool anyDistance = false, anyTime = false, anyCalories = false, anyMaxSpeed = false;

        var points = new List<ExternalGpsPoint>();
        bool hasTimestamps = true;
        bool truncated = false;

        foreach (var lap in laps)
        {
            if (ParseDouble(lap.Element(Tcx + "DistanceMeters")?.Value) is { } d)   { lapDistance += d; anyDistance = true; }
            if (ParseDouble(lap.Element(Tcx + "TotalTimeSeconds")?.Value) is { } t)  { lapTime += t; anyTime = true; }
            if (ParseDouble(lap.Element(Tcx + "Calories")?.Value) is { } c)          { lapCalories += c; anyCalories = true; }
            if (ParseDouble(lap.Element(Tcx + "MaximumSpeed")?.Value) is { } ms)     { lapMaxSpeed = Math.Max(lapMaxSpeed, ms); anyMaxSpeed = true; }

            foreach (var tp in lap.Descendants(Tcx + "Trackpoint"))
            {
                if (points.Count >= MaxPoints) { truncated = true; break; }
                var pos = tp.Element(Tcx + "Position");
                var lat = ParseDouble(pos?.Element(Tcx + "LatitudeDegrees")?.Value);
                var lng = ParseDouble(pos?.Element(Tcx + "LongitudeDegrees")?.Value);
                // Skip missing (indoor/paused sample) and out-of-range/non-finite coordinates.
                if (lat is not (>= -90 and <= 90) || lng is not (>= -180 and <= 180))
                    continue;

                var pt = new ExternalGpsPoint
                {
                    Lat       = lat.Value,
                    Lng       = lng.Value,
                    Altitude  = ParseDouble(tp.Element(Tcx + "AltitudeMeters")?.Value),
                    HeartRate = ParseDouble(tp.Element(Tcx + "HeartRateBpm")?.Element(Tcx + "Value")?.Value),
                    Cadence   = ParseDouble(tp.Element(Tcx + "Cadence")?.Value),
                };

                var tpx = tp.Element(Tcx + "Extensions")?.Descendants(Ext + "TPX").FirstOrDefault();
                if (tpx != null)
                {
                    pt.Power ??= ParseDouble(tpx.Element(Ext + "Watts")?.Value);
                    // Run cadence lives in the extension when the top-level Cadence is absent.
                    pt.Cadence ??= ParseDouble(tpx.Element(Ext + "RunCadence")?.Value);
                }

                if (TrackTimestamp.TryParseUtc(tp.Element(Tcx + "Time")?.Value, out var ts))
                    pt.TimestampUtc = ts;
                else
                    hasTimestamps = false;

                points.Add(pt);
            }
            if (truncated) break;
        }

        if (truncated)
            result.Warnings.Add($"Track truncated to the first {MaxPoints} points.");

        // Each lap's DistanceMeters/TotalTimeSeconds/Calories passes the per-value finite check,
        // but the cross-lap SUM is unchecked and can overflow to +Infinity — reject here so a
        // non-finite total never reaches the DB (System.Text.Json 500s serializing it back out).
        if ((anyDistance && !double.IsFinite(lapDistance)) ||
            (anyTime && !double.IsFinite(lapTime)) ||
            (anyCalories && !double.IsFinite(lapCalories)))
        {
            result.ErrorMessage = "TCX file has non-finite lap totals (distance, time, or calories).";
            return result;
        }

        if (points.Count == 0)
        {
            result.ErrorMessage = "TCX file contains no valid track points.";
            return result;
        }

        if (!hasTimestamps)
            result.Warnings.Add("Some or all track points are missing timestamps; time-based stats skipped.");

        // --- Shared post-processing ---
        var stats = TrackStatsCalculator.Compute(points, hasTimestamps);

        var firstPt = points.First();
        var lastPt  = points.Last();

        var baseName = Path.GetFileNameWithoutExtension(fileName);
        // Only the first emitted activity may claim the filename id: siblings sharing one external id
        // would dedupe against each other on re-import (the FIT split rule).
        long? filenameId = index == 0 && useFilenameAsId && long.TryParse(baseName, System.Globalization.CultureInfo.InvariantCulture, out var parsedId)
            ? parsedId : null;

        var name = index == 0 ? baseName : $"{baseName} ({index + 1})";
        var sport = activity.Attribute("Sport")?.Value;

        var summary = new ExternalActivitySummary
        {
            // Match the GPX id scheme so re-imports dedupe-skip (GI-07).
            ExternalId   = filenameId ?? stats.StartUtc?.Ticks
                           ?? TrackStatsCalculator.ComputeContentId(points, stats.Distance),
            ProviderId   = filenameId.HasValue ? "strava" : "tcx",
            Source       = filenameId.HasValue ? "strava" : "tcx",
            Name         = name,
            ActivityType = MapSport(sport),
            StartDateUtc = stats.StartUtc,
            StartDateLocal = stats.StartUtc,   // no timezone info in TCX — use UTC as-is
            StartLat     = firstPt.Lat,
            StartLng     = firstPt.Lng,
            EndLat       = lastPt.Lat,
            EndLng       = lastPt.Lng,

            // Prefer lap totals; fall back to computed values.
            Distance      = anyDistance ? lapDistance : stats.Distance,
            ElevationGain = stats.ElevationGain > 0 ? stats.ElevationGain : null,
            TotalDescent  = stats.ElevationLoss > 0 ? stats.ElevationLoss : null,
            Calories      = anyCalories ? lapCalories : null,
            // Elapsed = wall-clock first-to-last span (includes pauses); lap timer is TotalTime (FP-07).
            ElapsedTime   = stats.ElapsedSeconds ?? (anyTime ? lapTime : null),
            TotalTime     = anyTime ? lapTime : stats.ElapsedSeconds,
            MovingTime    = stats.MovingTime,
            AverageSpeed  = stats.AverageSpeed,
            MaxSpeed      = anyMaxSpeed ? lapMaxSpeed : stats.MaxSpeed,
            AverageHeartRate = stats.AverageHeartRate,
            MaxHeartRate  = stats.MaxHeartRate,
            AverageCadence = stats.AverageCadence,
            MaxCadence    = stats.MaxCadence,
            AveragePower  = stats.AveragePower,
            MaxPower      = stats.MaxPower,
        };

        result.Success   = true;
        result.Summary   = summary;
        result.GpsPoints = points;
        return result;
    }

    private static string? MapSport(string? sport) => sport?.ToUpperInvariant() switch
    {
        "RUNNING" => "Run",
        "BIKING"  => "Ride",
        "WALKING" => "Walk",
        "HIKING"  => "Hike",
        "SWIMMING" => "Swim",
        null or "" or "OTHER" => "Walk",
        _ => SanitizeType(sport),
    };

    // Passthrough types are attacker-controlled: strip control chars, trim, cap length.
    private static string SanitizeType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "Walk";
        var cleaned = new string(value.Where(c => c >= 0x20).ToArray()).Trim();
        if (cleaned.Length == 0) return "Walk";
        return cleaned.Length > 50 ? cleaned[..50] : cleaned;
    }

    private static double? ParseDouble(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            && double.IsFinite(d) ? d : null;
    }
}
