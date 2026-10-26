using System.Net;
using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Strava;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace RouteBaseWeb.Tests.Services;

/// <summary>
/// Characterization tests for <see cref="StravaApiClient"/>'s HTTP retry / 429 contract.
/// Pin current behavior before the SV-* stability edits: a 429 aborts immediately via
/// <see cref="StravaRateLimitException"/>; transient 5xx are retried until success.
/// </summary>
public class StravaApiClientTests
{
    private static StravaApiClient CreateClient(FakeHttpMessageHandler handler, TimeProvider? time = null) =>
        new(new FakeAuth(), new FakeHttpClientFactory(handler), NullLogger<StravaApiClient>.Instance,
            time ?? new RecordingTimeProvider());

    [Fact]
    public async Task GetActivitiesAsync_RateLimited_ThrowsImmediately()
    {
        var handler = new FakeHttpMessageHandler(
            (HttpStatusCode.TooManyRequests, "{}"));

        var client = CreateClient(handler);

        await Assert.ThrowsAsync<StravaRateLimitException>(() => client.GetActivitiesAsync());
        Assert.Equal(1, handler.CallCount); // no retry on 429
    }

    [Fact]
    public async Task GetActivitiesAsync_TransientErrorsThenSuccess_RetriesAndReturns()
    {
        // Two transient 5xx (retried with 1s+2s backoff) then a 200 with one activity.
        var handler = new FakeHttpMessageHandler(
            (HttpStatusCode.InternalServerError, "{}"),
            (HttpStatusCode.BadGateway, "{}"),
            (HttpStatusCode.OK, "[{\"id\": 123, \"name\": \"Ride\"}]"));

        var time = new RecordingTimeProvider();
        var client = CreateClient(handler, time);

        var activities = await client.GetActivitiesAsync();

        Assert.Single(activities);
        Assert.Equal(123, activities[0].Id);
        Assert.Equal(3, handler.CallCount);
        // Backoff schedule pinned via the injected clock (was previously only enforced by wall-clock).
        Assert.Equal(new[] { 1.0, 2.0 }, time.Delays.Select(d => d.TotalSeconds));
    }

    [Fact]
    public async Task GetActivitiesAsync_ServerErrorAfterRetries_Throws()
    {
        // 4 = initial + 3 retries all 500 → SendWithRetryAsync returns the 500, caller throws.
        var handler = new FakeHttpMessageHandler(
            (HttpStatusCode.InternalServerError, "{}"),
            (HttpStatusCode.InternalServerError, "{}"),
            (HttpStatusCode.InternalServerError, "{}"),
            (HttpStatusCode.InternalServerError, "{}"));

        var client = CreateClient(handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetActivitiesAsync());
    }

    [Fact]
    public async Task GetAllActivitiesAsync_SecondPageFails_Throws()
    {
        // Page 1 = a full 200-item page (forces a page 2 fetch); page 2 = 500 on every retry.
        var page1 = "[" + string.Join(",", Enumerable.Range(1, 200).Select(i => $"{{\"id\":{i}}}")) + "]";
        var handler = new FakeHttpMessageHandler(
            (HttpStatusCode.OK, page1),
            (HttpStatusCode.InternalServerError, "{}"),
            (HttpStatusCode.InternalServerError, "{}"),
            (HttpStatusCode.InternalServerError, "{}"),
            (HttpStatusCode.InternalServerError, "{}"));

        var client = CreateClient(handler);

        // A mid-pagination failure must surface, never return the partial 200-item list.
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAllActivitiesAsync());
    }

    // ── Fakes ────────────────────────────────────────────────────────────────

    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body)> _responses;

        public FakeHttpMessageHandler(params (HttpStatusCode, string)[] responses)
            => _responses = new Queue<(HttpStatusCode, string)>(responses);

        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            var (status, body) = _responses.Count > 0
                ? _responses.Dequeue()
                : (HttpStatusCode.OK, "[]");
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body),
            });
        }
    }

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    /// <summary>Records requested backoff delays and completes them instantly, so the retry tests
    /// exercise the full backoff schedule without the ~17 s of real <c>Task.Delay</c> waits.
    /// <c>Task.Delay(delay, provider)</c> schedules via <see cref="CreateTimer"/> with dueTime = delay,
    /// so recording dueTime captures the requested schedule; the timer fires immediately.</summary>
    private sealed class RecordingTimeProvider : TimeProvider
    {
        public List<TimeSpan> Delays { get; } = new();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Delays.Add(dueTime);
            return new ImmediateTimer(callback, state);
        }

        private sealed class ImmediateTimer : ITimer
        {
            public ImmediateTimer(TimerCallback callback, object? state)
                // Defer to the thread pool so the delay completes after Task.Delay finishes wiring up.
                => ThreadPool.QueueUserWorkItem(_ => callback(state));

            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class FakeAuth : IStravaAuthService
    {
        public Task<string?> GetValidAccessTokenAsync() => Task.FromResult<string?>("test-token");
        public Task<StravaTokenResponse?> ExchangeCodeForTokenAsync(string code)
            => Task.FromResult<StravaTokenResponse?>(null);
        public string GetAuthorizationUrl(string redirectUri, string? clientId, string? state = null) => "";
        public Task<bool> IsAuthenticatedAsync() => Task.FromResult(true);
        public Task<StravaAthlete?> GetAthleteAsync() => Task.FromResult<StravaAthlete?>(null);
        public Task DisconnectAsync() => Task.CompletedTask;
        public Task<string?> GetSavedClientIdAsync() => Task.FromResult<string?>(null);
        public Task SaveCredentialsAsync(string clientId, string clientSecret) => Task.CompletedTask;
    }
}
