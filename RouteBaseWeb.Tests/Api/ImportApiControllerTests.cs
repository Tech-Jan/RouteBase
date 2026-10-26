using RouteBaseWeb.Api;
using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Uploads;
using RouteBaseWeb.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace RouteBaseWeb.Tests.Api;

/// <summary>
/// GI-06: committed-batch state machine. A duplicate commit must not enqueue a second work item
/// (which would free the per-user import reservation now owned by a fresh import), and files may
/// not be appended after commit. Uses the real TrackImportStaging + real OperationQueue.
/// </summary>
// Serial with the other real-staging tests: this stages a real batch under the shared routebase-import
// root, which TrackImportStagingTests' CleanupOrphans wipe test would delete mid-test if run concurrently.
[Collection("TrackImportRealStagingSerial")]
public class ImportApiControllerTests
{
    private const string UserId = "gi06-user";

    private static ImportApiController NewController(TrackImportStaging staging, OperationQueue queue) =>
        new(new TestCurrentUserService(UserId), staging, new TrackUploadPolicy(new TrackUploadLimits()), queue,
            NullLogger<ImportApiController>.Instance);

    [Fact]
    public async Task CommitBatch_AlreadyCommitted_Returns409_AndDoesNotEnqueue()
    {
        var staging = new TrackImportStaging(NullLogger<TrackImportStaging>.Instance, TimeProvider.System);
        var queue = new OperationQueue(TimeProvider.System);
        queue.TryReserveExclusiveSlot(UserId);
        var batchId = staging.BeginBatch(UserId, useFilenameAsId: false);
        try
        {
            await staging.AppendFilesAsync(UserId, batchId, new[] { FormFile("a.gpx", "AAA") }, default);
            var controller = NewController(staging, queue);

            var first = await controller.CommitBatch(batchId);
            Assert.IsType<OkObjectResult>(first);
            Assert.Equal(1, queue.PendingCountFor(UserId));

            var second = await controller.CommitBatch(batchId);
            Assert.IsType<ConflictObjectResult>(second);
            Assert.Equal(1, queue.PendingCountFor(UserId)); // no second work item enqueued
        }
        finally
        {
            staging.Delete(UserId, batchId);
        }
    }

    [Fact]
    public async Task AppendBatchFiles_AfterCommit_Returns409()
    {
        var staging = new TrackImportStaging(NullLogger<TrackImportStaging>.Instance, TimeProvider.System);
        var queue = new OperationQueue(TimeProvider.System);
        queue.TryReserveExclusiveSlot(UserId);
        var batchId = staging.BeginBatch(UserId, useFilenameAsId: false);
        try
        {
            await staging.AppendFilesAsync(UserId, batchId, new[] { FormFile("a.gpx", "AAA") }, default);
            Assert.True(await staging.TryMarkCommittedAsync(UserId, batchId));
            var controller = NewController(staging, queue);

            var result = await controller.AppendBatchFiles(
                batchId, new List<IFormFile> { FormFile("b.gpx", "BBB") });
            Assert.IsType<ConflictObjectResult>(result);
        }
        finally
        {
            staging.Delete(UserId, batchId);
        }
    }

    // ── Error-body wire contract on the hostile-upload paths (D31/D32, 2026-07-24) ──
    // The wire body is uniformly { code, message, … }: ApiError for plain errors,
    // PolicyRejectedBody (message + files + defaulted code) for the policy reject.

    [Fact]
    public async Task AppendBatchFiles_UnrecognizedContent_Returns400PolicyRejectedBody()
    {
        var staging = new TrackImportStaging(NullLogger<TrackImportStaging>.Instance, TimeProvider.System);
        var queue = new OperationQueue(TimeProvider.System);
        queue.TryReserveExclusiveSlot(UserId);
        var batchId = staging.BeginBatch(UserId, useFilenameAsId: false);
        try
        {
            var controller = NewController(staging, queue);

            // Sniffs Unknown (no XML root, no ".FIT" signature) → policy allowlist reject.
            var result = await controller.AppendBatchFiles(
                batchId, new List<IFormFile> { FormFile("evil.gpx", "MZBINARYJUNK") });

            var bad = Assert.IsType<BadRequestObjectResult>(result);
            var body = Assert.IsType<PolicyRejectedBody>(bad.Value);
            Assert.Equal(ApiErrorCodes.BadRequest, body.Code);
            Assert.Contains("GPX, TCX, and FIT", body.Message, StringComparison.Ordinal);
            Assert.Contains("evil.gpx", body.Files!, StringComparer.Ordinal);
        }
        finally
        {
            staging.Delete(UserId, batchId);
        }
    }

    [Fact]
    public async Task CommitBatch_UnknownBatch_Returns404ApiError()
    {
        var controller = NewController(
            new TrackImportStaging(NullLogger<TrackImportStaging>.Instance, TimeProvider.System), new OperationQueue(TimeProvider.System));

        var result = await controller.CommitBatch("no-such-batch");

        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        var body = Assert.IsType<ApiError>(notFound.Value);
        Assert.Equal(ApiErrorCodes.NotFound, body.Code);
        Assert.False(string.IsNullOrWhiteSpace(body.Message));
    }

    [Fact]
    public void BeginBatch_WhileImportSlotHeld_Returns409ApiError()
    {
        var staging = new TrackImportStaging(NullLogger<TrackImportStaging>.Instance, TimeProvider.System);
        var queue = new OperationQueue(TimeProvider.System);
        var controller = NewController(staging, queue);

        var first = controller.BeginBatch();
        var ok = Assert.IsType<OkObjectResult>(first);
        var batchId = Assert.IsType<BeginBatchResponse>(ok.Value).BatchId!;
        try
        {
            var second = controller.BeginBatch();

            var conflict = Assert.IsType<ConflictObjectResult>(second);
            var body = Assert.IsType<ApiError>(conflict.Value);
            Assert.Equal(ApiErrorCodes.Conflict, body.Code);
            Assert.False(string.IsNullOrWhiteSpace(body.Message));
        }
        finally
        {
            staging.Delete(UserId, batchId);
        }
    }

    private static IFormFile FormFile(string fileName, string content)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "files", fileName);
    }
}
