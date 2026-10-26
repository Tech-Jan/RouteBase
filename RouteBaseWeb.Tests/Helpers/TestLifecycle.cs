using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Heatmaps;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Services.Lifecycle;
using Microsoft.Extensions.Logging.Abstractions;

namespace RouteBaseWeb.Tests.Helpers;

/// <summary>
/// Builds a REAL <see cref="ActivityLifecycleService"/> for tests that construct lifecycle-dependent services
/// by hand. Real, not a stub, because the lifecycle rules (validation, lifecycle_prev bookkeeping, the audit
/// row) are exactly what those tests are implicitly relying on — a fake would let a broken transition pass.
///
/// Only the heatmap side-effect is faked: marking heatmaps stale is a genuine side-effect of leaving
/// circulation, but it needs the whole tile stack, and no test here asserts on tiles.
/// </summary>
public static class TestLifecycle
{
    /// <param name="heatmaps">Pass a real <c>HeatmapLifecycle</c> to assert on the staleness side-effect;
    /// omit it for the no-op.</param>
    public static ActivityLifecycleService For(
        RouteBaseDbContext context, IVisibilityService visibility, UserCacheInvalidator invalidator,
        IHeatmapLifecycle? heatmaps = null) =>
        new(context, visibility, invalidator, heatmaps ?? new NoOpHeatmapLifecycle(),
            NullLogger<ActivityLifecycleService>.Instance, TimeProvider.System);

    /// <summary>No-op heatmap lifecycle — records nothing, asserts nothing.</summary>
    public sealed class NoOpHeatmapLifecycle : IHeatmapLifecycle
    {
        public Task<HeatmapAttachResult> AttachAsync(SegmentId segmentId, IEnumerable<string> activityTypes) =>
            Task.FromResult(HeatmapAttachResult.NotFound);
        public Task FailPendingAsync(SegmentId segmentId, string error) => Task.CompletedTask;
        public Task<Heatmap?> GetVisibleAsync(SegmentId segmentId, CancellationToken ct = default) =>
            Task.FromResult<Heatmap?>(null);
        public Task WithdrawAsync(SegmentId segmentId, CancellationToken ct = default) => Task.CompletedTask;
        public Task MarkStaleAsync(SegmentId segmentId, CancellationToken ct = default) => Task.CompletedTask;
        public Task MarkStaleForTrackAsync(TrackId trackId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<int?> CaptureIdForSegmentAsync(SegmentId segmentId, CancellationToken ct = default) =>
            Task.FromResult<int?>(null);
        public void PurgeTiles(int heatmapId) { }
    }
}
