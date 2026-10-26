using System.Text.Json;
using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Imports;
using RouteBaseWeb.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using RouteBaseWeb.Models.Sync;
using RouteBaseWeb.Models.Strava;

namespace RouteBaseWeb.Services;

/// <summary>
/// Coordinates Strava-specific import: fetches streams + detail via <see cref="IStravaApiClient"/>,
/// maps to provider-agnostic models, and delegates persistence to <see cref="IActivityImportService"/>.
/// </summary>
public class StravaImportOrchestrator : IStravaImportOrchestrator
{
    private readonly RouteBaseDbContext _context;
    private readonly IStravaApiClient _apiClient;
    private readonly IActivityImportService _importService;
    private readonly ILogger<StravaImportOrchestrator> _logger;

    public StravaImportOrchestrator(
        RouteBaseDbContext context,
        IStravaApiClient apiClient,
        IActivityImportService importService,
        ILogger<StravaImportOrchestrator> logger)
    {
        _context = context;
        _apiClient = apiClient;
        _importService = importService;
        _logger = logger;
    }

    public async Task<Activity?> ImportActivityAsync(StravaActivity stravaActivity, string? userId = null)
    {
        var query = _context.Activities.Where(a => a.ApiId == stravaActivity.Id);
        if (userId != null) query = query.Where(a => a.UserId == userId);
        var existingActivity = await query.FirstOrDefaultAsync();
        if (existingActivity != null)
        {
            _logger.LogInformation("Activity {ActivityId} already exists", stravaActivity.Id);
            return existingActivity;
        }

        var streamsTask = _apiClient.GetActivityStreamsAsync(stravaActivity.Id);
        var detailTask  = _apiClient.GetActivityDetailAsync(stravaActivity.Id);
        await Task.WhenAll(streamsTask, detailTask);
        var streams = await streamsTask;
        if (streams == null)
        {
            // Fetch failed (not "no GPS"). Persisting now would strip GPS permanently — abort.
            _logger.LogWarning("Stream fetch failed for {Id}; not importing", stravaActivity.Id);
            return null;
        }
        var detail  = await detailTask;
        var summary   = MapToExternalSummary(stravaActivity, detail);
        var gpsPoints = ConvertStreamsToGpsPoints(streams,
            stravaActivity.StartDate);

        return await _importService.ImportFromSummaryAsync(summary, gpsPoints, userId ?? "");
    }

    public async Task<(int imported, int skipped, int failed)> ImportActivitiesParallelAsync(
        IReadOnlyList<ExternalActivitySummary> activities,
        string userId,
        Action<int, string, int, int, int> progressCallback,
        Action<int> onActivityImported,
        CancellationToken ct,
        int maxConcurrency = 4)
    {
        var apiIdsQuery = _context.Activities.Where(a => a.ApiId != null);
        if (!string.IsNullOrEmpty(userId)) apiIdsQuery = apiIdsQuery.Where(a => a.UserId == userId);
        var existingApiIds = await apiIdsQuery.Select(a => a.ApiId!.Value).ToHashSetAsync(ct);

        int preSkipped = 0;
        // seen carries the DB-existing ids too, so a duplicate ExternalId across pages (Strava
        // can shift items while paging) is pre-skipped once instead of persisted twice.
        var seen = new HashSet<long>(existingApiIds);
        var toFetch = new List<ExternalActivitySummary>();
        foreach (var a in activities)
        {
            if (!seen.Add(a.ExternalId))
            {
                preSkipped++;
                continue;
            }
            toFetch.Add(a);
        }

        int imported = 0, failed = 0;
        int totalCompleted = preSkipped;
        StravaRateLimitException? rateLimitEx = null;

        using var rateLimitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Fetcher threads share imported/failed/totalCompleted with the single-threaded persister,
        // so every mutation goes through Interlocked. Counts a fetch-stage failure + advances progress.
        void ReportFetchFailure(string name)
        {
            Interlocked.Increment(ref failed);
            var completed = Interlocked.Increment(ref totalCompleted);
            progressCallback(completed, name, Volatile.Read(ref imported), preSkipped, Volatile.Read(ref failed));
        }

        await ParallelImportPipeline.RunAsync<ExternalActivitySummary, PreparedImport>(
            toFetch,
            prepareAsync: async (externalActivity, t) =>
            {
                if (externalActivity.RawData is not StravaActivity sa)
                {
                    // Unreachable today (worker list is built by MapToExternalSummary) but
                    // count it as failed rather than silently dropping — keeps Current == Total.
                    ReportFetchFailure(externalActivity.Name ?? $"Activity {externalActivity.ExternalId}");
                    return null;
                }

                try
                {
                    var streamsTask = _apiClient.GetActivityStreamsAsync(sa.Id);
                    var detailTask  = _apiClient.GetActivityDetailAsync(sa.Id);
                    await Task.WhenAll(streamsTask, detailTask);

                    var streams = await streamsTask;
                    if (streams == null)
                    {
                        _logger.LogWarning("Stream fetch failed for {Id}; not importing", sa.Id);
                        ReportFetchFailure(externalActivity.Name ?? $"Activity {sa.Id}");
                        return null;
                    }

                    var summary   = MapToExternalSummary(sa, await detailTask);
                    var gpsPoints = ConvertStreamsToGpsPoints(streams, sa.StartDate);

                    return new PreparedImport(summary, gpsPoints);
                }
                catch (StravaRateLimitException ex)
                {
                    rateLimitEx ??= ex;
                    rateLimitCts.Cancel();
                    return null;
                }
                catch (OperationCanceledException) when (rateLimitEx != null)
                {
                    // Another task already triggered the rate-limit cancel; this task was cancelled as a result.
                    return null;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One malformed activity (bad stream token, mapping throw) must not fault
                    // Parallel.ForEachAsync and kill the whole run — count it and continue.
                    _logger.LogError(ex, "Error preparing Strava activity {Id}", sa.Id);
                    ReportFetchFailure(externalActivity.Name ?? $"Activity {sa.Id}");
                    return null;
                }
            },
            persistAsync: async p =>
            {
                var completed = Interlocked.Increment(ref totalCompleted);
                try
                {
                    var result = await _importService.ImportFromSummaryAsync(p.Summary, p.GpsPoints, userId);
                    if (result != null)
                    {
                        Interlocked.Increment(ref imported);
                        // Single persister task ⇒ serial; the worker collects these for the post-batch match.
                        onActivityImported(result.TrackId.Value);
                    }
                    else Interlocked.Increment(ref failed);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error persisting activity {Id}", p.Summary.ExternalId);
                    Interlocked.Increment(ref failed);
                }
                // IE-07 (adopted from the TrackFile twin via the shared pipeline): now inside the
                // persister's protected region, so a throwing callback is counted failed and the
                // loop continues instead of wedging the bounded channel.
                progressCallback(completed, p.Summary.Name ?? $"Activity {p.Summary.ExternalId}",
                    Volatile.Read(ref imported), preSkipped, Volatile.Read(ref failed));
            },
            onPersistError: (p, ex) =>
            {
                _logger.LogError(ex, "Persist stage failed for activity {Id}", p.Summary.ExternalId);
                Interlocked.Increment(ref failed);
            },
            ct,
            maxConcurrency,
            fetchCancellation: rateLimitCts.Token,
            isExpectedFetchCancellation: () => rateLimitEx != null);

        if (rateLimitEx != null)
            throw rateLimitEx;

        return (imported, preSkipped, failed);
    }

    private sealed record PreparedImport(ExternalActivitySummary Summary, List<ExternalGpsPoint> GpsPoints);

    // ── Mapping helpers ──────────────────────────────────────────────────────

    public static ExternalActivitySummary MapToExternalSummary(
        StravaActivity sa,
        StravaDetailedActivity? detail = null)
    {
        double? startLat = sa.StartLatLng?.Count > 0 ? sa.StartLatLng[0] : null;
        double? startLng = sa.StartLatLng?.Count > 1 ? sa.StartLatLng[1] : null;
        double? endLat   = sa.EndLatLng?.Count > 0   ? sa.EndLatLng[0]   : null;
        double? endLng   = sa.EndLatLng?.Count > 1   ? sa.EndLatLng[1]   : null;

        // SV-07: detail (from a separate API call) wins; sa itself carries the same fields
        // when it's already a StravaDetailedActivity (e.g. re-mapped from cache).
        var det = detail ?? sa as StravaDetailedActivity;

        return new ExternalActivitySummary
        {
            ExternalId       = sa.Id,
            ProviderId       = "strava",
            Source           = "strava",
            Name             = sa.Name,
            ActivityType     = sa.Type ?? sa.SportType,
            StartDateLocal   = sa.StartDateLocal != default ? sa.StartDateLocal : null,
            StartDateUtc     = sa.StartDate      != default ? sa.StartDate      : null,
            TimezoneOffset   = ParseTimezoneOffset(sa.Timezone),
            StartLat         = startLat,
            StartLng         = startLng,
            EndLat           = endLat,
            EndLng           = endLng,
            Distance         = sa.Distance,
            TotalTime        = sa.ElapsedTime,
            MovingTime       = sa.MovingTime,
            ElapsedTime      = sa.ElapsedTime,
            ElevationGain    = sa.TotalElevationGain,
            AverageSpeed     = sa.AverageSpeed,
            MaxSpeed         = sa.MaxSpeed,
            AverageHeartRate = sa.AverageHeartrate,
            MaxHeartRate     = sa.MaxHeartrate,
            AverageCadence   = sa.AverageCadence,
            Calories         = det?.Calories,
            Description      = det?.Description,
            AveragePower     = det?.AverageWatts,
            MaxPower         = det?.MaxWatts,
            RawData          = sa
        };
    }

    /// <summary>
    /// Maps Strava stream dictionaries to provider-agnostic <see cref="ExternalGpsPoint"/> list.
    /// </summary>
    public static List<ExternalGpsPoint> ConvertStreamsToGpsPoints(
        Dictionary<string, StravaStream> streams,
        DateTime activityStartUtc)
    {
        if (!streams.TryGetValue("latlng", out var latlngStream) || latlngStream.Data == null)
            return [];

        var latlngData = latlngStream.Data;
        var altData    = streams.TryGetValue("altitude",        out var s) ? s.Data : null;
        var timeData   = streams.TryGetValue("time",            out s)     ? s.Data : null;
        var distData   = streams.TryGetValue("distance",        out s)     ? s.Data : null;
        var velData    = streams.TryGetValue("velocity_smooth", out s)     ? s.Data : null;
        var hrData     = streams.TryGetValue("heartrate",       out s)     ? s.Data : null;
        var cadData    = streams.TryGetValue("cadence",         out s)     ? s.Data : null;
        var wattData   = streams.TryGetValue("watts",           out s)     ? s.Data : null;
        var gradeData  = streams.TryGetValue("grade_smooth",    out s)     ? s.Data : null;

        var result = new List<ExternalGpsPoint>(latlngData.Count);

        for (int i = 0; i < latlngData.Count; i++)
        {
            var latlng = latlngData[i] as JsonElement?;
            if (latlng == null || latlng.Value.ValueKind != JsonValueKind.Array)
                continue;

            var coordEnum = latlng.Value.EnumerateArray();
            if (!coordEnum.MoveNext()) continue;
            var lat = coordEnum.Current.GetDouble();
            if (!coordEnum.MoveNext()) continue;
            var lng = coordEnum.Current.GetDouble();

            var timeOffsetSec = GetDouble(timeData, i);
            DateTime? tsUtc   = timeOffsetSec.HasValue ? activityStartUtc.AddSeconds(timeOffsetSec.Value) : null;

            result.Add(new ExternalGpsPoint
            {
                Lat            = lat,
                Lng            = lng,
                Altitude       = GetDouble(altData,   i),
                Distance       = GetDouble(distData,  i),
                Speed          = GetDouble(velData,   i),
                HeartRate      = GetDouble(hrData,    i),
                Cadence        = GetDouble(cadData,   i),
                Power          = GetDouble(wattData,  i),
                GradeSmooth    = GetDouble(gradeData, i),
                TimestampUtc   = tsUtc,
            });
        }

        return result;
    }

    /// <summary>
    /// Parses a Strava timezone string like "(GMT+02:00) Europe/Amsterdam" into a
    /// signed float offset in hours (e.g. 2.0, -5.5). Returns null on failure.
    /// </summary>
    private static float? ParseTimezoneOffset(string? timezone)
    {
        if (string.IsNullOrEmpty(timezone))
            return null;

        var start = timezone.IndexOf('(', StringComparison.Ordinal);
        var end   = timezone.IndexOf(')', StringComparison.Ordinal);
        if (start < 0 || end < 0 || end <= start + 4)
            return null;

        var offsetPart = timezone.Substring(start + 4, end - start - 4); // skip "(GMT"
        if (offsetPart.Length < 6)
            return null;

        var sign  = offsetPart[0] == '-' ? -1 : 1;
        var parts = offsetPart.TrimStart('+', '-').Split(':');
        if (parts.Length != 2
            || !int.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out var hours)
            || !int.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var minutes))
            return null;

        return sign * (hours + minutes / 60f);
    }

    private static double? GetDouble(IList<object>? data, int i)
    {
        if (data == null || i >= data.Count)
            return null;
        // Guard ValueKind like the lat/lng path: an inline JSON null (or any non-number) in a present scalar
        // stream is a JsonElement of a non-Number kind, and .GetDouble() would THROW → the catch upstream
        // drops the whole activity. Treat it as a missing sample instead.
        return data[i] is JsonElement { ValueKind: JsonValueKind.Number } el ? el.GetDouble() : null;
    }
}
