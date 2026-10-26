using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Tracks;
using RouteBaseWeb.Tests.Services.TrackEdit;   // TrackEditTestDb — the harness the version-history tests use
using Microsoft.Extensions.Logging;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Tracks;

/// <summary>
/// The re-derive enqueue under backpressure. The bounded operation queue (cap 100) rejects on full by returning
/// false; <see cref="TrackVersionHistory"/> used to discard that, skipping the segment/effort/heatmap/photo
/// re-derive with no signal anywhere. These pin the two halves of the fix: a queue-full restore still SUCCEEDS
/// but now LOGS an ERROR, and the metadata-only reset branch (behind <c>if (pointsMove)</c>) still enqueues
/// nothing at all.
///
/// The level was raised from Warning to Error deliberately: the Error_Log mirror filters at Error, so at
/// Warning a dropped derive never reached /Admin/Errors and the failure was undiagnosable after the fact.
/// </summary>
public class TrackVersionHistoryQueueTests
{
    private const string Me = "me";

    /// <summary>A full queue does not fail the restore — it still returns null (success) — but the dropped
    /// re-derive is now observable: exactly one Error naming the track, instead of a silent skip.</summary>
    [Fact]
    public async Task RestoreAsync_WhenQueueFull_LogsError_DoesNotThrow()
    {
        await using var db = await TrackEditTestDb.CreateAsync(Me);
        await db.AddActivityAsync(new TrackId(101), Me);
        await db.SeedRampAsync(new TrackId(101), 10);   // seq 1 — the version we'll restore
        await db.SeedRampAsync(new TrackId(101), 7);    // seq 2 — a different-geometry head
        var v1 = (await db.HistoryFor(Me).ListAsync(new TrackId(101))).Single(v => v.Seq == 1);

        // Fill the bounded channel to its cap so the restore's own enqueue is rejected. TryEnqueue returns
        // false once the 100-item channel is full — loop until it does, then confirm it is actually full.
        while (db.Queue.TryEnqueue(new OperationQueue.WorkItem
        {
            Type = OperationQueue.OperationType.TrackEditDerive,
            UserId = Me,
        })) { }
        Assert.False(db.Queue.TryEnqueue(new OperationQueue.WorkItem
        {
            Type = OperationQueue.OperationType.TrackEditDerive,
            UserId = Me,
        }));

        // The log now comes from the shared TrackDeriveQueue seam (every derive enqueuer routes through it),
        // not from TrackVersionHistory — same contract, one owner.
        var logger = new CapturingLogger<RouteBaseWeb.Services.TrackEdit.TrackDeriveQueue>();
        var result = await db.HistoryFor(Me, logger).RestoreAsync(new TrackId(101), new VersionId(v1.VersionId));

        Assert.Null(result);   // success — a full queue must not fail the restore
        var dropped = Assert.Single(logger.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains("101", dropped.Message, StringComparison.Ordinal);   // the dropped-derive log names the track
        // It must also say WHAT stays broken — pins the second half of the message (L259).
        Assert.Contains("stale", dropped.Message, StringComparison.Ordinal);
    }

    /// <summary>The <c>:189</c> enqueue is behind <c>if (pointsMove)</c>: a reset whose head already points at
    /// the original's content (a prior restore re-used its object) moves no points, so it must delete the
    /// intermediate versions WITHOUT enqueuing a re-derive. Pins that guard — an unconditional enqueue here
    /// would churn the queue on every metadata-only reset.</summary>
    [Fact]
    public async Task Reset_MetadataOnlyVersionDelete_DoesNotEnqueue()
    {
        await using var db = await TrackEditTestDb.CreateAsync(Me);
        await db.AddActivityAsync(new TrackId(101), Me);
        await db.SeedRampAsync(new TrackId(101), 10);   // seq 1 — original
        await db.SeedRampAsync(new TrackId(101), 7);    // seq 2 — a different head
        var v1 = (await db.HistoryFor(Me).ListAsync(new TrackId(101))).Single(v => v.Seq == 1);

        // Restore the original's content: seq 3 re-uses seq 1's object (zero new bytes), so the head now shares
        // the original's sha. This restore DOES enqueue (queue not full) — capture the depth after it.
        Assert.Null(await db.HistoryFor(Me).RestoreAsync(new TrackId(101), new VersionId(v1.VersionId)));
        int enqueuedBeforeReset = db.Queue.PendingCountFor(Me);
        Assert.Equal(3, (await db.HistoryFor(Me).ListAsync(new TrackId(101))).Count);   // seq 1, 2, 3 all present

        // Reset deletes seq 2 and seq 3, leaving the original. head.ObjectSha == original.ObjectSha ⇒
        // pointsMove == false ⇒ no derive is enqueued.
        Assert.Null(await db.HistoryFor(Me).ResetToOriginalAsync(new TrackId(101)));

        Assert.Single(await db.HistoryFor(Me).ListAsync(new TrackId(101)));                 // only the original survives
        Assert.Equal(enqueuedBeforeReset, db.Queue.PendingCountFor(Me));       // the reset enqueued nothing
    }
}

/// <summary>Records every log call so a test can assert a warning fired. Mirrors the CapturingLogger in
/// ApiExceptionFilterTests (file-scoped there, so not shareable).</summary>
file sealed class CapturingLogger<T> : ILogger<T>
{
    public readonly List<(LogLevel Level, string Message)> Entries = new();
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
        => Entries.Add((logLevel, formatter(state, exception)));
}
