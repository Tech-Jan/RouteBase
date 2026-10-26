using System.Net.Http.Headers;
using System.Text.Json;
using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Models.Strava;

namespace RouteBaseWeb.Services;

public class StravaApiClient : IStravaApiClient
{
    private readonly IStravaAuthService _auth;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<StravaApiClient> _logger;
    private readonly TimeProvider _timeProvider;

    private const string StravaApiBaseUrl = "https://www.strava.com/api/v3";
    private const int DefaultMaxRetries = 3;

    public StravaApiClient(
        IStravaAuthService auth,
        IHttpClientFactory httpClientFactory,
        ILogger<StravaApiClient> logger,
        TimeProvider timeProvider)
    {
        _auth = auth;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    public async Task<List<StravaActivity>> GetActivitiesAsync(DateTime? after = null, int page = 1, int perPage = 30)
    {
        // SV-04: a page fetch must never return [] on failure — GetAllActivitiesAsync reads []
        // as "last page" and the worker would stamp a truncated import Complete. Throw instead
        // so the failure surfaces (worker routes it to Fail via the outer catch / rate-limit catch).
        var accessToken = await _auth.GetValidAccessTokenAsync();
        if (string.IsNullOrEmpty(accessToken))
            throw new InvalidOperationException("Not authenticated with Strava.");

        var client = _httpClientFactory.CreateClient();
        var url = $"{StravaApiBaseUrl}/athlete/activities?page={page}&per_page={perPage}";
        if (after.HasValue)
        {
            var afterTimestamp = ((DateTimeOffset)after.Value).ToUnixTimeSeconds();
            url += $"&after={afterTimestamp}";
        }

        // 429 throws StravaRateLimitException inside SendWithRetryAsync (before this point).
        using var response = await SendWithRetryAsync(client, () => AuthorizedGet(url, accessToken));

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Strava activities fetch failed: {response.StatusCode}");

        using var stream = await response.Content.ReadAsStreamAsync();
        return await JsonSerializer.DeserializeAsync<List<StravaActivity>>(stream) ?? [];
    }

    /// <summary>
    /// Pages through all activities from Strava using perPage=200 (Strava's maximum).
    /// Throws <see cref="StravaRateLimitException"/> on 429.
    /// </summary>
    public async Task<List<StravaActivity>> GetAllActivitiesAsync(DateTime? after = null)
    {
        const int perPage = 200;
        var all = new List<StravaActivity>();
        int page = 1;

        while (true)
        {
            var batch = await GetActivitiesAsync(after, page, perPage);
            if (batch.Count == 0)
                break;

            all.AddRange(batch);

            if (batch.Count < perPage)
                break;

            page++;
        }

        return all;
    }

    public async Task<Dictionary<string, StravaStream>?> GetActivityStreamsAsync(long activityId)
    {
        var accessToken = await _auth.GetValidAccessTokenAsync();
        if (string.IsNullOrEmpty(accessToken))
        {
            _logger.LogWarning("No valid access token available");
            return null;
        }

        try
        {
            var client = _httpClientFactory.CreateClient();
            var streamTypes = "latlng,altitude,time,distance,velocity_smooth,heartrate,cadence,watts,grade_smooth";
            var url = $"{StravaApiBaseUrl}/activities/{activityId}/streams?keys={streamTypes}&key_by_type=true";

            using var response = await SendWithRetryAsync(client, () => AuthorizedGet(url, accessToken));

            // 404 = activity has no streams (manual/trainer entries); import it with 0 GPS points.
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return [];

            if (!response.IsSuccessStatusCode)
            {
                // null (not []) so the orchestrator can tell "fetch failed" from "no GPS".
                _logger.LogError("Failed to fetch activity streams: {StatusCode}", response.StatusCode);
                return null;
            }

            using var stream = await response.Content.ReadAsStreamAsync();
            return await JsonSerializer.DeserializeAsync<Dictionary<string, StravaStream>>(stream) ?? [];
        }
        catch (StravaRateLimitException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching activity streams from Strava");
            return null;
        }
    }

    public async Task<StravaDetailedActivity?> GetActivityDetailAsync(long activityId)
    {
        var accessToken = await _auth.GetValidAccessTokenAsync();
        if (string.IsNullOrEmpty(accessToken))
            return null;

        try
        {
            var client = _httpClientFactory.CreateClient();
            var url = $"{StravaApiBaseUrl}/activities/{activityId}";

            using var response = await SendWithRetryAsync(client, () => AuthorizedGet(url, accessToken));
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Detail fetch for activity {Id} returned {Status}", activityId, response.StatusCode);
                return null;
            }

            using var stream = await response.Content.ReadAsStreamAsync();
            return await JsonSerializer.DeserializeAsync<StravaDetailedActivity>(stream);
        }
        catch (StravaRateLimitException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not fetch detail for activity {Id}", activityId);
            return null;
        }
    }

    // Creates an authorized GET request. A new instance must be created per attempt
    // because HttpRequestMessage cannot be reused after SendAsync.
    private static HttpRequestMessage AuthorizedGet(string url, string accessToken) =>
        new(HttpMethod.Get, url)
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", accessToken) }
        };

    /// <summary>
    /// Sends a request with exponential-backoff retry for transient server errors (5xx).
    /// Throws <see cref="StravaRateLimitException"/> immediately on the first 429 — Strava's
    /// rate-limit window is ~15 min, so retrying inside it would be pointless.
    /// </summary>
    private async Task<HttpResponseMessage> SendWithRetryAsync(
        HttpClient client,
        Func<HttpRequestMessage> requestFactory,
        int maxRetries = DefaultMaxRetries)
    {
        for (int attempt = 0; ; attempt++)
        {
            var response = await client.SendAsync(requestFactory());

            if (response.IsSuccessStatusCode)
                return response;

            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                response.Dispose(); // every other branch disposes/returns; don't leak the handle on 429
                throw new StravaRateLimitException();
            }

            var isTransient = (int)response.StatusCode >= 500;

            if (attempt >= maxRetries || !isTransient)
                return response;

            var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt)); // 1 s, 2 s, 4 s
            _logger.LogWarning(
                "Strava API returned {Status} on attempt {Attempt}/{Max}. Retrying in {Delay}s.",
                response.StatusCode, attempt + 1, maxRetries, delay.TotalSeconds);

            response.Dispose();
            await Task.Delay(delay, _timeProvider);
        }
    }
}
