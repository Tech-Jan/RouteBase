using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Queue;

namespace RouteBaseWeb.Services.Photos;

/// <summary>A candidate activity offered inline for an ambiguous/GPS-only photo (§3a). StartLocal is the
/// activity's local start for display — a wall-clock string (FE-12), never an instant.</summary>
public sealed record PhotoCandidateDto(int TrackId, string Name, string StartLocal, double? GpsDistanceM);

/// <summary>Per-file outcome of a photo import. Outcome ∈ { importing, imported, duplicate, ambiguous,
/// unmatched, failed }. <see cref="StagedName"/> + the batch id let the UI resolve ambiguous/unmatched
/// files via the assign endpoint; <see cref="Candidates"/> are the activities to pick from.</summary>
public sealed record PhotoFileResult(
    string FileName, string Outcome, string? Error,
    string? StagedName, IReadOnlyList<PhotoCandidateDto>? Candidates);

/// <summary>Typed snapshot of photo-import progress for the Photos-page results panel + op-toast.</summary>
public sealed record PhotoImportSnapshot(
    bool IsRunning, int Current, int Total,
    int Imported, int Duplicate, int Ambiguous, int Unmatched, int Failed,
    string Phase, string? Error, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt,
    string BatchId, IReadOnlyList<PhotoFileResult> Results);

/// <summary>
/// Thread-safe per-user photo-import progress. The state map + lifecycle live in
/// <see cref="UserProgressCore{TExtra}"/> (CR5-01 part 2); this class owns the running counters +
/// per-file results (incl. candidate lists for interactive resolve). Deliberately exposes no
/// queue fields — the Photos page polls its own import-status endpoint, outside the op-toast
/// queue rotation.
/// </summary>
public class PhotoImportProgress(TimeProvider clock)
{
    private const int MaxResultsPerUser = 5000;

    private sealed class PhotoExtras
    {
        public int Total;
        public int Imported, Duplicate, Ambiguous, Unmatched, Failed;
        public string BatchId = "";
        public readonly List<PhotoFileResult> Results = new();
    }

    private readonly UserProgressCore<PhotoExtras> _core = new(clock);

    public void Start(string userId, string batchId, int total) =>
        _core.ForceStart(userId, "Importing...", x =>
        {
            x.Total = total;
            x.Imported = x.Duplicate = x.Ambiguous = x.Unmatched = x.Failed = 0;
            x.BatchId = batchId;
            x.Results.Clear();
        });

    public void RecordFileStarted(string userId, string fileName) =>
        _core.Mutate(userId, s =>
        {
            if (s.Extra.Results.Count >= MaxResultsPerUser) return;
            s.Extra.Results.Add(new PhotoFileResult(fileName, "importing", null, null, null));
        });

    public void RecordFileCompleted(string userId, PhotoFileResult result) =>
        _core.Mutate(userId, s =>
        {
            var x = s.Extra;
            int idx = -1;
            for (int i = x.Results.Count - 1; i >= 0; i--)
                if (x.Results[i].FileName == result.FileName && x.Results[i].Outcome == "importing") { idx = i; break; }

            if (idx >= 0) x.Results[idx] = result;
            else if (x.Results.Count < MaxResultsPerUser) x.Results.Add(result);

            switch (result.Outcome)
            {
                case "imported":  x.Imported++;  break;
                case "duplicate": x.Duplicate++; break;
                case "ambiguous": x.Ambiguous++; break;
                case "unmatched": x.Unmatched++; break;
                case "failed":    x.Failed++;    break;
            }
        });

    /// <summary>Replace a resolved (assigned/kept) staged result with its final imported outcome, so a
    /// re-poll shows it moved out of the pending list. Adjusts counters.</summary>
    public void ResolveStagedFile(string userId, string stagedName, string newOutcome, TrackId trackId) =>
        _core.Mutate(userId, s =>
        {
            var x = s.Extra;
            for (int i = 0; i < x.Results.Count; i++)
            {
                if (x.Results[i].StagedName == stagedName)
                {
                    var prev = x.Results[i];
                    switch (prev.Outcome) { case "ambiguous": x.Ambiguous--; break; case "unmatched": x.Unmatched--; break; case "duplicate": x.Duplicate--; break; }
                    x.Results[i] = prev with { Outcome = newOutcome, Candidates = null, StagedName = null };
                    if (newOutcome == "imported") x.Imported++;
                    break;
                }
            }
        });

    public void Complete(string userId) => _core.Complete(userId);

    public void Fail(string userId, string error) => _core.Fail(userId, error);

    public PhotoImportSnapshot GetStatus(string userId) =>
        _core.Read(userId,
            s =>
            {
                var x = s.Extra;
                int current = x.Imported + x.Duplicate + x.Ambiguous + x.Unmatched + x.Failed;
                return new PhotoImportSnapshot(
                    s.IsRunning, current, x.Total,
                    x.Imported, x.Duplicate, x.Ambiguous, x.Unmatched, x.Failed,
                    s.Phase, s.Error, s.StartedAt, s.CompletedAt,
                    x.BatchId, x.Results.ToArray());
            },
            idle: new PhotoImportSnapshot(false, 0, 0, 0, 0, 0, 0, 0, "", null, null, null, "", Array.Empty<PhotoFileResult>()));

    // Lock-based like every other progress class (was a Volatile.Read drift — CR5-01).
    public bool IsRunning(string userId) => _core.IsRunning(userId);
}
