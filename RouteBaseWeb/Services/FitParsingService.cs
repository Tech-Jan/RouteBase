using Dynastream.Fit;
using RouteBaseWeb.Models.Gpx;
using RouteBaseWeb.Models.Sync;
using RouteBaseWeb.Services.Interfaces;
using DateTime = System.DateTime;

namespace RouteBaseWeb.Services;

/// <summary>
/// Parses Garmin FIT (binary) activity files via the official Garmin FIT SDK (namespace
/// <c>Dynastream.Fit</c>). Record messages become GPS points (lat/long in semicircles →
/// degrees; SDK converts the FIT epoch); session message supplies device-computed totals.
/// Output is the shared <see cref="GpxParseResult"/> and shared <see cref="TrackStatsCalculator"/>
/// post-processing, identical to GPX/TCX.
/// </summary>
public class FitParsingService : IFitParsingService
{
    // Semicircle → degrees: degrees = semicircles × 180 / 2^31.
    private const double SemicircleToDegrees = 180.0 / 2147483648.0;

    // Bound memory against a pathological/huge file (24 h @ 1 Hz ≈ 86 400 points).
    private const int MaxRecords = 1_000_000;

    private const uint InvalidTimestamp = 0xFFFFFFFF;

    private readonly ILogger<FitParsingService> _logger;

    public FitParsingService(ILogger<FitParsingService> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<GpxParseResult> Parse(Stream stream, string fileName, bool useFilenameAsId = false)
    {
        // OBS-2: a FIT stream can be several complete files concatenated (chained). Decode.Read raises
        // FileIdMesgEvent once per file header, so we bucket records + sessions per header and emit one
        // activity per file. A single file (even a multisport one with several sessions) has ONE header
        // → one segment → one activity, so a triathlon is never shattered.
        var files = new List<FitFile>();
        FitFile? current = null;
        int totalPoints = 0;
        bool intact = false;

        // Records may (defensively) arrive before the first FileId in a malformed stream; give them an
        // implicit initial segment rather than dropping them.
        FitFile Current() => current ??= NewFile(files);

        try
        {
            var decode = new Decode();
            var broadcaster = new MesgBroadcaster();
            decode.MesgEvent += broadcaster.OnMesg;
            decode.MesgDefinitionEvent += broadcaster.OnMesgDefinition;

            broadcaster.FileIdMesgEvent += (_, _) => current = NewFile(files);

            broadcaster.RecordMesgEvent += (_, e) =>
            {
                if (totalPoints >= MaxRecords) { Current().Truncated = true; return; }
                var pt = BuildPoint((RecordMesg)e.mesg, out bool hadTimestamp);
                if (pt == null) return; // no GPS fix / out-of-range position

                var file = Current();
                if (!hadTimestamp) file.HasTimestamps = false;
                file.Points.Add(pt);
                totalPoints++;
            };
            broadcaster.SessionMesgEvent += (_, e) => Current().Sessions.Add((SessionMesg)e.mesg);

            long start = stream.CanSeek ? stream.Position : 0;
            if (stream.CanSeek)
            {
                if (!decode.IsFIT(stream))
                    return [Fail("Not a valid FIT file.")];
                stream.Position = start;
            }
            intact = decode.Read(stream);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to decode FIT file {FileName}", fileName);
            return [Fail($"Failed to decode FIT: {ex.Message}")];
        }

        // The SDK reports CRC failure / garbled-mid-file corruption by returning false (not throwing);
        // fail closed so silently-wrong coordinates never persist as a clean import (FP-01).
        if (!intact)
            return [Fail("FIT file failed integrity check (corrupt or truncated data).")];

        // Drop segments with no usable GPS points (e.g. an indoor sub-file in a chain). All empty →
        // the whole import has no track — same failure as a single point-less file.
        var withPoints = files.Where(f => f.Points.Count > 0).ToList();
        if (withPoints.Count == 0)
            return [Fail("FIT file contains no GPS track points.")];

        var results = new List<GpxParseResult>(withPoints.Count);
        for (int i = 0; i < withPoints.Count; i++)
            results.Add(BuildResult(withPoints[i], fileName, useFilenameAsId, i));
        return results;
    }

    /// <summary>
    /// Builds one activity result from a single decoded file segment. <paramref name="index"/> is the
    /// 0-based position among the emitted activities: index 0 keeps the base name (and, when
    /// <paramref name="useFilenameAsId"/>, the raw filename id); later split pieces get a " (n)" suffix
    /// and a content-derived id so chained siblings never dedupe against one shared external id.
    /// </summary>
    private static GpxParseResult BuildResult(FitFile file, string fileName, bool useFilenameAsId, int index)
    {
        var points  = file.Points;
        var session = file.Sessions.FirstOrDefault(); // multisport single file: totals from the first session
        bool hasTimestamps = file.HasTimestamps;

        var result = new GpxParseResult();
        if (file.Truncated)
            result.Warnings.Add($"Track truncated to the first {MaxRecords} points.");
        if (!hasTimestamps)
            result.Warnings.Add("Some or all track points are missing timestamps; time-based stats skipped.");

        // --- Shared post-processing (per file) — correct per-file totals, not first-session-only ---
        var stats = TrackStatsCalculator.Compute(points, hasTimestamps);

        var firstPt = points.First();
        var lastPt  = points.Last();

        var baseName = Path.GetFileNameWithoutExtension(fileName);
        long? filenameId = index == 0 && useFilenameAsId && long.TryParse(baseName, System.Globalization.CultureInfo.InvariantCulture, out var parsedId)
            ? parsedId : null;

        var summary = new ExternalActivitySummary
        {
            ExternalId   = filenameId ?? stats.StartUtc?.Ticks
                           ?? TrackStatsCalculator.ComputeContentId(points, stats.Distance),
            ProviderId   = filenameId.HasValue ? "strava" : "fit",
            Source       = filenameId.HasValue ? "strava" : "fit",
            Name         = index == 0 ? baseName : $"{baseName} ({index + 1})",
            ActivityType = MapSport(session?.GetSport()),
            StartDateUtc = stats.StartUtc,
            StartDateLocal = stats.StartUtc,   // no timezone info in FIT — use UTC as-is
            StartLat     = firstPt.Lat,
            StartLng     = firstPt.Lng,
            EndLat       = lastPt.Lat,
            EndLng       = lastPt.Lng,

            // Prefer device session totals; fall back to computed values.
            Distance      = D(session?.GetTotalDistance())    ?? stats.Distance,
            ElevationGain = D(session?.GetTotalAscent())      ?? (stats.ElevationGain > 0 ? stats.ElevationGain : null),
            TotalDescent  = D(session?.GetTotalDescent())     ?? (stats.ElevationLoss > 0 ? stats.ElevationLoss : null),
            Calories      = D(session?.GetTotalCalories()),
            ElapsedTime   = D(session?.GetTotalElapsedTime()) ?? stats.ElapsedSeconds,
            TotalTime     = D(session?.GetTotalTimerTime())   ?? stats.ElapsedSeconds,
            MovingTime    = stats.MovingTime,
            AverageSpeed  = D(session?.GetEnhancedAvgSpeed()) ?? D(session?.GetAvgSpeed()) ?? stats.AverageSpeed,
            MaxSpeed      = D(session?.GetEnhancedMaxSpeed()) ?? D(session?.GetMaxSpeed()) ?? stats.MaxSpeed,
            AverageHeartRate = D(session?.GetAvgHeartRate())  ?? stats.AverageHeartRate,
            MaxHeartRate  = D(session?.GetMaxHeartRate())     ?? stats.MaxHeartRate,
            AverageCadence = D(session?.GetAvgCadence())      ?? stats.AverageCadence,
            MaxCadence    = D(session?.GetMaxCadence())       ?? stats.MaxCadence,
            AveragePower  = D(session?.GetAvgPower())         ?? stats.AveragePower,
            MaxPower      = D(session?.GetMaxPower())         ?? stats.MaxPower,
        };

        result.Success   = true;
        result.Summary   = summary;
        result.GpsPoints = points;
        return result;
    }

    /// <summary>
    /// Maps one FIT RecordMesg to an <see cref="ExternalGpsPoint"/>, or null when the sample has no
    /// usable GPS fix (indoor/paused) or an out-of-range semicircle position. <paramref name="hadTimestamp"/>
    /// is false when the sample lacks a valid timestamp — the point is still kept, but time-based stats
    /// are skipped for its file.
    /// </summary>
    private static ExternalGpsPoint? BuildPoint(RecordMesg rec, out bool hadTimestamp)
    {
        hadTimestamp = true;

        var latSc = rec.GetPositionLat();
        var lngSc = rec.GetPositionLong();
        if (latSc == null || lngSc == null)
            return null; // no GPS fix (indoor/paused sample)

        var lat = latSc.Value * SemicircleToDegrees;
        var lng = lngSc.Value * SemicircleToDegrees;
        if (lat is < -90 or > 90 || lng is < -180 or > 180)
            return null; // out-of-range semicircle position

        var pt = new ExternalGpsPoint
        {
            Lat       = lat,
            Lng       = lng,
            Altitude  = D(rec.GetEnhancedAltitude()) ?? D(rec.GetAltitude()),
            HeartRate = D(rec.GetHeartRate()),
            Cadence   = D(rec.GetCadence()),
            Power     = D(rec.GetPower()),
        };

        var ts = rec.GetTimestamp();
        if (ts != null && ts.GetTimeStamp() is not (0 or InvalidTimestamp))
            pt.TimestampUtc = DateTime.SpecifyKind(ts.GetDateTime(), DateTimeKind.Utc);
        else
            hadTimestamp = false;

        return pt;
    }

    private static FitFile NewFile(List<FitFile> files)
    {
        var f = new FitFile();
        files.Add(f);
        return f;
    }

    private static GpxParseResult Fail(string message) =>
        new() { Success = false, ErrorMessage = message };

    /// <summary>One decoded FIT file within a (possibly chained) stream: its own points + sessions.</summary>
    private sealed class FitFile
    {
        public List<ExternalGpsPoint> Points { get; } = [];
        public List<SessionMesg> Sessions { get; } = [];
        public bool HasTimestamps { get; set; } = true;
        public bool Truncated { get; set; }
    }

    private static string MapSport(Sport? sport) => sport switch
    {
        Sport.Running                 => "Run",
        Sport.Cycling or Sport.EBiking => "Ride",
        Sport.Walking                 => "Walk",
        Sport.Hiking                  => "Hike",
        Sport.Swimming                => "Swim",
        null or Sport.Generic         => "Walk",
        _                             => SanitizeType(sport.ToString()),
    };

    // Unknown FIT sports render as numeric strings; strip control chars, trim, cap length.
    private static string SanitizeType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "Walk";
        var cleaned = new string(value.Where(c => c >= 0x20).ToArray()).Trim();
        if (cleaned.Length == 0) return "Walk";
        return cleaned.Length > 50 ? cleaned[..50] : cleaned;
    }

    // Widen the SDK's numeric channel types to the model's double? without repeating null checks.
    private static double? D(float? v)  => v.HasValue ? v.Value : null;
    private static double? D(byte? v)   => v.HasValue ? v.Value : null;
    private static double? D(ushort? v) => v.HasValue ? v.Value : null;
}
