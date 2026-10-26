using RouteBaseWeb.Models;
using Microsoft.EntityFrameworkCore;
using RouteBaseWeb.Models.Sync;
using RouteBaseWeb.Services.Lifecycle;

namespace RouteBaseWeb.Services;

/// <summary>
/// Provider-agnostic activity import service.
/// Accepts <see cref="ExternalActivitySummary"/> + <see cref="ExternalGpsPoint"/> list
/// and persists them to the database. StravaService (and future GarminService) delegate
/// all DB writes here so the pipeline stays identical regardless of source.
/// </summary>
public class ActivityImportService : Interfaces.IActivityImportService
{
    private readonly RouteBaseDbContext _context;
    private readonly Tracks.TrackContentStore _content;
    private readonly ILogger<ActivityImportService> _logger;

    public ActivityImportService(
        RouteBaseDbContext context, Tracks.TrackContentStore content,
        ILogger<ActivityImportService> logger)
    {
        _context = context;
        _content = content;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ExistingActivity?> FindExistingAsync(long externalId, string source, string userId)
    {
        var row = await _context.Activities
            .Where(a => a.ApiId == externalId && a.Source == source && a.UserId == userId)
            .Select(a => new { a.TrackId, a.TrackName, a.Lifecycle, a.LifecycleReason })
            .FirstOrDefaultAsync();
        return row is null ? null : new ExistingActivity(row.TrackId.Value, row.TrackName, row.Lifecycle, row.LifecycleReason);
    }

    /// <summary>
    /// Inserts the activity + GPS points in a single transaction.
    /// Computes total_descent from the altitude stream.
    /// Returns the new <see cref="Activity"/> (with TrackId populated) or null on failure.
    /// </summary>
    public async Task<Activity?> ImportFromSummaryAsync(
        ExternalActivitySummary summary,
        List<ExternalGpsPoint> gpsPoints,
        string userId)
    {
        using var transaction = await _context.Database.BeginTransactionAsync();

        try
        {
            // EF Core maps C# null to SQL NULL. Do NOT use DBNull.Value — the SQLite
            // provider has no store type mapping for System.DBNull and will throw.
            // CS8604: params object[] is not annotated as object?[], but EF Core explicitly
            // handles null elements as SQL NULL. Suppressed at the call site rather than
            // scattered across every nullable argument.
#pragma warning disable CS8604
            await _context.Database.ExecuteSqlRawAsync(Sql.ActivityImportSql.InsertActivitySql,
                summary.ExternalId,
                summary.Name,
                summary.ActivityType,
                summary.StartDateLocal,
                summary.StartDateUtc,
                summary.TimezoneOffset,
                summary.StartLat,
                summary.StartLng,
                summary.EndLat,
                summary.EndLng,
                summary.Distance,
                summary.TotalTime ?? summary.ElapsedTime, // GI-02: {11} time_total prefers device timer time
                summary.MovingTime,
                summary.ElapsedTime,
                summary.ElevationGain,
                summary.AverageSpeed,
                summary.MaxSpeed,
                summary.AverageHeartRate,
                summary.MaxHeartRate,
                summary.AverageCadence,
                summary.MaxCadence,
                summary.AveragePower,
                summary.MaxPower,
                summary.Calories,
                summary.Description,
                summary.Source ?? summary.ProviderId,
                userId);
#pragma warning restore CS8604

            var trackIdResult = await _context.Database
                .SqlQueryRaw<int>(Sql.ActivityImportSql.SelectLastRowIdSql)
                .ToListAsync();
            var trackId = trackIdResult.FirstOrDefault();

            if (gpsPoints.Count > 0)
            {
                await WriteTrackBlobAsync(new TrackId(trackId), gpsPoints);

                var totalDescent = summary.TotalDescent ?? ComputeTotalDescent(gpsPoints);
                if (totalDescent > 0)
                {
                    await _context.Database.ExecuteSqlRawAsync(
                        Sql.ActivityImportSql.UpdateTotalDescentSql,
                        totalDescent, trackId);
                }

                await UpsertActivityBboxAsync(new TrackId(trackId), gpsPoints);
            }

            await transaction.CommitAsync();

            _logger.LogInformation(
                "Imported activity {ExternalId} (source: {Source}) → track_ID {TrackId}, {GpsCount} GPS points",
                summary.ExternalId, summary.Source ?? summary.ProviderId, trackId, gpsPoints.Count);

            return new Activity { TrackId = new TrackId(trackId), ApiId = summary.ExternalId };
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            _logger.LogError(ex, "Error importing activity {ExternalId}, transaction rolled back", summary.ExternalId);
            throw;
        }
    }

    /// <summary>
    /// Writes a track's points as its first version (seq 1, no parents — an import is where lineage begins).
    /// Sole import-path track-write funnel (GPX + Strava); finalize writes its own v1 and live-recording
    /// appends go to staging. Runs on the ambient context so the content write stays inside the caller's
    /// import transaction. No cache invalidation: content is immutable and keyed by hash, so nothing can be stale.
    /// Invariant: any path that writes a track blob must ALSO call <see cref="UpsertActivityBboxAsync(int)"/> —
    /// the candidate-segment/candidate-track match prefilter reads Activity_Bbox; a missing row silently
    /// degrades matching to a full decode-based bbox scan.
    /// </summary>
    public async Task WriteTrackBlobAsync(TrackId trackId, List<ExternalGpsPoint> points)
    {
        var track = Tracks.DecodedTrackExtensions.FromExternalPoints(points);
        var encoded = Tracks.TrackBlobCodec.Encode(track);
        await _content.AppendVersionAsync(trackId, encoded, track.Count);
    }

    /// <summary>
    /// Computes total descent in metres by summing all negative altitude deltas.
    /// </summary>
    public double ComputeTotalDescent(List<ExternalGpsPoint> points)
    {
        double totalDescent = 0.0;
        double? prevAlt = null;

        foreach (var pt in points)
        {
            if (pt.Altitude.HasValue)
            {
                if (prevAlt.HasValue && pt.Altitude.Value < prevAlt.Value)
                    totalDescent += prevAlt.Value - pt.Altitude.Value;
                prevAlt = pt.Altitude.Value;
            }
        }

        return totalDescent;
    }

    /// <summary>
    /// Expands (or inserts) the <c>Activity_Bbox</c> row from a point batch — computed in C#, skipping
    /// null coords; empty batch is a no-op. Callers: import (full list = exact bbox), recording append
    /// (batch = incremental expand). Expand-only is sound (see <see cref="Sql.ActivityImportSql.UpsertActivityBboxSql"/>).
    /// </summary>
    public async Task UpsertActivityBboxAsync(TrackId trackId, IReadOnlyList<ExternalGpsPoint> points)
    {
        double minLat = double.MaxValue, maxLat = double.MinValue;
        double minLng = double.MaxValue, maxLng = double.MinValue;
        bool any = false;
        foreach (var p in points)
        {
            if (double.IsNaN(p.Lat) || double.IsNaN(p.Lng)) continue;
            minLat = Math.Min(minLat, p.Lat); maxLat = Math.Max(maxLat, p.Lat);
            minLng = Math.Min(minLng, p.Lng); maxLng = Math.Max(maxLng, p.Lng);
            any = true;
        }
        if (!any) return;
        await UpsertBboxValuesAsync(trackId, minLat, maxLat, minLng, maxLng);
    }

    /// <summary>
    /// Expands (or inserts) the <c>Activity_Bbox</c> row from the track's current content — the
    /// belt-and-suspenders path for callers that hold no point list (post-import guard, finalize).
    /// No-op when the track has no content.
    /// </summary>
    public async Task UpsertActivityBboxAsync(TrackId trackId)
    {
        var blob = await _content.GetHeadDataAsync(trackId);
        if (blob == null) return;

        var track = Tracks.TrackBlobCodec.Decode(blob);
        double minLat = double.MaxValue, maxLat = double.MinValue;
        double minLng = double.MaxValue, maxLng = double.MinValue;
        bool any = false;
        for (int i = 0; i < track.Count; i++)
        {
            double la = track.Lat[i], ln = track.Lng[i];
            if (double.IsNaN(la) || double.IsNaN(ln)) continue;
            minLat = Math.Min(minLat, la); maxLat = Math.Max(maxLat, la);
            minLng = Math.Min(minLng, ln); maxLng = Math.Max(maxLng, ln);
            any = true;
        }
        if (!any) return;
        // Rewrite, not expand-only: this overload runs when content IS the truth (finalize after
        // outlier rejection, post-import guard) — an expand-only merge kept a live-append GPS
        // teleport's inflated box forever after finalize dropped the point from the content.
        await _context.Database.ExecuteSqlRawAsync(
            Sql.ActivityEditSql.RewriteActivityBboxSql, trackId.Value, minLat, maxLat, minLng, maxLng);
    }

    private Task UpsertBboxValuesAsync(TrackId trackId, double minLat, double maxLat, double minLng, double maxLng) =>
        _context.Database.ExecuteSqlRawAsync(
            Sql.ActivityImportSql.UpsertActivityBboxSql, trackId.Value, minLat, maxLat, minLng, maxLng);
}
