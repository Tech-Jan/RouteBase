namespace RouteBaseWeb.Services.Interfaces;

/// <summary>
/// Resolves the local-time offset for an activity from its start coordinates (GI-01).
/// GPX files carry no timezone, so the persist path uses this to stamp
/// <c>start_datetime_loc</c> / <c>datetime_timezone</c> instead of leaving UTC-as-local.
/// </summary>
public interface IActivityTimezoneResolver
{
    /// <summary>
    /// Offset to add to <paramref name="startUtc"/> to get local wall time;
    /// null = unknown (caller keeps UTC).
    /// </summary>
    Task<TimeSpan?> ResolveOffsetAsync(double? lat, double? lng, DateTime startUtc, string userId);
}
