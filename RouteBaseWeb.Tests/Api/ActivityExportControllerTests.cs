using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using RouteBaseWeb.Api;
using RouteBaseWeb.Models;
using RouteBaseWeb.Models.GridFilter;
using RouteBaseWeb.Models.Requests;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Interfaces;
using Xunit;

namespace RouteBaseWeb.Tests.Api;

/// <summary>
/// POST /api/activities/export — the activities page's "Export GPX (N)" button. It ENQUEUES: zipping a few
/// hundred rides takes minutes, so the request must return immediately with the URL the file will appear
/// at, and the work happens on the operation queue (<see cref="ActivityExportHandler"/>).
///
/// The two things worth pinning here are that the reply names a real future download, and that the
/// download endpoint is keyed by OWNER — a guessed job id must not serve someone else's ZIP.
/// </summary>
public class ActivityExportControllerTests
{
    private const string Me = "me";
    private const string Other = "other";

    [Fact]
    public async Task QueueExport_EnqueuesTheJob_AndAnswersWithItsDownloadUrl()
    {
        var queue = new OperationQueue(TimeProvider.System);
        var controller = Build(queue, out _);

        var result = controller.QueueExport(new ActivityExportRequest { TrackIds = [2, 5, 7] });

        var accepted = Assert.IsType<AcceptedResult>(result);
        var body = Assert.IsType<ActivityExportQueuedResponse>(accepted.Value);
        Assert.Equal($"/api/activities/export/download/{body.JobId}", body.DownloadUrl);
        Assert.Equal(3, body.RequestedCount);
        Assert.Equal(1, queue.PendingCountFor(Me));

        // The ids ride to the handler untouched — ownership is resolved there, off the request thread.
        await using var reader = queue.ReadAllAsync(CancellationToken.None).GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(OperationQueue.OperationType.ActivityExport, reader.Current.Type);
        var payload = Assert.IsType<ActivityExportPayload>(reader.Current.Payload);
        Assert.Equal([2, 5, 7], payload.TrackIds);
        Assert.Equal(body.JobId, payload.JobId);
    }

    /// <summary>
    /// One export at a time per user. A second click while the first is building would overwrite the single
    /// per-user progress state, leaving the toast pointing at a job whose file it can no longer name.
    /// </summary>
    [Fact]
    public void QueueExport_WhileOneIsAlreadyRunning_IsRejected()
    {
        var queue = new OperationQueue(TimeProvider.System);
        var controller = Build(queue, out var progress);
        progress.ForceStart(Me, jobId: 1);

        var result = controller.QueueExport(new ActivityExportRequest { TrackIds = [2] });

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.IsType<ApiError>(conflict.Value);
        Assert.Equal(0, queue.PendingCountFor(Me));
    }

    [Fact]
    public void QueueExport_EmptyIdSet_IsRejected()
    {
        var controller = Build(new OperationQueue(TimeProvider.System), out _);

        var result = controller.QueueExport(new ActivityExportRequest());

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.IsType<ApiError>(bad.Value);
    }

    [Fact]
    public void Download_ServesTheCallersOwnFinishedExport()
    {
        var store = new FakeStore();
        store.Files[(Me, 7)] = [1, 2, 3];
        var controller = Build(new OperationQueue(TimeProvider.System), out _, store);

        var file = Assert.IsType<FileStreamResult>(controller.Download(7));
        Assert.Equal("application/zip", file.ContentType);
        Assert.Equal(ActivityExportProgress.ExportFileName, file.FileDownloadName);
    }

    /// <summary>
    /// Another user's job id answers exactly like a job that never existed — the store keys the file by
    /// owner, so there is no existence oracle to probe (the surface's 404-never-403 convention).
    /// </summary>
    [Fact]
    public void Download_SomeoneElsesJobId_Is404_NotAnExistenceOracle()
    {
        var store = new FakeStore();
        store.Files[(Other, 7)] = [1, 2, 3];
        var controller = Build(new OperationQueue(TimeProvider.System), out _, store);

        var mine = Assert.IsType<NotFoundObjectResult>(controller.Download(7));
        var absent = Assert.IsType<NotFoundObjectResult>(controller.Download(999));
        Assert.Equal(
            Assert.IsType<ApiError>(absent.Value).Message,
            Assert.IsType<ApiError>(mine.Value).Message);
    }

    private static ActivityExportController Build(
        OperationQueue queue, out ActivityExportProgress progress, FakeStore? store = null)
    {
        progress = new ActivityExportProgress(TimeProvider.System);
        var controller = new ActivityExportController(
            new UnusedGpxExport(), new UnusedBulkExport(), new FakeQuery(),
            new TestCurrentUserService(Me), queue, progress, store ?? new FakeStore())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        controller.Response.Body = new MemoryStream();
        return controller;
    }

    private sealed class FakeStore : IActivityExportStore
    {
        public Dictionary<(string UserId, long JobId), byte[]> Files { get; } = new();
        private long _next;

        public long NextJobId() => ++_next;
        public void Commit(string userId, long jobId, string builtFilePath) => Files[(userId, jobId)] = [];
        public Stream? OpenRead(string userId, long jobId) =>
            Files.TryGetValue((userId, jobId), out var bytes) ? new MemoryStream(bytes) : null;
        public int SweepOlderThan(TimeSpan maxAge, DateTimeOffset now) => 0;
    }

    private sealed class UnusedGpxExport : IGpxExportService
    {
        public Task<(Stream? Stream, string FileName)?> ExportAsync(TrackId trackId, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class UnusedBulkExport : IActivityExportService
    {
        public Task<BulkExportManifest> WriteAllAsZipAsync(
            Stream output, string userId, IReadOnlyList<int>? trackIds, CancellationToken ct,
            Action<int, int>? onProgress = null)
            => throw new NotSupportedException();
    }

    /// <summary>The enqueue path makes no query at all — that is the point of deferring ownership.</summary>
    private sealed class FakeQuery : IActivityQueryService
    {
        public Task<List<TrackId>> GetFilteredActivityIdsAsync(
            string? activityType, DateTime? startDate, DateTime? endDate, string? ownerUserId = null,
            IReadOnlyList<string>? activityTypes = null, GridFilterCollection? filters = null,
            IReadOnlyList<string>? ownerUserIds = null, bool ownerMatchNone = false,
            IReadOnlyCollection<TrackId>? restrictIds = null)
            => throw new NotSupportedException();

        public Task<List<Activity>> GetAllActivitiesAsync() => throw new NotSupportedException();
        public Task<Activity?> GetActivityMetadataByIdAsync(int id) => throw new NotSupportedException();
        public Task<Activity?> GetOwnedActivityMetadataByIdAsync(int id) => throw new NotSupportedException();
        public Task<bool> IsActivityOwnedAsync(TrackId trackId) => throw new NotSupportedException();
        public Task<bool> IsActivityVisibleAsync(TrackId trackId) => throw new NotSupportedException();
        public Task<Dictionary<int, ActivityMapSlice>> GetActivitySlicesByIdsAsync(List<int> trackIds, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<List<ActivityEndpoint>> GetActivityEndpointsAsync(List<int> trackIds, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<List<string>> GetActivityTypesAsync() => throw new NotSupportedException();
        public Task<List<int>> GetTrackIdsContainingPointAsync(double lat, double lng, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> GetVisibleActivityCountAsync() => throw new NotSupportedException();
        public Task<Dictionary<TrackId, int>> GetPhotoCountsAsync(IReadOnlyCollection<TrackId> trackIds, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<(List<Activity> Items, int TotalCount)> GetPagedActivitiesAsync(
            string? activityType, DateTime? startDate, DateTime? endDate, string sortColumn, string sortDirection,
            int page, int pageSize, string? ownerUserId = null, IReadOnlyList<string>? activityTypes = null,
            GridFilterCollection? filters = null, IReadOnlyList<string>? ownerUserIds = null, bool ownerMatchNone = false,
            IReadOnlyCollection<TrackId>? restrictIds = null, int? knownCount = null)
            => throw new NotSupportedException();
        public Task<List<Activity>> GetActivitiesWithGpsCoordinatesAsync(int limit = 100) => throw new NotSupportedException();
        public Task<ActivityStatistics> GetStatisticsAsync(
            string? activityType = null, DateTime? startDate = null, DateTime? endDate = null, string? ownerUserId = null)
            => throw new NotSupportedException();
    }
}
