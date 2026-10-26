using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Strava;

namespace RouteBaseWeb.Services.Interfaces;

public interface IStravaApiClient
{
    /// <summary>Fetches one page of activities from Strava.</summary>
    Task<List<StravaActivity>> GetActivitiesAsync(DateTime? after = null, int page = 1, int perPage = 30);

    /// <summary>Pages through all activities from Strava until the last page is reached.</summary>
    Task<List<StravaActivity>> GetAllActivitiesAsync(DateTime? after = null);

    /// <summary>
    /// Fetches GPS and sensor streams for an activity. Returns <c>null</c> when the fetch
    /// itself failed (no token, non-404 error status, exception) — the caller must NOT import,
    /// or the activity would be persisted permanently GPS-less. Returns an empty dictionary on
    /// 404 (Strava's answer for activities with no streams: manual/trainer entries import with 0 points).
    /// </summary>
    Task<Dictionary<string, StravaStream>?> GetActivityStreamsAsync(long activityId);

    /// <summary>Fetches the detailed activity endpoint (calories, description, power).</summary>
    Task<StravaDetailedActivity?> GetActivityDetailAsync(long activityId);
}
