using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Sync;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Services.Lifecycle;
using Xunit;

namespace RouteBaseWeb.Tests.Services;

/// <summary>
/// The post-import segment match must run for whatever actually landed, even when the import is cancelled
/// or fails partway.
///
/// The bug: the match sat in the try body, after the import loop. Every early exit skipped it — an OCE from
/// <c>ct.ThrowIfCancellationRequested()</c>, the rate-limit <c>return</c>, or any caught exception — leaving
/// committed tracks with zero efforts. That did not self-heal, because a re-import pre-skips those tracks as
/// already present, so they never re-matched; only a later analysis that happened to touch them fixed it.
///
/// These pin the salvage, not the happy path: cancel after one activity has landed, then assert the match
/// still ran for it. A regression that reintroduces cancellation-sensitivity here is invisible to every
/// other test, because the import itself still "succeeds" from the queue's point of view.
/// </summary>
public class ImportMatchSalvageTests
{
    [Fact]
    public async Task ActivityImport_CancelledMidBatch_StillMatchesWhatWasImported()
    {
        var segments = new RecordingSegmentAnalysis();
        using var cts = new CancellationTokenSource();

        // Two activities. The first imports (track 101); importing it trips the cancel, so the loop's
        // ThrowIfCancellationRequested fires on the second iteration — the exact shape of a user cancel
        // landing mid-batch.
        var provider = new CancellingSyncProvider(cts, importedTrackId: 101, activityCount: 2);

        var services = new ServiceCollection().AddSingleton(TimeProvider.System);
        services.AddScoped<ICurrentUserService, StubCurrentUser>();
        services.AddScoped<IActivityImportService, NoopImportService>();
        services.AddSingleton<IActivitySyncService>(provider);
        services.AddSingleton<ISegmentAnalysis>(segments);
        await using var sp = services.BuildServiceProvider();

        var handler = new ActivityImportHandler(
            sp.GetRequiredService<IServiceScopeFactory>(),
            new ActivityImportProgress(TimeProvider.System),
            NullLogger<ActivityImportHandler>.Instance);

        // The handler deliberately does not swallow OCE (its catch filters it out), so cancellation still
        // surfaces to the queue. The salvage happens in the finally on the way out.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => handler.RunAsync(new ActivityImportPayload("u", "fake", null), cts.Token));

        Assert.Equal([101], segments.MatchedTrackIds);
    }

    [Fact]
    public async Task ActivityImport_NothingImported_DoesNotCallTheMatch()
    {
        // The salvage must stay free when a cancel lands before anything was committed — otherwise every
        // cancelled import would pay for a match with an empty set.
        var segments = new RecordingSegmentAnalysis();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var provider = new CancellingSyncProvider(cts, importedTrackId: 101, activityCount: 2);

        var services = new ServiceCollection().AddSingleton(TimeProvider.System);
        services.AddScoped<ICurrentUserService, StubCurrentUser>();
        services.AddScoped<IActivityImportService, NoopImportService>();
        services.AddSingleton<IActivitySyncService>(provider);
        services.AddSingleton<ISegmentAnalysis>(segments);
        await using var sp = services.BuildServiceProvider();

        var handler = new ActivityImportHandler(
            sp.GetRequiredService<IServiceScopeFactory>(),
            new ActivityImportProgress(TimeProvider.System),
            NullLogger<ActivityImportHandler>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => handler.RunAsync(new ActivityImportPayload("u", "fake", null), cts.Token));

        Assert.False(segments.MatchWasCalled);
    }

    // ── doubles ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Imports one activity, then cancels — so the next loop iteration throws.</summary>
    private sealed class CancellingSyncProvider(CancellationTokenSource cts, int importedTrackId, int activityCount)
        : IActivitySyncService
    {
        public string ProviderId => "fake";
        public string DisplayName => "Fake";

        public Task<List<ExternalActivitySummary>> FetchAllActivitiesAsync(DateTime? after = null) =>
            Task.FromResult(Enumerable.Range(1, activityCount)
                .Select(i => new ExternalActivitySummary { ExternalId = i, ProviderId = "fake", Name = $"a{i}" })
                .ToList());

        public Task<Activity?> ImportActivityAsync(ExternalActivitySummary externalActivity, string? userId = null)
        {
            cts.Cancel();
            return Task.FromResult<Activity?>(new Activity { TrackId = new TrackId(importedTrackId), UserId = userId ?? "u" });
        }
    }

    private sealed class RecordingSegmentAnalysis : ISegmentAnalysis
    {
        public bool MatchWasCalled;
        public IReadOnlyList<int> MatchedTrackIds = [];

        public Task<int> GetEffectiveMatchThresholdAsync(string userId, int? explicitPercent) => Task.FromResult(40);

        public Task MatchNewTracksAsync(IReadOnlyList<int> trackIds, string? userId = null,
            int matchThresholdPercent = SegmentAnalysis.DefaultMatchThresholdPercent, CancellationToken ct = default)
        {
            // Honouring the token is load-bearing, not politeness. The regression this test guards is the
            // handler passing its own (cancelled) ct into the salvage instead of CancellationToken.None —
            // and a double that ignored ct would record the match either way, making the test vacuous.
            // It was, until a RED check caught it.
            ct.ThrowIfCancellationRequested();
            MatchWasCalled = true;
            MatchedTrackIds = trackIds;
            return Task.CompletedTask;
        }

        public Task<bool> CanAnalyzeSegmentAsync(SegmentId segmentId) => throw new NotImplementedException();
        public Task AnalyzeSegmentMatchesAsync(SegmentId segmentId, string? userId = null,
            int matchThresholdPercent = SegmentAnalysis.DefaultMatchThresholdPercent, CancellationToken ct = default) =>
            throw new NotImplementedException();
        public Task MatchNewTrackAsync(TrackId trackId, string? userId = null,
            int matchThresholdPercent = SegmentAnalysis.DefaultMatchThresholdPercent, CancellationToken ct = default) =>
            throw new NotImplementedException();
        public Task<IReadOnlyList<SegmentId>> RemoveTrackFromSegmentsAsync(TrackId trackId, string? userId = null,
            CancellationToken ct = default) => throw new NotImplementedException();
        public Task ClearAllSegmentMatchesAsync(string? userId = null, CancellationToken ct = default) =>
            throw new NotImplementedException();
        public Task ClearOrphanedSegmentMatchesAsync(string userId, IReadOnlyList<SegmentId> processedSegmentIds,
            CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class NoopImportService : IActivityImportService
    {
        public Task<ExistingActivity?> FindExistingAsync(long externalId, string source, string userId) =>
            Task.FromResult<ExistingActivity?>(null);
        public Task UpsertActivityBboxAsync(TrackId trackId) => Task.CompletedTask;
        public Task<Activity?> ImportFromSummaryAsync(ExternalActivitySummary summary,
            List<ExternalGpsPoint> gpsPoints, string userId) => throw new NotImplementedException();
        public Task WriteTrackBlobAsync(TrackId trackId, List<ExternalGpsPoint> points) => throw new NotImplementedException();
        public double ComputeTotalDescent(List<ExternalGpsPoint> points) => throw new NotImplementedException();
        public Task UpsertActivityBboxAsync(TrackId trackId, IReadOnlyList<ExternalGpsPoint> points) =>
            throw new NotImplementedException();
    }

    private sealed class StubCurrentUser : ICurrentUserService
    {
        private string _userId = "";
        public string GetUserId() => _userId;
        public string? TryGetUserId() => _userId;
        public string? GetDisplayName() => null;
        public bool IsAdmin() => false;
        public void SetUserIdOverride(string userId) => _userId = userId;
        public IDisposable BeginUserScope(string userId)
        {
            var previous = _userId;
            _userId = userId;
            return new Restore(this, previous);
        }
        private sealed class Restore(StubCurrentUser s, string previous) : IDisposable
        {
            public void Dispose() => s._userId = previous;
        }
    }
}
