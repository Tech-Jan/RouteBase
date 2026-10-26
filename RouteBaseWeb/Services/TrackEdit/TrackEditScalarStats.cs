using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Services.Sql;
using RouteBaseWeb.Services.Tracks;
using Microsoft.EntityFrameworkCore;

namespace RouteBaseWeb.Services.TrackEdit;

/// <summary>
/// Synchronous Activity scalar recompute for a replaced/restored track (CommitPath §14.4): motion + elevation
/// (<see cref="ActivityStatsSql.UpdateAllStatsSql"/>), sensor aggregates
/// (<see cref="ActivityEditSql.UpdateSensorStatsSql"/>), start/end/timezone
/// (<see cref="ActivityRecordingSql.UpdateStartLocationSql"/>) and an exact bbox overwrite
/// (<see cref="ActivityEditSql.RewriteActivityBboxSql"/>). Runs on the caller's context so every UPDATE
/// enlists in the caller's phase-1 transaction — the blob, stats and bbox commit atomically. Reused by save
/// (in-memory replay slot) and revert (decoded snapshot).
/// </summary>
public static class TrackEditScalarStats
{
    /// <summary>Recomputes + writes the Activity scalars; returns the computed stats so the caller can stamp
    /// the same numbers onto the version row it is appending (one computation, two writers).</summary>
    public static async Task<GpsNormalizationStats> WriteAsync(
        RouteBaseDbContext context, IGpsNormalizationService normalization, IActivityTimezoneResolver resolver,
        TrackId trackId, DecodedTrack track, string userId, CancellationToken ct)
    {
        var stats = normalization.ComputeStats(track.ToGpsDataList(trackId));

        // CS8604: EF Core maps null params to SQL NULL (params object[] isn't annotated object?[]).
#pragma warning disable CS8604
        await context.Database.ExecuteSqlRawAsync(ActivityStatsSql.UpdateAllStatsSql,
            stats.TotalDistanceMeters, stats.TotalAscentMeters, stats.TotalDescentMeters,
            stats.TimeTotalSeconds, stats.TimeMovingSeconds, stats.SpeedAvgMs, stats.SpeedMaxMs, trackId.Value);

        var sensor = TrackEditConverters.ComputeSensorStats(track);
        await context.Database.ExecuteSqlRawAsync(ActivityEditSql.UpdateSensorStatsSql,
            sensor.HrAvg, sensor.HrMax, sensor.CadAvg, sensor.CadMax, sensor.PowAvg, sensor.PowMax, trackId.Value);

        float? currentTz = await context.Activities.AsNoTracking()
            .Where(a => a.TrackId == trackId).Select(a => a.DateTimeTimezone).FirstOrDefaultAsync(ct);
        var offset = await ResolveOffsetAsync(resolver, stats, userId, currentTz);
        DateTime? startLocal = stats.StartUtc.HasValue ? stats.StartUtc.Value + offset : null;
        await context.Database.ExecuteSqlRawAsync(ActivityRecordingSql.UpdateStartLocationSql,
            stats.StartUtc, startLocal, (float)offset.TotalHours,
            stats.StartLat, stats.StartLng, stats.EndLat, stats.EndLng, trackId.Value);
#pragma warning restore CS8604

        await WriteBboxAsync(context, trackId, track, ct);
        return stats;
    }

    /// <summary>Resolver offset (DST-correct) → the activity's current tz → 0. Shared with the created-summary
    /// builder so an edit's tz stays consistent across replaced + created rows.</summary>
    public static async Task<TimeSpan> ResolveOffsetAsync(
        IActivityTimezoneResolver resolver, GpsNormalizationStats stats, string userId, float? currentTzHours)
    {
        var resolved = stats.StartUtc.HasValue
            ? await resolver.ResolveOffsetAsync(stats.StartLat, stats.StartLng, stats.StartUtc.Value, userId)
            : null;
        return resolved ?? (currentTzHours.HasValue ? TimeSpan.FromHours(currentTzHours.Value) : TimeSpan.Zero);
    }

    private static async Task WriteBboxAsync(
        RouteBaseDbContext context, TrackId trackId, DecodedTrack track, CancellationToken ct)
    {
        double minLat = double.MaxValue, maxLat = double.MinValue, minLng = double.MaxValue, maxLng = double.MinValue;
        bool any = false;
        for (int i = 0; i < track.Count; i++)
        {
            double lat = track.Lat[i], lng = track.Lng[i];
            if (double.IsNaN(lat) || double.IsNaN(lng)) continue;
            any = true;
            if (lat < minLat) minLat = lat;
            if (lat > maxLat) maxLat = lat;
            if (lng < minLng) minLng = lng;
            if (lng > maxLng) maxLng = lng;
        }

        if (any)
            await context.Database.ExecuteSqlRawAsync(
                ActivityEditSql.RewriteActivityBboxSql, trackId.Value, minLat, maxLat, minLng, maxLng);
        else
            await context.Database.ExecuteSqlRawAsync( // defensive: MinSlotPoints geometry keeps coords present
                Sql.ChildTableRegistry.ActivityBboxByTrack.DeleteByKeySql, trackId.Value);
    }
}
