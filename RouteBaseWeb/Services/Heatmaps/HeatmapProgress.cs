using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Queue;

namespace RouteBaseWeb.Services.Heatmaps;

/// <summary>Typed snapshot of heatmap-generation progress. Property names map 1:1 to the wire
/// keys (camelCase via web JSON defaults) — pinned by ProgressWireKeyTests.</summary>
public sealed record HeatmapProgressSnapshot(
    bool IsRunning, int SegmentId, int Current, int Total, string Phase, string? Error,
    DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, int QueuePosition, int QueueTotal);

/// <summary>
/// Thread-safe per-user progress for background heatmap generation, tracking a single segment's
/// build (paint → render). The state map + lifecycle live in
/// <see cref="UserProgressCore{TExtra}"/> (CR5-01 part 2); this class owns only the segment id
/// and paint/render counters.
/// </summary>
public class HeatmapProgress(TimeProvider clock)
{
    private sealed class HeatmapExtras
    {
        public SegmentId SegmentId;
        public int Current;
        public int Total;
    }

    private readonly UserProgressCore<HeatmapExtras> _core = new(clock);

    private static void ResetExtras(HeatmapExtras x, SegmentId segmentId)
    {
        x.SegmentId = segmentId;
        x.Current = 0;
        x.Total = 0;
    }

    public bool TryStart(string userId, SegmentId segmentId) =>
        _core.TryStart(userId, "Starting...", x => ResetExtras(x, segmentId));

    public void ForceStart(string userId, SegmentId segmentId) =>
        _core.ForceStart(userId, "Starting...", x => ResetExtras(x, segmentId));

    public void SetQueueInfo(string userId, int position, int total) =>
        _core.SetQueueInfo(userId, position, total);

    public void UpdateProgress(string userId, string phase, int current = 0, int total = 0) =>
        _core.Mutate(userId, s =>
        {
            s.Phase = phase;
            s.Extra.Current = current;
            s.Extra.Total = total;
        });

    public void Complete(string userId) => _core.Complete(userId);

    public void Fail(string userId, string error) => _core.Fail(userId, error);

    public HeatmapProgressSnapshot GetStatus(string userId) =>
        _core.Read(userId,
            s => new HeatmapProgressSnapshot(
                IsRunning: s.IsRunning, SegmentId: s.Extra.SegmentId.Value,
                Current: s.Extra.Current, Total: s.Extra.Total,
                Phase: s.Phase, Error: s.Error, StartedAt: s.StartedAt, CompletedAt: s.CompletedAt,
                QueuePosition: s.QueuePosition, QueueTotal: s.QueueTotal),
            idle: new HeatmapProgressSnapshot(
                IsRunning: false, SegmentId: 0, Current: 0, Total: 0, Phase: "", Error: null,
                StartedAt: null, CompletedAt: null, QueuePosition: 0, QueueTotal: 0));

    public bool IsRunning(string userId) => _core.IsRunning(userId);

    /// <summary>Clear a finished state so the toast stops re-showing it on the next poll/page load.</summary>
    public void Acknowledge(string userId) => _core.Acknowledge(userId);

    /// <summary>Builds the op-toast body for <see cref="IOperationHandler.GetQueueProgress"/> —
    /// moved verbatim from QueueApiController.NormaliseHeatmap (arch finding 1).</summary>
    public QueueProgressSnapshot BuildQueueProgress(string userId, int pendingCountForUser)
    {
        var s = GetStatus(userId);
        int cur = s.Current, tot = s.Total;

        // Heatmap phases carry their own labels (e.g. "Painting 3/12", "Rendering z=14"); the phase
        // text is already descriptive, so use it as the detail when a count is present.
        string detail = tot > 0 ? $"{cur} / {tot}" : "";

        int liveTotal = s.QueuePosition + pendingCountForUser;
        int queueTotal = Math.Max(s.QueueTotal, liveTotal);

        var body = new BasicProgressResponse(
            OperationType: QueueOperationTypes.Heatmap,
            IsRunning: s.IsRunning,
            Phase: s.Phase,
            Current: cur,
            Total: tot,
            Detail: detail,
            CompletedSummary: "Heatmap ready",
            Error: s.Error,
            StartedAt: s.StartedAt,
            CompletedAt: s.CompletedAt,
            QueuePosition: s.QueuePosition,
            QueueTotal: queueTotal);
        return new QueueProgressSnapshot(s.IsRunning, s.CompletedAt, body);
    }
}
