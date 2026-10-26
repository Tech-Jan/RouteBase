using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Services.Segments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Segments;

/// <summary>
/// the analyze-all <see cref="SegmentAnalysisHandler"/> ran unverified in a background worker.
/// These drive its <see cref="SegmentAnalysisHandler.RunAsync"/> body directly through a DI scope
/// (matching <c>OperationQueueWorkerHeatmapTests</c>' in-memory-SQLite shape) and assert only
/// observable behaviour — which segments were analysed, in what order, what progress was reported,
/// and how it reacts to cancellation vs a real failure. A recording <see cref="ISegmentAnalysis"/>
/// snapshots progress at each analyze call so the mid-run counters/name/phase are observable before
/// the terminal Complete overwrites them.
/// </summary>
public sealed class SegmentAnalysisHandlerTests : IAsyncLifetime
{
    private const string UserA = "user-a";
    private const string UserB = "user-b";

    private RouteBaseDbContext _context = null!;
    private DbContextOptions<RouteBaseDbContext> _options = null!;
    private ServiceProvider _provider = null!;
    private RecordingSegmentAnalysis _analysis = null!;
    private SegmentAnalysisProgress _progress = null!;
    private SegmentAnalysisHandler _handler = null!;

    public async Task InitializeAsync()
    {
        // Shared-cache in-memory SQLite; the open connection on _context pins the DB alive so the
        // handler's own scoped RouteBaseDbContext sees the seeded rows (same pattern as the heatmap
        // worker tests).
        _options = new DbContextOptionsBuilder<RouteBaseDbContext>()
            .UseSqlite($"Data Source=file:seghandler_{Guid.NewGuid():N}?mode=memory&cache=shared").Options;
        _context = new RouteBaseDbContext(_options);
        await _context.Database.OpenConnectionAsync();
        await _context.Database.EnsureCreatedAsync();

        _context.Users.AddRange(
            new AppUser { UserId = UserA, DisplayName = "A", Email = "a@t.com", IsAdmin = false },
            new AppUser { UserId = UserB, DisplayName = "B", Email = "b@t.com", IsAdmin = false });
        await _context.SaveChangesAsync();

        _progress = new SegmentAnalysisProgress(TimeProvider.System);
        _analysis = new RecordingSegmentAnalysis(UserA, _progress);

        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<ICurrentUserService, StubCurrentUserService>();
        services.AddScoped(_ => new RouteBaseDbContext(_options));
        services.AddScoped<ISegmentAnalysis>(_ => _analysis);
        _provider = services.BuildServiceProvider();

        _handler = new SegmentAnalysisHandler(
            _provider.GetRequiredService<IServiceScopeFactory>(), _progress,
            NullLogger<SegmentAnalysisHandler>.Instance);
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _context.Database.CloseConnectionAsync();
        await _context.DisposeAsync();
    }

    private async Task SeedSegmentsAsync(params (int Id, string? Name, string UserId)[] segments)
    {
        foreach (var s in segments)
            _context.Segments.Add(new Segment
            {
                SegmentId = new SegmentId(s.Id), SegmentName = s.Name, UserId = s.UserId, ActivityType = "Ride",
            });
        await _context.SaveChangesAsync();
    }

    [Fact]
    public async Task Clean_pass_analyzes_only_own_segments_in_name_order_then_clears_orphans_and_completes()
    {
        // Ids deliberately out of name order so ascending-by-name != id order != descending.
        await SeedSegmentsAsync(
            (10, "Charlie", UserA),
            (20, "Alpha", UserA),
            (30, "Bravo", UserA),
            (99, "Zeta", UserB)); // another user's segment — must never be touched
        _analysis.ThresholdToReturn = 42;

        await _handler.RunAsync(new SegmentAnalysisPayload(UserA), CancellationToken.None);

        // Ownership filter (== userId) + OrderBy(SegmentName) ascending: exactly UserA's three, Alpha→Bravo→Charlie.
        Assert.Equal(new[] { 20, 30, 10 }, _analysis.AnalyzeCalls.Select(c => c.SegmentId.Value).ToArray());
        Assert.All(_analysis.AnalyzeCalls, c => Assert.Equal(UserA, c.UserId));
        Assert.All(_analysis.AnalyzeCalls, c => Assert.Equal(42, c.Threshold)); // resolved threshold flows through

        // Progress observed AT each analyze call: 1-based counter (i + 1), the real name, "Analyzing..." phase.
        Assert.Equal(new[] { 1, 2, 3 }, _analysis.AnalyzeCalls.Select(c => c.ProgressCurrent).ToArray());
        Assert.Equal(new[] { "Alpha", "Bravo", "Charlie" }, _analysis.AnalyzeCalls.Select(c => c.ProgressName).ToArray());
        Assert.All(_analysis.AnalyzeCalls, c => Assert.Equal("Analyzing...", c.ProgressPhase));

        // Orphan-wipe runs once, after the loop, over exactly the processed ids in processing order.
        Assert.Equal(1, _analysis.ClearOrphanedCallCount);
        Assert.Equal(UserA, _analysis.ClearOrphanedUserId);
        Assert.Equal(new[] { 20, 30, 10 }, _analysis.ClearOrphanedProcessedIds!.Select(x => x.Value).ToArray());

        var status = _progress.GetStatus(UserA);
        Assert.Equal("Completed", status.Phase);
        Assert.False(status.IsRunning);
        Assert.NotNull(status.CompletedAt);
        Assert.Equal(3, status.Total);
    }

    [Fact]
    public async Task Null_segment_name_falls_back_to_the_segment_id_label_in_progress()
    {
        await SeedSegmentsAsync((7, null, UserA));

        await _handler.RunAsync(new SegmentAnalysisPayload(UserA), CancellationToken.None);

        var call = Assert.Single(_analysis.AnalyzeCalls);
        Assert.Equal("Segment 7", call.ProgressName); // seg.SegmentName ?? $"Segment {seg.SegmentId}"
    }

    [Fact]
    public async Task Already_running_progress_is_force_started_resetting_the_segment_total()
    {
        await SeedSegmentsAsync((1, "A", UserA), (2, "B", UserA));
        _progress.ForceStart(UserA, 99); // pre-existing running state carrying a stale total

        await _handler.RunAsync(new SegmentAnalysisPayload(UserA), CancellationToken.None);

        var status = _progress.GetStatus(UserA);
        Assert.Equal("Completed", status.Phase);
        Assert.Equal(2, status.Total); // TryStart returned false → ForceStart(userId, 2) reset 99 → 2
        Assert.Equal(2, _analysis.AnalyzeCalls.Count);
    }

    [Fact]
    public async Task Cancellation_mid_loop_rethrows_and_skips_completion_and_the_orphan_wipe()
    {
        await SeedSegmentsAsync((1, "A", UserA), (2, "B", UserA));
        using var cts = new CancellationTokenSource();
        _analysis.CancelOnAnalyze = cts; // the first analyze cancels the token

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _handler.RunAsync(new SegmentAnalysisPayload(UserA), cts.Token));

        Assert.Single(_analysis.AnalyzeCalls);          // ThrowIfCancellationRequested stopped the 2nd iteration
        Assert.Equal(0, _analysis.ClearOrphanedCallCount);
        // OCE is rethrown (the `is not OperationCanceledException` filter), so neither Complete nor Fail
        // ran: progress is frozen in its last in-loop state ("Analyzing..."), still running, no error.
        var status = _progress.GetStatus(UserA);
        Assert.Equal("Analyzing...", status.Phase);
        Assert.True(status.IsRunning);
        Assert.Null(status.Error);
    }

    [Fact]
    public async Task Non_cancellation_failure_marks_progress_failed_without_completing_or_wiping()
    {
        await SeedSegmentsAsync((1, "A", UserA), (2, "B", UserA));
        _analysis.ThrowOnAnalyze = new InvalidOperationException("boom");

        // Caught by `catch when (ex is not OperationCanceledException)` — must NOT propagate.
        await _handler.RunAsync(new SegmentAnalysisPayload(UserA), CancellationToken.None);

        var status = _progress.GetStatus(UserA);
        Assert.Equal("Failed", status.Phase);
        Assert.Equal("boom", status.Error);
        Assert.False(status.IsRunning);
        Assert.Equal(0, _analysis.ClearOrphanedCallCount);
        Assert.Single(_analysis.AnalyzeCalls); // threw on the first segment
    }

    // ── Fakes ────────────────────────────────────────────────────────────────

    /// <summary>Records every analyze/clear call and snapshots progress at analyze time (before the
    /// terminal Complete overwrites the phase). Optionally cancels a token or throws on analyze.</summary>
    private sealed class RecordingSegmentAnalysis(string userId, SegmentAnalysisProgress progress) : ISegmentAnalysis
    {
        public int ThresholdToReturn = SegmentAnalysis.DefaultMatchThresholdPercent;
        public Exception? ThrowOnAnalyze;
        public CancellationTokenSource? CancelOnAnalyze;
        public readonly List<AnalyzeCall> AnalyzeCalls = new();
        public int ClearOrphanedCallCount;
        public string? ClearOrphanedUserId;
        public IReadOnlyList<SegmentId>? ClearOrphanedProcessedIds;

        public sealed record AnalyzeCall(SegmentId SegmentId, string? UserId, int Threshold,
            int ProgressCurrent, int ProgressTotal, string ProgressName, string ProgressPhase);

        public Task<int> GetEffectiveMatchThresholdAsync(string user, int? explicitPercent) =>
            Task.FromResult(ThresholdToReturn);

        public Task AnalyzeSegmentMatchesAsync(SegmentId segmentId, string? user = null,
            int matchThresholdPercent = SegmentAnalysis.DefaultMatchThresholdPercent, CancellationToken ct = default)
        {
            var s = progress.GetStatus(userId);
            AnalyzeCalls.Add(new AnalyzeCall(segmentId, user, matchThresholdPercent,
                s.Current, s.Total, s.SegmentName, s.Phase));
            CancelOnAnalyze?.Cancel();
            if (ThrowOnAnalyze is not null) throw ThrowOnAnalyze;
            return Task.CompletedTask;
        }

        public Task ClearOrphanedSegmentMatchesAsync(string user, IReadOnlyList<SegmentId> processedSegmentIds,
            CancellationToken ct = default)
        {
            ClearOrphanedCallCount++;
            ClearOrphanedUserId = user;
            ClearOrphanedProcessedIds = processedSegmentIds.ToList();
            return Task.CompletedTask;
        }

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
