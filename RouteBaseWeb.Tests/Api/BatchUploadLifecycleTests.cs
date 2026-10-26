using RouteBaseWeb.Api;
using RouteBaseWeb.Models.Uploads;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Photos;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace RouteBaseWeb.Tests.Api;

/// <summary>
/// CR5-02 lifecycle characterization pins: the twin-fixed batch-upload behaviors, pinned on BOTH
/// verticals (Track ↔ Photo) through their public seams before the Services/Uploads extraction.
/// Covers the halves the existing suites leave unpinned: photo-side GI-11/GI-05 twins (parallel chunk
/// appends, collision suffixing), photo controller 409s (append-after-commit PI-05, duplicate-commit
/// PI-04 incl. loser-performs-no-delete/no-release), abort semantics on both verticals (unknown batch
/// → aborted=false + reservation untouched; abort-after-commit → 409 + files kept), and torn manifest
/// .tmp survival (IE-02 ↔ PI-08: readers see old-or-new, never torn).
/// </summary>
// Serial with the other real-staging tests: track batches live under the shared routebase-import root,
// which TrackImportStagingTests' CleanupOrphans wipe test would delete mid-test if run concurrently.
[Collection("TrackImportRealStagingSerial")]
public class BatchUploadLifecycleTests
{
    private static readonly string TrackRoot = Path.Combine(Path.GetTempPath(), "routebase-import");
    private static readonly string PhotoRoot = Path.Combine(Path.GetTempPath(), "routebase-photos");

    private static ImportApiController TrackController(string userId, TrackImportStaging staging, OperationQueue queue) =>
        new(new TestCurrentUserService(userId), staging, new TrackUploadPolicy(new TrackUploadLimits()), queue,
            NullLogger<ImportApiController>.Instance);

    // Batch endpoints touch only staging + policy + queue + current user; the remaining deps are
    // never dereferenced on these paths (trackId stays null → no ownership query).
    private static PhotosApiController PhotoController(string userId, PhotoImportStaging staging, OperationQueue queue) =>
        new(staging, new PhotoUploadPolicy(new PhotoUploadLimits()), queue,
            new TestCurrentUserService(userId), photoQuery: null!, photoCommand: null!, activities: null!,
            progress: null!, orchestrator: null!,
            NullLogger<PhotosApiController>.Instance);

    private static IFormFile FormFile(string fileName, string content)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "files", fileName);
    }

    private static bool Aborted(IActionResult result) =>
        Assert.IsType<AbortBatchResponse>(Assert.IsAssignableFrom<ObjectResult>(result).Value).Aborted;

    // ── Abort of an unknown batch: aborted=false, reservation untouched (both verticals) ──

    [Fact]
    public async Task Track_AbortUnknownBatch_AbortedFalse_ReservationUntouched()
    {
        var userId = "cr502-track-" + Guid.NewGuid().ToString("N");
        var staging = new TrackImportStaging(NullLogger<TrackImportStaging>.Instance, TimeProvider.System);
        var queue = new OperationQueue(TimeProvider.System);
        Assert.True(queue.TryReserveExclusiveSlot(userId));

        var result = await TrackController(userId, staging, queue).AbortBatch(Guid.NewGuid().ToString("N"));

        Assert.False(Aborted(result));
        Assert.False(queue.TryReserveExclusiveSlot(userId)); // reservation still held — abort must not release it
    }

    [Fact]
    public async Task Photo_AbortUnknownBatch_AbortedFalse_ReservationUntouched()
    {
        var userId = "cr502-photo-" + Guid.NewGuid().ToString("N");
        var staging = new PhotoImportStaging(NullLogger<PhotoImportStaging>.Instance, TimeProvider.System);
        var queue = new OperationQueue(TimeProvider.System);
        Assert.True(queue.TryReserveExclusiveSlot(userId));

        var result = await PhotoController(userId, staging, queue).AbortBatch(Guid.NewGuid().ToString("N"));

        Assert.False(Aborted(result));
        Assert.False(queue.TryReserveExclusiveSlot(userId));
    }

    // ── Abort after commit: 409, files kept (both verticals) ──────────────────────────────

    [Fact]
    public async Task Track_AbortAfterCommit_Returns409_AndKeepsFiles()
    {
        var userId = "cr502-track-" + Guid.NewGuid().ToString("N");
        var staging = new TrackImportStaging(NullLogger<TrackImportStaging>.Instance, TimeProvider.System);
        var queue = new OperationQueue(TimeProvider.System);
        queue.TryReserveExclusiveSlot(userId);
        var batchId = staging.BeginBatch(userId, useFilenameAsId: false);
        try
        {
            await staging.AppendFilesAsync(userId, batchId, new[] { FormFile("a.gpx", "AAA") }, default);
            Assert.True(await staging.TryMarkCommittedAsync(userId, batchId));

            var result = await TrackController(userId, staging, queue).AbortBatch(batchId);

            Assert.IsType<ConflictObjectResult>(result);
            var batch = await staging.LoadAsync(userId, batchId);
            Assert.NotNull(batch); // the running import's files must survive a late abort
            Assert.Single(batch!.Files);
            Assert.False(queue.TryReserveExclusiveSlot(userId)); // reservation still owned by the import
        }
        finally
        {
            staging.Delete(userId, batchId);
        }
    }

    [Fact]
    public async Task Photo_AbortAfterCommit_Returns409_AndKeepsFiles()
    {
        var userId = "cr502-photo-" + Guid.NewGuid().ToString("N");
        var staging = new PhotoImportStaging(NullLogger<PhotoImportStaging>.Instance, TimeProvider.System);
        var queue = new OperationQueue(TimeProvider.System);
        queue.TryReserveExclusiveSlot(userId);
        var batchId = staging.BeginBatch(userId, previewMaxEdge: 1024);
        try
        {
            await staging.AppendFilesAsync(userId, batchId, new[] { FormFile("a.jpg", "AAA") }, default);
            Assert.True(await staging.TryMarkCommittedAsync(userId, batchId));

            var result = await PhotoController(userId, staging, queue).AbortBatch(batchId);

            Assert.IsType<ConflictObjectResult>(result);
            var batch = await staging.LoadAsync(userId, batchId);
            Assert.NotNull(batch);
            Assert.Single(batch!.Files);
            Assert.False(queue.TryReserveExclusiveSlot(userId));
        }
        finally
        {
            staging.Delete(userId, batchId);
        }
    }

    // ── Duplicate commit: 409, loser performs no delete + no release (photo twin of GI-06/IE-01) ──

    [Fact]
    public async Task Photo_DuplicateCommit_Returns409_NoSecondEnqueue_NoDelete_NoRelease()
    {
        var userId = "cr502-photo-" + Guid.NewGuid().ToString("N");
        var staging = new PhotoImportStaging(NullLogger<PhotoImportStaging>.Instance, TimeProvider.System);
        var queue = new OperationQueue(TimeProvider.System);
        queue.TryReserveExclusiveSlot(userId);
        var batchId = staging.BeginBatch(userId, previewMaxEdge: 1024);
        try
        {
            await staging.AppendFilesAsync(userId, batchId, new[] { FormFile("a.jpg", "AAA") }, default);
            var controller = PhotoController(userId, staging, queue);

            var first = await controller.CommitBatch(batchId);
            Assert.IsType<OkObjectResult>(first);
            Assert.Equal(1, queue.PendingCountFor(userId));

            var second = await controller.CommitBatch(batchId);
            Assert.IsType<ConflictObjectResult>(second);
            Assert.Equal(1, queue.PendingCountFor(userId));       // no second work item
            Assert.NotNull(await staging.LoadAsync(userId, batchId)); // loser must not delete
            Assert.False(queue.TryReserveExclusiveSlot(userId));          // loser must not release
        }
        finally
        {
            staging.Delete(userId, batchId);
        }
    }

    // ── Append after commit: 409 through the controller (photo twin of IE-06/PI-05) ─────────

    [Fact]
    public async Task Photo_AppendAfterCommit_Returns409()
    {
        var userId = "cr502-photo-" + Guid.NewGuid().ToString("N");
        var staging = new PhotoImportStaging(NullLogger<PhotoImportStaging>.Instance, TimeProvider.System);
        var queue = new OperationQueue(TimeProvider.System);
        queue.TryReserveExclusiveSlot(userId);
        var batchId = staging.BeginBatch(userId, previewMaxEdge: 1024);
        try
        {
            await staging.AppendFilesAsync(userId, batchId, new[] { FormFile("a.jpg", "AAA") }, default);
            Assert.True(await staging.TryMarkCommittedAsync(userId, batchId));

            var result = await PhotoController(userId, staging, queue).AppendBatchFiles(
                batchId, new List<IFormFile> { FormFile("late.jpg", "BBB") });

            Assert.IsType<ConflictObjectResult>(result);
        }
        finally
        {
            staging.Delete(userId, batchId);
        }
    }

    // ── GI-11 twin (photo): manifest RMW survives concurrent chunk appends ──────────────────

    [Fact]
    public async Task Photo_TwoParallelChunkAppends_AllFilesInManifest()
    {
        var userId = "cr502-photo-" + Guid.NewGuid().ToString("N");
        var staging = new PhotoImportStaging(NullLogger<PhotoImportStaging>.Instance, TimeProvider.System);
        var batchId = staging.BeginBatch(userId, previewMaxEdge: 1024);
        try
        {
            var chunk1 = Enumerable.Range(0, 10).Select(i => FormFile($"a{i}.jpg", $"A{i}")).ToArray();
            var chunk2 = Enumerable.Range(0, 10).Select(i => FormFile($"b{i}.jpg", $"B{i}")).ToArray();

            await Task.WhenAll(
                staging.AppendFilesAsync(userId, batchId, chunk1, default),
                staging.AppendFilesAsync(userId, batchId, chunk2, default));

            var batch = await staging.LoadAsync(userId, batchId);
            Assert.NotNull(batch);
            Assert.Equal(20, batch!.Files.Count);
            Assert.Equal(20, batch.Files.Select(f => f.StagedPath).Distinct(StringComparer.Ordinal).Count());
        }
        finally
        {
            staging.Delete(userId, batchId);
        }
    }

    // ── GI-05 twin (photo): collision suffix probes disk, keeps every file ──────────────────

    [Fact]
    public async Task Photo_TripleCollision_KeepsAllThreeFiles()
    {
        var userId = "cr502-photo-" + Guid.NewGuid().ToString("N");
        var staging = new PhotoImportStaging(NullLogger<PhotoImportStaging>.Instance, TimeProvider.System);
        var batchId = staging.BeginBatch(userId, previewMaxEdge: 1024);
        try
        {
            await staging.AppendFilesAsync(userId, batchId, new[] { FormFile("a.jpg", "AAA") }, default);
            await staging.AppendFilesAsync(userId, batchId, new[] { FormFile("a_1.jpg", "BBB") }, default);
            await staging.AppendFilesAsync(userId, batchId, new[] { FormFile("a.jpg", "CCC") }, default);

            var batch = await staging.LoadAsync(userId, batchId);
            Assert.NotNull(batch);
            Assert.Equal(3, batch!.Files.Count);
            Assert.Equal(3, batch.Files.Select(f => f.StagedPath).Distinct(StringComparer.Ordinal).Count());

            var contents = new List<string>();
            foreach (var f in batch.Files)
                contents.Add(await File.ReadAllTextAsync(f.StagedPath));
            Assert.Equal(new[] { "AAA", "BBB", "CCC" }, contents.OrderBy(c => c, StringComparer.Ordinal).ToArray());
        }
        finally
        {
            staging.Delete(userId, batchId);
        }
    }

    // ── IE-02 ↔ PI-08: a torn/garbage manifest.json.tmp never corrupts reads or later writes ──

    [Fact]
    public async Task Track_Load_SurvivesTornManifestTmp()
    {
        var userId = "cr502-track-" + Guid.NewGuid().ToString("N");
        var staging = new TrackImportStaging(NullLogger<TrackImportStaging>.Instance, TimeProvider.System);
        var batchId = staging.BeginBatch(userId, useFilenameAsId: false);
        try
        {
            await staging.AppendFilesAsync(userId, batchId, new[] { FormFile("a.gpx", "AAA") }, default);
            await File.WriteAllTextAsync(
                Path.Combine(TrackRoot, userId, batchId, "manifest.json.tmp"), "{ torn garbage");

            var batch = await staging.LoadAsync(userId, batchId); // readers only ever see manifest.json
            Assert.NotNull(batch);
            Assert.Single(batch!.Files);

            // The next atomic write overwrites the stray .tmp instead of failing.
            await staging.AppendFilesAsync(userId, batchId, new[] { FormFile("b.gpx", "BBB") }, default);
            var after = await staging.LoadAsync(userId, batchId);
            Assert.Equal(2, after!.Files.Count);
        }
        finally
        {
            staging.Delete(userId, batchId);
        }
    }

    [Fact]
    public async Task Photo_Load_SurvivesTornManifestTmp()
    {
        var userId = "cr502-photo-" + Guid.NewGuid().ToString("N");
        var staging = new PhotoImportStaging(NullLogger<PhotoImportStaging>.Instance, TimeProvider.System);
        var batchId = staging.BeginBatch(userId, previewMaxEdge: 1024);
        try
        {
            await staging.AppendFilesAsync(userId, batchId, new[] { FormFile("a.jpg", "AAA") }, default);
            await File.WriteAllTextAsync(
                Path.Combine(PhotoRoot, userId, batchId, "manifest.json.tmp"), "{ torn garbage");

            var batch = await staging.LoadAsync(userId, batchId);
            Assert.NotNull(batch);
            Assert.Single(batch!.Files);

            await staging.AppendFilesAsync(userId, batchId, new[] { FormFile("b.jpg", "BBB") }, default);
            var after = await staging.LoadAsync(userId, batchId);
            Assert.Equal(2, after!.Files.Count);
        }
        finally
        {
            staging.Delete(userId, batchId);
        }
    }
}
