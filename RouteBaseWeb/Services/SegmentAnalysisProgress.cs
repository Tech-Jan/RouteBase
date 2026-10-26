using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Queue;

namespace RouteBaseWeb.Services;

/// <summary>Typed snapshot of segment-analysis progress. Property names map 1:1 to the wire keys
/// (camelCase via web JSON defaults); CurrentSegment→current, TotalSegments→total,
/// CurrentSegmentName→segmentName preserve today's contract — pinned by ProgressWireKeyTests.</summary>
public sealed record AnalysisProgressSnapshot(
    bool IsRunning, int Current, int Total, string SegmentName, string Phase, string? Error,
    DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, int QueuePosition, int QueueTotal);

/// <summary>
/// Thread-safe singleton holding per-user progress for background segment analysis. The state
/// map + lifecycle live in <see cref="UserProgressCore{TExtra}"/> (CR5-01 part 2); this class
/// owns only the segment counters and current-segment name.
/// </summary>
public class SegmentAnalysisProgress(TimeProvider clock)
{
    private sealed class AnalysisExtras
    {
        public int CurrentSegment;
        public int TotalSegments;
        public string CurrentSegmentName = "";
    }

    private readonly UserProgressCore<AnalysisExtras> _core = new(clock);

    private static void ResetExtras(AnalysisExtras x, int totalSegments)
    {
        x.CurrentSegment = 0;
        x.TotalSegments = totalSegments;
        x.CurrentSegmentName = "";
    }

    // ── Progress mutation ──────────────────────────────────────────────────

    public bool TryStart(string userId, int totalSegments) =>
        _core.TryStart(userId, "Starting...", x => ResetExtras(x, totalSegments));

    public void ForceStart(string userId, int totalSegments) =>
        _core.ForceStart(userId, "Starting...", x => ResetExtras(x, totalSegments));

    public void SetQueueInfo(string userId, int position, int total) =>
        _core.SetQueueInfo(userId, position, total);

    public void UpdateProgress(string userId, int current, string segmentName, string phase) =>
        _core.Mutate(userId, s =>
        {
            s.Extra.CurrentSegment = current;
            s.Extra.CurrentSegmentName = segmentName;
            s.Phase = phase;
        });

    public void Complete(string userId) => _core.Complete(userId);

    public void Fail(string userId, string error) => _core.Fail(userId, error);

    public void Acknowledge(string userId) => _core.Acknowledge(userId);

    public AnalysisProgressSnapshot GetStatus(string userId) =>
        _core.Read(userId,
            s => new AnalysisProgressSnapshot(
                IsRunning: s.IsRunning, Current: s.Extra.CurrentSegment, Total: s.Extra.TotalSegments,
                SegmentName: s.Extra.CurrentSegmentName, Phase: s.Phase, Error: s.Error,
                StartedAt: s.StartedAt, CompletedAt: s.CompletedAt,
                QueuePosition: s.QueuePosition, QueueTotal: s.QueueTotal),
            idle: new AnalysisProgressSnapshot(
                IsRunning: false, Current: 0, Total: 0, SegmentName: "", Phase: "", Error: null,
                StartedAt: null, CompletedAt: null, QueuePosition: 0, QueueTotal: 0));

    /// <summary>Convenience: is analysis currently running for the given user?</summary>
    public bool IsRunning(string userId) => _core.IsRunning(userId);

    /// <summary>Builds the op-toast body for <see cref="IOperationHandler.GetQueueProgress"/> —
    /// moved verbatim from QueueApiController.NormaliseAnalysis (arch finding 1). Shared by
    /// SegmentAnalysisHandler, SingleSegmentAnalysisHandler and TrackEditDeriveHandler, which all
    /// compose this progress class and render as the same "analysis" toast.</summary>
    public QueueProgressSnapshot BuildQueueProgress(string userId, int pendingCountForUser)
    {
        var s = GetStatus(userId);
        int cur = s.Current, tot = s.Total;
        string name = s.SegmentName;

        string detail = tot > 0
            ? (name.Length > 0 ? $"{cur} / {tot} — {name}" : $"{cur} / {tot} segments")
            : "";

        string completedSummary = $"{tot} segments analyzed";

        int liveTotal = s.QueuePosition + pendingCountForUser;
        int queueTotal = Math.Max(s.QueueTotal, liveTotal);

        var body = new BasicProgressResponse(
            OperationType: QueueOperationTypes.Analysis,
            IsRunning: s.IsRunning,
            Phase: s.Phase,
            Current: cur,
            Total: tot,
            Detail: detail,
            CompletedSummary: completedSummary,
            Error: s.Error,
            StartedAt: s.StartedAt,
            CompletedAt: s.CompletedAt,
            QueuePosition: s.QueuePosition,
            QueueTotal: queueTotal);
        return new QueueProgressSnapshot(s.IsRunning, s.CompletedAt, body);
    }
}
