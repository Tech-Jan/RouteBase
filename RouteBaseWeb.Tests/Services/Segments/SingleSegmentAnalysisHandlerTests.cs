using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Services.Segments;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Segments;

/// <summary>
/// the one-segment <see cref="SingleSegmentAnalysisHandler"/> ran unverified in a background
/// worker. These drive its <see cref="SingleSegmentAnalysisHandler.RunAsync"/> body directly through
/// a DI scope and assert observable behaviour only — the segment/user/threshold passed to analyze,
/// the progress reported at that moment, completion, and cancellation vs a real failure. The handler
/// touches no DbContext, so a recording <see cref="ISegmentAnalysis"/> and a stub current-user
/// service are the whole graph.
/// </summary>
public sealed class SingleSegmentAnalysisHandlerTests
{
    private const string UserA = "user-a";

    private readonly RecordingSegmentAnalysis _analysis;
    private readonly SegmentAnalysisProgress _progress;
    private readonly SingleSegmentAnalysisHandler _handler;

    public SingleSegmentAnalysisHandlerTests()
    {
        _progress = new SegmentAnalysisProgress(TimeProvider.System);
        _analysis = new RecordingSegmentAnalysis(UserA, _progress);

        var services = new ServiceCollection();
        services.AddScoped<ICurrentUserService, StubCurrentUserService>();
        services.AddScoped<ISegmentAnalysis>(_ => _analysis);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        _handler = new SingleSegmentAnalysisHandler(
            scopeFactory, _progress, NullLogger<SingleSegmentAnalysisHandler>.Instance);
    }

    [Fact]
    public async Task Clean_run_analyzes_the_one_segment_with_resolved_threshold_and_completes()
    {
        _analysis.ThresholdToReturn = 42;

        await _handler.RunAsync(new SingleSegmentAnalysisPayload(UserA, 5, null), CancellationToken.None);

        var call = Assert.Single(_analysis.AnalyzeCalls);
        Assert.Equal(new SegmentId(5), call.SegmentId);
        Assert.Equal(UserA, call.UserId);
        Assert.Equal(42, call.Threshold);
        Assert.Equal(1, call.ProgressCurrent);
        Assert.Equal("Segment 5", call.ProgressName); // $"Segment {segmentId}"
        Assert.Equal("Analyzing...", call.ProgressPhase);

        // Threshold resolution received the payload's explicit value (null here → the user's default).
        Assert.True(_analysis.GetEffectiveWasCalled);
        Assert.Equal(UserA, _analysis.LastGetEffectiveUserId);
        Assert.Null(_analysis.LastGetEffectiveExplicit);

        var status = _progress.GetStatus(UserA);
        Assert.Equal("Completed", status.Phase);
        Assert.False(status.IsRunning);
        Assert.NotNull(status.CompletedAt);
    }

    [Fact]
    public async Task Explicit_threshold_from_the_payload_is_forwarded_to_resolution()
    {
        await _handler.RunAsync(new SingleSegmentAnalysisPayload(UserA, 5, 73), CancellationToken.None);

        Assert.Equal(73, _analysis.LastGetEffectiveExplicit);
    }

    [Fact]
    public async Task Already_running_progress_is_force_started_resetting_total_to_one()
    {
        _progress.ForceStart(UserA, 5); // stale running state carrying total 5

        await _handler.RunAsync(new SingleSegmentAnalysisPayload(UserA, 5, null), CancellationToken.None);

        var status = _progress.GetStatus(UserA);
        Assert.Equal("Completed", status.Phase);
        Assert.Equal(1, status.Total); // TryStart returned false → ForceStart(userId, 1) reset 5 → 1
        Assert.Single(_analysis.AnalyzeCalls);
    }

    [Fact]
    public async Task Pre_cancelled_token_rethrows_before_analyzing_and_never_completes()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _handler.RunAsync(new SingleSegmentAnalysisPayload(UserA, 5, null), cts.Token));

        Assert.Empty(_analysis.AnalyzeCalls); // ThrowIfCancellationRequested threw before analyze
        // OCE is rethrown before UpdateProgress runs, so the phase is still the TryStart value and the
        // op is still marked running — neither Complete nor Fail was reached (the `is not OCE` filter).
        var status = _progress.GetStatus(UserA);
        Assert.Equal("Starting...", status.Phase);
        Assert.True(status.IsRunning);
    }

    [Fact]
    public async Task Non_cancellation_failure_marks_progress_failed()
    {
        _analysis.ThrowOnAnalyze = new InvalidOperationException("boom");

        // Caught by `catch when (ex is not OperationCanceledException)` — must NOT propagate.
        await _handler.RunAsync(new SingleSegmentAnalysisPayload(UserA, 5, null), CancellationToken.None);

        var status = _progress.GetStatus(UserA);
        Assert.Equal("Failed", status.Phase);
        Assert.Equal("boom", status.Error);
        Assert.False(status.IsRunning);
        Assert.Single(_analysis.AnalyzeCalls);
    }

    // ── Fakes ────────────────────────────────────────────────────────────────

    /// <summary>Records the single analyze call and snapshots progress at that moment (before the
    /// terminal Complete overwrites the phase). Optionally throws on analyze.</summary>
    private sealed class RecordingSegmentAnalysis(string userId, SegmentAnalysisProgress progress) : ISegmentAnalysis
    {
        public int ThresholdToReturn = SegmentAnalysis.DefaultMatchThresholdPercent;
        public Exception? ThrowOnAnalyze;
        public bool GetEffectiveWasCalled;
        public string? LastGetEffectiveUserId;
        public int? LastGetEffectiveExplicit;
        public readonly List<AnalyzeCall> AnalyzeCalls = new();

        public sealed record AnalyzeCall(SegmentId SegmentId, string? UserId, int Threshold,
            int ProgressCurrent, string ProgressName, string ProgressPhase);

        public Task<int> GetEffectiveMatchThresholdAsync(string user, int? explicitPercent)
        {
            GetEffectiveWasCalled = true;
            LastGetEffectiveUserId = user;
            LastGetEffectiveExplicit = explicitPercent;
            return Task.FromResult(ThresholdToReturn);
        }

        public Task AnalyzeSegmentMatchesAsync(SegmentId segmentId, string? user = null,
            int matchThresholdPercent = SegmentAnalysis.DefaultMatchThresholdPercent, CancellationToken ct = default)
        {
            var s = progress.GetStatus(userId);
            AnalyzeCalls.Add(new AnalyzeCall(segmentId, user, matchThresholdPercent, s.Current, s.SegmentName, s.Phase));
            if (ThrowOnAnalyze is not null) throw ThrowOnAnalyze;
            return Task.CompletedTask;
        }

        public Task ClearOrphanedSegmentMatchesAsync(string user, IReadOnlyList<SegmentId> processedSegmentIds,
            CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> CanAnalyzeSegmentAsync(SegmentId segmentId) => throw new NotSupportedException();
        public Task MatchNewTrackAsync(TrackId trackId, string? user = null,
            int matchThresholdPercent = SegmentAnalysis.DefaultMatchThresholdPercent, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task MatchNewTracksAsync(IReadOnlyList<int> trackIds, string? user = null,
            int matchThresholdPercent = SegmentAnalysis.DefaultMatchThresholdPercent, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<SegmentId>> RemoveTrackFromSegmentsAsync(TrackId trackId, string? user = null,
            CancellationToken ct = default) => throw new NotSupportedException();
        public Task ClearAllSegmentMatchesAsync(string? user = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class StubCurrentUserService : ICurrentUserService
    {
        private string _userId = "";
        public string GetUserId() => _userId;
        // Added when the settings redesign gave anonymous a non-throwing read (R1: _Layout is the error
        // page's layout, so a throw there turns a handled 500 into a blank one). This stub is always a
        // signed-in user, so the two agree.
        public string? TryGetUserId() => _userId;
        public string? GetDisplayName() => null;
        public bool IsAdmin() => false;
        public void SetUserIdOverride(string userId) => _userId = userId;
        public IDisposable BeginUserScope(string userId) => throw new NotSupportedException();
    }
}
