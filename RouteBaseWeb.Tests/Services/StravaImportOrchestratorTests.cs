using System.Text.Json;
using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Strava;
using RouteBaseWeb.Models.Sync;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using RouteBaseWeb.Services.Lifecycle;

namespace RouteBaseWeb.Tests.Services;

/// <summary>
/// Characterization tests for <see cref="StravaImportOrchestrator"/>'s pure static mappers
/// (<c>ConvertStreamsToGpsPoints</c>, <c>MapToExternalSummary</c>). Pin current behavior
/// before the SV-* stability edits touch the fetcher/orchestrator.
/// </summary>
public class StravaImportOrchestratorTests
{
    private static Dictionary<string, StravaStream> ParseStreams(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, StravaStream>>(json)!;

    [Fact]
    public void ConvertStreamsToGpsPoints_MapsAllStreamFields()
    {
        var start = new DateTime(2024, 6, 1, 10, 0, 0, DateTimeKind.Utc);
        var streams = ParseStreams("""
        {
          "latlng":          { "data": [[52.1, 4.2], [52.2, 4.3]] },
          "altitude":        { "data": [10.5, 11.5] },
          "time":            { "data": [0, 5] },
          "distance":        { "data": [0, 50] },
          "velocity_smooth": { "data": [2.5, 3.0] },
          "heartrate":       { "data": [120, 130] },
          "cadence":         { "data": [80, 82] },
          "watts":           { "data": [200, 210] },
          "grade_smooth":    { "data": [1.0, 2.0] }
        }
        """);

        var points = StravaImportOrchestrator.ConvertStreamsToGpsPoints(streams, start);

        Assert.Equal(2, points.Count);

        var p0 = points[0];
        Assert.Equal(52.1, p0.Lat);
        Assert.Equal(4.2, p0.Lng);
        Assert.Equal(10.5, p0.Altitude);
        Assert.Equal(0, p0.Distance);
        Assert.Equal(2.5, p0.Speed);
        Assert.Equal(120, p0.HeartRate);
        Assert.Equal(80, p0.Cadence);
        Assert.Equal(200, p0.Power);
        Assert.Equal(1.0, p0.GradeSmooth);
        Assert.Equal(start, p0.TimestampUtc);

        var p1 = points[1];
        Assert.Equal(52.2, p1.Lat);
        Assert.Equal(4.3, p1.Lng);
        Assert.Equal(start.AddSeconds(5), p1.TimestampUtc);
    }

    [Fact]
    public void ConvertStreamsToGpsPoints_MissingLatLng_ReturnsEmpty()
    {
        var streams = ParseStreams("""{ "altitude": { "data": [10.0, 11.0] } }""");

        var points = StravaImportOrchestrator.ConvertStreamsToGpsPoints(
            streams, new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.Empty(points);
    }

    [Theory]
    [InlineData("(GMT+02:00) Europe/Amsterdam", 2.0f)]
    [InlineData("(GMT-05:30) Asia/Kolkata", -5.5f)]
    public void MapToExternalSummary_ParsesTimezoneOffset(string timezone, float expected)
    {
        var sa = new StravaActivity { Id = 1, Timezone = timezone };

        var summary = StravaImportOrchestrator.MapToExternalSummary(sa);

        Assert.Equal(expected, summary.TimezoneOffset);
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData(null)]
    [InlineData("")]
    public void MapToExternalSummary_UnparseableTimezone_ReturnsNull(string? timezone)
    {
        var sa = new StravaActivity { Id = 1, Timezone = timezone };

        var summary = StravaImportOrchestrator.MapToExternalSummary(sa);

        Assert.Null(summary.TimezoneOffset);
    }
}

/// <summary>
/// Behavior tests for <see cref="StravaImportOrchestrator"/>'s import flow (SV-02 nullable
/// streams, SV-06 per-item catch + dedupe). Uses a real in-memory SQLite context (the
/// orchestrator queries <c>Activities</c> for the pre-skip set) with hand-rolled fakes.
/// </summary>
public class StravaImportOrchestratorImportTests : IAsyncLifetime
{
    private RouteBaseDbContext _context = null!;
    private FakeApiClient _api = null!;
    private FakeImportService _import = null!;
    private StravaImportOrchestrator _sut = null!;

    public async Task InitializeAsync()
    {
        var dbName = $"strava_orch_{Guid.NewGuid():N}";
        var options = new DbContextOptionsBuilder<RouteBaseDbContext>()
            .UseSqlite($"Data Source=file:{dbName}?mode=memory&cache=shared")
            .Options;
        _context = new RouteBaseDbContext(options);
        await _context.Database.EnsureCreatedAsync();

        _api    = new FakeApiClient();
        _import = new FakeImportService();
        _sut    = new StravaImportOrchestrator(
            _context, _api, _import, NullLogger<StravaImportOrchestrator>.Instance);
    }

    public async Task DisposeAsync()
    {
        await _context.Database.EnsureDeletedAsync();
        await _context.DisposeAsync();
    }

    private static Dictionary<string, StravaStream> LatLngStreams(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, StravaStream>>(json)!;

    private static ExternalActivitySummary Summary(long id) =>
        StravaImportOrchestrator.MapToExternalSummary(new StravaActivity { Id = id, Name = $"Act {id}" });

    // ── SV-02 serial path ─────────────────────────────────────────────────────

    [Fact]
    public async Task ImportActivityAsync_StreamsFetchFails_ReturnsNullWithoutPersisting()
    {
        _api.Streams[1] = null; // fetch failure

        var result = await _sut.ImportActivityAsync(new StravaActivity { Id = 1 });

        Assert.Null(result);
        Assert.Equal(0, _import.ImportCallCount);
    }

    [Fact]
    public async Task ImportActivityAsync_NoStreams404_ImportsWithZeroGpsPoints()
    {
        _api.Streams[1] = new Dictionary<string, StravaStream>(StringComparer.Ordinal); // 404 → empty dict

        var result = await _sut.ImportActivityAsync(new StravaActivity { Id = 1 });

        Assert.NotNull(result);
        Assert.Equal(1, _import.ImportCallCount);
        Assert.Empty(_import.LastPoints!);
    }

    // ── SV-02 / SV-06 parallel path ───────────────────────────────────────────

    [Fact]
    public async Task ImportActivitiesParallelAsync_FetchFailure_CountsFailedAndReportsProgress()
    {
        _api.Streams[1] = null;                                    // fetch fails
        _api.Streams[2] = new Dictionary<string, StravaStream>(StringComparer.Ordinal);  // no GPS, imports fine

        var progress = new List<int>();
        var (imported, skipped, failed) = await _sut.ImportActivitiesParallelAsync(
            [Summary(1), Summary(2)], "u",
            (current, _, _, _, _) => { lock (progress) progress.Add(current); },
            onActivityImported: _ => { },
            CancellationToken.None);

        Assert.Equal((1, 0, 1), (imported, skipped, failed));
        Assert.Equal(2, progress.Max()); // both items advanced Current
    }

    [Fact]
    public async Task ImportActivitiesParallelAsync_MappingThrows_CountsFailedAndContinues()
    {
        // Activity 1's latlng contains a string element → GetDouble() throws during mapping.
        _api.Streams[1] = LatLngStreams("""{ "latlng": { "data": [["x", 4.2]] } }""");
        _api.Streams[2] = new Dictionary<string, StravaStream>(StringComparer.Ordinal);

        var (imported, skipped, failed) = await _sut.ImportActivitiesParallelAsync(
            [Summary(1), Summary(2)], "u", (_, _, _, _, _) => { }, onActivityImported: _ => { }, CancellationToken.None);

        Assert.Equal((1, 0, 1), (imported, skipped, failed));
    }

    [Fact]
    public async Task ImportActivitiesParallelAsync_DuplicateExternalId_ImportsOnce()
    {
        _api.Streams[7] = new Dictionary<string, StravaStream>(StringComparer.Ordinal);

        var (imported, skipped, failed) = await _sut.ImportActivitiesParallelAsync(
            [Summary(7), Summary(7)], "u", (_, _, _, _, _) => { }, onActivityImported: _ => { }, CancellationToken.None);

        Assert.Equal((1, 1, 0), (imported, skipped, failed));
        Assert.Equal(1, _import.ImportCallCount);
    }

    // ── inval-T2: onActivityImported surfaces exactly the imported trackIds ──────
    // FakeImportService returns Activity{TrackId = ExternalId}, so the spy's ids equal the imported
    // ExternalIds. Batch = 1 fresh + 1 in-batch duplicate (pre-skipped) + 1 fetch-fail → spy = [fresh] only.

    [Fact]
    public async Task ImportActivitiesParallelAsync_FiresCallbackOnlyForImportedTracks()
    {
        _api.Streams[3] = new Dictionary<string, StravaStream>(StringComparer.Ordinal); // fresh → imports (TrackId 3)
        _api.Streams[5] = null;                                   // fetch fails → never imported

        var importedIds = new List<int>();
        var (imported, skipped, failed) = await _sut.ImportActivitiesParallelAsync(
            [Summary(3), Summary(3), Summary(5)], "u",
            (_, _, _, _, _) => { },
            onActivityImported: id => { lock (importedIds) importedIds.Add(id); },
            CancellationToken.None);

        Assert.Equal((1, 1, 1), (imported, skipped, failed)); // 1 imported, 1 dup-skipped, 1 failed
        Assert.Equal(new[] { 3 }, importedIds.ToArray());       // callback fired once, only for the fresh track
    }

    // ── CR5-03 pipeline pins: skeleton semantics shared with TrackFileImportOrchestrator ──

    [Fact]
    public async Task ImportActivitiesParallelAsync_ProgressCallbackThrows_CountsFailedAndCompletes()
    {
        // IE-07 twin (CR5-03): a throwing progress callback must be counted failed and the run
        // must complete — the shared-skeleton semantics the TrackFile copy already has. RED
        // against the pre-extraction Strava copy: progressCallback sat outside the persister's
        // try, so one throw faulted the persister, filled the bounded channel (capacity 8), and
        // wedged the fetcher's WriteAsync forever. >8 queued items make the wedge deterministic.
        var summaries = Enumerable.Range(1, 20).Select(i => Summary(i)).ToList();

        int callbackCalls = 0;
        var run = _sut.ImportActivitiesParallelAsync(
            summaries, "u",
            (_, _, _, _, _) =>
            {
                if (Interlocked.Increment(ref callbackCalls) == 1)
                    throw new InvalidOperationException("boom");
            },
            onActivityImported: _ => { },
            CancellationToken.None);

        var (imported, skipped, failed) = await run.WaitAsync(TimeSpan.FromSeconds(30)); // no wedge
        Assert.Equal(20, _import.ImportCallCount); // items after the throw still persisted
        Assert.Equal((20, 0), (imported, skipped));
        Assert.True(failed >= 1); // the throwing callback's item is counted failed
    }

    [Fact]
    public async Task ImportActivitiesParallelAsync_ProgressCurrentValues_CoverPreSkipToTotalExactlyOnce()
    {
        // Callback-sequence contract: totalCompleted starts at preSkipped, every fetched item
        // (imported, fetch-failed, or persist-failed) advances Current exactly once.
        _api.Streams[2] = null;                                    // fetch fails
        _api.Streams[3] = new Dictionary<string, StravaStream>(StringComparer.Ordinal);  // imports
        _api.Streams[4] = new Dictionary<string, StravaStream>(StringComparer.Ordinal);  // imports
        _api.Streams[1] = new Dictionary<string, StravaStream>(StringComparer.Ordinal);  // imports (dup pre-skipped once)

        var currents = new List<int>();
        var (imported, skipped, failed) = await _sut.ImportActivitiesParallelAsync(
            [Summary(1), Summary(1), Summary(2), Summary(3), Summary(4)], "u",
            (current, _, _, _, _) => { lock (currents) currents.Add(current); },
            onActivityImported: _ => { },
            CancellationToken.None);

        Assert.Equal((3, 1, 1), (imported, skipped, failed));
        Assert.Equal(new[] { 2, 3, 4, 5 }, currents.OrderBy(c => c).ToArray());
    }

    [Fact]
    public async Task ImportActivitiesParallelAsync_PreCanceledToken_ThrowsOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _sut.ImportActivitiesParallelAsync(
                [Summary(1)], "u", (_, _, _, _, _) => { }, onActivityImported: _ => { }, cts.Token)
                .WaitAsync(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public async Task ImportActivitiesParallelAsync_RateLimit_RethrownAfterPipelineDrains()
    {
        // Rate-limit contract: first StravaRateLimitException cancels remaining fetches, the
        // pipeline drains without wedging, and the exception is rethrown to the caller.
        for (long id = 1; id <= 10; id++) _api.RateLimitIds.Add(id);
        var summaries = Enumerable.Range(1, 10).Select(i => Summary(i)).ToList();

        await Assert.ThrowsAsync<StravaRateLimitException>(() =>
            _sut.ImportActivitiesParallelAsync(
                summaries, "u", (_, _, _, _, _) => { }, onActivityImported: _ => { }, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(30)));

        Assert.Equal(0, _import.ImportCallCount);
    }

    // ── Fakes ──────────────────────────────────────────────────────────────────

    private sealed class FakeApiClient : IStravaApiClient
    {
        public Dictionary<long, Dictionary<string, StravaStream>?> Streams { get; } = new();
        public HashSet<long> RateLimitIds { get; } = new();

        public Task<Dictionary<string, StravaStream>?> GetActivityStreamsAsync(long activityId)
        {
            if (RateLimitIds.Contains(activityId)) throw new StravaRateLimitException();
            return Task.FromResult(Streams.TryGetValue(activityId, out var s)
                ? s
                : new Dictionary<string, StravaStream>(StringComparer.Ordinal));
        }

        public Task<StravaDetailedActivity?> GetActivityDetailAsync(long activityId)
            => Task.FromResult<StravaDetailedActivity?>(null);
        public Task<List<StravaActivity>> GetActivitiesAsync(DateTime? after = null, int page = 1, int perPage = 30)
            => Task.FromResult(new List<StravaActivity>());
        public Task<List<StravaActivity>> GetAllActivitiesAsync(DateTime? after = null)
            => Task.FromResult(new List<StravaActivity>());
    }

    private sealed class FakeImportService : IActivityImportService
    {
        private int _importCallCount;
        public int ImportCallCount => _importCallCount;
        public List<ExternalGpsPoint>? LastPoints { get; private set; }

        public Task<Activity?> ImportFromSummaryAsync(
            ExternalActivitySummary summary, List<ExternalGpsPoint> gpsPoints, string userId)
        {
            Interlocked.Increment(ref _importCallCount);
            LastPoints = gpsPoints;
            return Task.FromResult<Activity?>(new Activity { TrackId = new TrackId((int)summary.ExternalId) });
        }

        public Task<ExistingActivity?> FindExistingAsync(long externalId, string source, string userId)
            => throw new NotSupportedException();
        public Task WriteTrackBlobAsync(TrackId trackId, List<ExternalGpsPoint> points)
            => throw new NotSupportedException();
        public double ComputeTotalDescent(List<ExternalGpsPoint> points)
            => throw new NotSupportedException();
        public Task UpsertActivityBboxAsync(TrackId trackId, IReadOnlyList<ExternalGpsPoint> points)
            => throw new NotSupportedException();
        public Task UpsertActivityBboxAsync(TrackId trackId)
            => throw new NotSupportedException();
    }
}
