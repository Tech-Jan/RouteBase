namespace RouteBaseWeb.Services.Uploads;

/// <summary>Begin outcome: <c>Reserved=false</c> means another import holds the per-user slot.</summary>
public sealed record BeginBatchResult(bool Reserved, string? BatchId = null);

public enum BatchAppendOutcome { Staged, BatchNotFound, AlreadyCommitted, PolicyRejected }

public sealed record BatchAppendResult(
    BatchAppendOutcome Outcome, int Staged = 0, string? Error = null, IReadOnlyList<string>? RejectedFiles = null);

public enum BatchCommitOutcome { Queued, BatchNotFound, EmptyBatch, AlreadyCommitted, LostCommitRace, QueueFull }

public sealed record BatchCommitResult(BatchCommitOutcome Outcome, int QueuedFiles = 0);

public enum BatchAbortOutcome { Aborted, UnknownBatch, AlreadyCommitted }

/// <summary>
/// Shared begin/append/commit/abort sequencing for chunked batch uploads (CR5-02) — the reservation +
/// commit-race + cleanup semantics that were twin-maintained across the ImportApiController and
/// PhotosApiController endpoint quartets. Owns the ordering invariants:
/// <list type="bullet">
/// <item>begin: atomic per-user import-slot reservation before the batch dir exists; any begin failure
/// releases the slot (two uploads — or an upload + a Strava import — must not race the old non-atomic
/// IsRunning check);</item>
/// <item>append: pre-check committed → policy guard over cumulative staged count/bytes → stage; an
/// <see cref="InvalidOperationException"/> from staging means the in-lock guard raced a commit
/// (IE-06 ↔ PI-05) — also maps a permanently-unreadable manifest, equally terminal;</item>
/// <item>commit: mark committed before enqueue so a late/duplicate abort can't delete this import's
/// files or release its reservation mid-run (GI-06); a false TryMarkCommittedAsync means "lost the
/// commit race or the batch vanished" (IE-01 ↔ PI-04) — the winner owns cleanup + the reservation,
/// so the loser performs no delete and no release; a full queue rolls back (delete + release);</item>
/// <item>abort: an unknown/already-cleaned batch must not touch the reservation, which may now belong
/// to a fresh import (the worker deletes the dir before releasing); a committed batch refuses — the
/// running import owns its files and slot.</item>
/// </list>
/// Controllers stay the HTTP mapping layer: they translate these outcomes to today's exact status
/// codes + JSON bodies and keep feature-local checks (photo ownership/extension hygiene, logging).
/// </summary>
public sealed class BatchUploadCoordinator
{
    private readonly IBatchUploadStaging _staging;
    private readonly OperationQueue _queue;
    private readonly int _headerBytes;
    private readonly Func<IReadOnlyList<UploadCandidate>, int, long, UploadPolicyVerdict> _evaluate;
    private readonly Func<string, string, OperationQueue.WorkItem> _createWorkItem;

    /// <param name="headerBytes">The feature policy's <c>HeaderBytes</c> sniff-peek size.</param>
    /// <param name="evaluate">The feature policy's <c>Evaluate</c> (candidates, stagedCount, stagedBytes).</param>
    /// <param name="createWorkItem">Builds the feature's queue work item from (userId, batchId).</param>
    public BatchUploadCoordinator(
        IBatchUploadStaging staging, OperationQueue queue, int headerBytes,
        Func<IReadOnlyList<UploadCandidate>, int, long, UploadPolicyVerdict> evaluate,
        Func<string, string, OperationQueue.WorkItem> createWorkItem)
    {
        _staging        = staging;
        _queue          = queue;
        _headerBytes    = headerBytes;
        _evaluate       = evaluate;
        _createWorkItem = createWorkItem;
    }

    /// <summary>Reserves the per-user import slot, then runs <paramref name="beginBatch"/> (the
    /// feature's BeginBatch with its own parameters). Any failure releases the slot and rethrows.</summary>
    public BeginBatchResult Begin(string userId, Func<string> beginBatch)
    {
        if (!_queue.TryReserveExclusiveSlot(userId))
            return new BeginBatchResult(Reserved: false);

        try
        {
            return new BeginBatchResult(Reserved: true, beginBatch());
        }
        catch
        {
            _queue.ReleaseExclusiveSlot(userId);
            throw;
        }
    }

    /// <summary>Stages a non-empty chunk into an open batch after the policy guard.</summary>
    public async Task<BatchAppendResult> AppendAsync(
        string userId, string batchId, IReadOnlyList<IFormFile> files, CancellationToken ct)
    {
        var batch = await _staging.LoadInfoAsync(userId, batchId);
        if (batch == null)
            return new BatchAppendResult(BatchAppendOutcome.BatchNotFound);
        // Pre-check (GI-06): the import may already be running and late files would be
        // deleted unimported (Current > Total in the toast).
        if (batch.Committed)
            return new BatchAppendResult(BatchAppendOutcome.AlreadyCommitted);

        // Safe-upload guard: content-sniff allowlist (replaces extension trust) + size/count caps.
        var candidates = await UploadCandidateReader.ReadAsync(files, _headerBytes, ct);
        var verdict = _evaluate(candidates, batch.FileCount, batch.TotalBytes);
        if (!verdict.Ok)
            return new BatchAppendResult(BatchAppendOutcome.PolicyRejected, Error: verdict.Error, RejectedFiles: verdict.Files);

        try
        {
            var staged = await _staging.AppendFilesAsync(userId, batchId, files, ct);
            return new BatchAppendResult(BatchAppendOutcome.Staged, staged);
        }
        catch (InvalidOperationException)
        {
            // In-lock guard raced a commit (IE-06 ↔ PI-05): the batch is already committed/running.
            return new BatchAppendResult(BatchAppendOutcome.AlreadyCommitted);
        }
    }

    /// <summary>Marks the batch committed and enqueues its work item (see class doc for the race rules).</summary>
    public async Task<BatchCommitResult> CommitAsync(string userId, string batchId)
    {
        var batch = await _staging.LoadInfoAsync(userId, batchId);
        if (batch == null)
            return new BatchCommitResult(BatchCommitOutcome.BatchNotFound);

        if (batch.FileCount == 0)
        {
            DeleteAndRelease(userId, batchId);
            return new BatchCommitResult(BatchCommitOutcome.EmptyBatch);
        }

        // GI-06: a duplicate commit (retry/double-click) must not re-mark committed and enqueue a
        // second work item — the phantom run's finally would free a reservation now owned by a
        // fresh import, breaking the per-user import gate.
        if (batch.Committed)
            return new BatchCommitResult(BatchCommitOutcome.AlreadyCommitted);

        if (!await _staging.TryMarkCommittedAsync(userId, batchId))
            return new BatchCommitResult(BatchCommitOutcome.LostCommitRace);

        if (!_queue.TryEnqueue(_createWorkItem(userId, batchId)))
        {
            DeleteAndRelease(userId, batchId);
            return new BatchCommitResult(BatchCommitOutcome.QueueFull);
        }

        return new BatchCommitResult(BatchCommitOutcome.Queued, batch.FileCount);
    }

    /// <summary>Discards an uncommitted batch and releases the slot (see class doc for the abort rules).</summary>
    public async Task<BatchAbortOutcome> AbortAsync(string userId, string batchId)
    {
        var batch = await _staging.LoadInfoAsync(userId, batchId);
        if (batch == null)
            return BatchAbortOutcome.UnknownBatch;
        if (batch.Committed)
            return BatchAbortOutcome.AlreadyCommitted;

        DeleteAndRelease(userId, batchId);
        return BatchAbortOutcome.Aborted;
    }

    // Order matters: files first, then the slot — mirrors the worker, which deletes the dir
    // before releasing, so a released slot never points at a half-deleted batch.
    private void DeleteAndRelease(string userId, string batchId)
    {
        _staging.Delete(userId, batchId);
        _queue.ReleaseExclusiveSlot(userId);
    }
}
