using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Lifecycle;
using RouteBaseWeb.Services.Queue;

namespace RouteBaseWeb.Services;

/// <summary>
/// Per-file outcome reported by the GPX import orchestrator.
/// Outcome ∈ { "importing", "imported", "skipped", "failed" }.
/// </summary>
/// <summary>Detail = the badge tooltip: an error message for "failed", the explanation for a skip.</summary>
public record FileResult(string FileName, string Outcome, string? Detail);

/// <summary>Typed snapshot of activity-import progress. Property names map 1:1 to the wire keys
/// (camelCase via web JSON defaults) the op-toast client reads — pinned by ProgressWireKeyTests.</summary>
public sealed record ImportProgressSnapshot(
    bool IsRunning, int Current, int Total, int Imported, int Skipped, int Failed,
    string ActivityName, string Phase, string? Error, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt,
    int QueuePosition, int QueueTotal, string Source, IReadOnlyList<FileResult> Results);

/// <summary>
/// Thread-safe singleton holding per-user progress for background activity import. The state map
/// + lifecycle live in <see cref="UserProgressCore{TExtra}"/> (CR5-01 part 2); this class owns
/// only the import-specific counters, source discriminator and per-file results.
/// </summary>
public class ActivityImportProgress(TimeProvider clock)
{
    // Cap per-user Results to bound memory under pathological batches.
    private const int MaxResultsPerUser = 5000;

    private sealed class ImportExtras
    {
        public int Current;
        public int Total;
        public int Imported;
        public int Skipped;
        public int Failed;
        public string CurrentActivityName = "";
        public string Source = "";  // "gpx" | provider id (e.g. "strava") — discriminator for QueueApiController
        public readonly List<FileResult> Results = new();
    }

    private readonly UserProgressCore<ImportExtras> _core = new(clock);

    private static void ResetExtras(ImportExtras x, int total, string source)
    {
        x.Current = 0;
        x.Total = total;
        x.Imported = 0;
        x.Skipped = 0;
        x.Failed = 0;
        x.CurrentActivityName = "";
        x.Source = source;
        x.Results.Clear();
    }

    // ── Progress mutation ──────────────────────────────────────────────────

    public bool TryStart(string userId, int total, string source) =>
        _core.TryStart(userId, "Starting...", x => ResetExtras(x, total, source));

    public void ForceStart(string userId, int total, string source) =>
        _core.ForceStart(userId, "Starting...", x => ResetExtras(x, total, source));

    public void SetTotal(string userId, int total) =>
        _core.Mutate(userId, s => s.Extra.Total = total);

    public void SetQueueInfo(string userId, int position, int total) =>
        _core.SetQueueInfo(userId, position, total);

    public void UpdateProgress(string userId, int current, string activityName, string phase,
        int imported, int skipped, int failed) =>
        _core.Mutate(userId, s =>
        {
            s.Extra.Current = current;
            s.Extra.CurrentActivityName = activityName;
            s.Phase = phase;
            s.Extra.Imported = imported;
            s.Extra.Skipped = skipped;
            s.Extra.Failed = failed;
        });

    /// <summary>
    /// Per-file: record that a file has started importing. Appends an "importing"
    /// FileResult and sets CurrentActivityName. Used by the GPX path.
    /// </summary>
    public void RecordFileStarted(string userId, string fileName) =>
        _core.Mutate(userId, s =>
        {
            var x = s.Extra;
            if (x.Results.Count >= MaxResultsPerUser) return;
            x.Results.Add(new FileResult(fileName, "importing", null));
            x.CurrentActivityName = fileName;
            // Persister has dequeued the first parsed file — phase advances from
            // "Parsing files..." (set by the worker pre-call) to the active import phase.
            s.Phase = "Importing...";
        });

    /// <summary>
    /// Per-file: record the final outcome of a file. Replaces the existing
    /// in-flight "importing" entry (if any) and updates the matching counter.
    /// Outcome ∈ { "imported", "skipped", "in-trash", "in-work", "failed" }. The last three all COUNT as
    /// skipped — they are the same decision (the file is already imported, don't import it twice), told
    /// truthfully. Only "duplicate/skipped" used to be sayable, which is what left a trashed ride looking
    /// like a redundant file.
    /// </summary>
    public void RecordFileCompleted(string userId, string fileName, string outcome, string? detail) =>
        _core.Mutate(userId, s =>
        {
            var x = s.Extra;
            int idx = -1;
            for (int i = x.Results.Count - 1; i >= 0; i--)
            {
                if (x.Results[i].FileName == fileName && x.Results[i].Outcome == "importing")
                {
                    idx = i;
                    break;
                }
            }

            var entry = new FileResult(fileName, outcome, detail);
            if (idx >= 0) x.Results[idx] = entry;
            else if (x.Results.Count < MaxResultsPerUser) x.Results.Add(entry);

            switch (outcome)
            {
                case "imported": x.Imported++; break;
                case "failed":   x.Failed++;   break;
                case ImportSkipReason.Skipped:
                case ImportSkipReason.InTrash:
                case ImportSkipReason.InWork:  x.Skipped++; break;
            }
            x.Current = x.Imported + x.Skipped + x.Failed;
        });

    public void Complete(string userId, int imported, int skipped, int failed) =>
        _core.Complete(userId, x =>
        {
            x.Imported = imported;
            x.Skipped = skipped;
            x.Failed = failed;
        });

    public void Fail(string userId, string error) => _core.Fail(userId, error);

    public void Acknowledge(string userId) => _core.Acknowledge(userId);

    public ImportProgressSnapshot GetStatus(string userId) =>
        _core.Read(userId,
            s => new ImportProgressSnapshot(
                IsRunning: s.IsRunning, Current: s.Extra.Current, Total: s.Extra.Total,
                Imported: s.Extra.Imported, Skipped: s.Extra.Skipped, Failed: s.Extra.Failed,
                ActivityName: s.Extra.CurrentActivityName, Phase: s.Phase, Error: s.Error,
                StartedAt: s.StartedAt, CompletedAt: s.CompletedAt,
                QueuePosition: s.QueuePosition, QueueTotal: s.QueueTotal,
                // Snapshot Results so callers can read it outside the lock.
                Source: s.Extra.Source, Results: s.Extra.Results.ToArray()),
            idle: new ImportProgressSnapshot(
                IsRunning: false, Current: 0, Total: 0, Imported: 0, Skipped: 0, Failed: 0,
                ActivityName: "", Phase: "", Error: null, StartedAt: null, CompletedAt: null,
                QueuePosition: 0, QueueTotal: 0, Source: "", Results: Array.Empty<FileResult>()));

    /// <summary>Convenience: is the import currently running for the given user?</summary>
    public bool IsRunning(string userId) => _core.IsRunning(userId);

    /// <summary>Builds the op-toast body for <see cref="IOperationHandler.GetQueueProgress"/> —
    /// moved verbatim from QueueApiController.NormaliseImport (arch finding 1). Shared by
    /// ActivityImportHandler and TrackImportHandler, which both compose this progress class; the
    /// "import" vs "trackImport" discriminator is data-driven (Source), not handler-driven.</summary>
    public QueueProgressSnapshot BuildQueueProgress(string userId, int pendingCountForUser)
    {
        var s = GetStatus(userId);
        int cur = s.Current, tot = s.Total;
        string name = s.ActivityName;
        bool isGpx = s.Source == "gpx";

        string detail = tot > 0
            ? (name.Length > 0 ? $"{cur} / {tot} — {name}" : $"{cur} / {tot} {(isGpx ? "files" : "activities")}")
            // Provider-neutral: ActivityImportHandler is documented provider-agnostic (arch finding 3), so no
            // hardcoded provider name here — a second source must not render "…from Strava".
            : (isGpx ? "Preparing files..." : "Fetching activity list...");

        string completedSummary = $"{s.Imported} imported, {s.Skipped} skipped, {s.Failed} failed";

        int liveTotal = s.QueuePosition + pendingCountForUser;
        int queueTotal = Math.Max(s.QueueTotal, liveTotal);

        var body = new ImportProgressResponse(
            OperationType: isGpx ? QueueOperationTypes.TrackImport : QueueOperationTypes.Import,
            IsRunning: s.IsRunning,
            Phase: s.Phase,
            Current: cur,
            Total: tot,
            Detail: detail,
            CompletedSummary: completedSummary,
            Imported: s.Imported,
            Skipped: s.Skipped,
            Failed: s.Failed,
            Error: s.Error,
            StartedAt: s.StartedAt,
            CompletedAt: s.CompletedAt,
            QueuePosition: s.QueuePosition,
            QueueTotal: queueTotal,
            Results: s.Results);
        return new QueueProgressSnapshot(s.IsRunning, s.CompletedAt, body);
    }
}
