using RouteBaseWeb.Services.Interfaces;
using GeoTimeZone;

namespace RouteBaseWeb.Services;

/// <summary>
/// Variant A (GI-01): coordinate-based timezone resolver backed by the offline GeoTimeZone
/// IANA lookup (MIT, tz shape data bundled in the package — no network call). Correct even
/// when the athlete travels, because the offset comes from the activity's own start point.
/// Requires ICU / tz database on the host (present on standard Linux/.NET images).
/// </summary>
public class GeoTimeZoneActivityResolver : IActivityTimezoneResolver
{
    private readonly ILogger<GeoTimeZoneActivityResolver> _logger;

    public GeoTimeZoneActivityResolver(ILogger<GeoTimeZoneActivityResolver> logger)
    {
        _logger = logger;
    }

    public Task<TimeSpan?> ResolveOffsetAsync(double? lat, double? lng, DateTime startUtc, string userId)
    {
        // GPX always supplies coords (parser requires points); guard anyway for other callers.
        if (!lat.HasValue || !lng.HasValue)
            return Task.FromResult<TimeSpan?>(null);

        try
        {
            // NOTE: TimeZoneResult.Result is a STRING PROPERTY (the IANA id), not Task.Result —
            // GeoTimeZone is fully synchronous. Do NOT rewrite to await; the async-rule grep skips it.
            var tzId = TimeZoneLookup.GetTimeZone(lat.Value, lng.Value).Result;
            if (string.IsNullOrEmpty(tzId))
                return Task.FromResult<TimeSpan?>(null);

            // Interpret startUtc as a UTC instant so the offset is DST-correct for that moment.
            var tz = TimeZoneInfo.FindSystemTimeZoneById(tzId);
            var offset = tz.GetUtcOffset(DateTime.SpecifyKind(startUtc, DateTimeKind.Utc));
            return Task.FromResult<TimeSpan?>(offset);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Timezone lookup failed for ({Lat}, {Lng}); keeping UTC", lat, lng);
            return Task.FromResult<TimeSpan?>(null);
        }
    }
}
