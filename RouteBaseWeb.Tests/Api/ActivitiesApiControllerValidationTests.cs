using RouteBaseWeb.Api;
using RouteBaseWeb.Models;
using RouteBaseWeb.Models.GridFilter;
using RouteBaseWeb.Models.Requests;
using RouteBaseWeb.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace RouteBaseWeb.Tests.Api;

/// <summary>
/// Pins the gps-data resource caps on <see cref="ActivitiesApiController"/> (the activities twin of
/// the segments SA-05 rule): at most 1000 ids per request, and full resolution (maxPointsPerTrack ≤ 0)
/// only for small id sets — the overview's chunked loader never sends more than 150 ids per request,
/// so one giant full-res request is always hostile, not a legitimate client. Also pins the PATCH
/// name/type length caps (200/50 — the same bounds RecordingRequestValidator enforces on the
/// recording write path; without them a multi-MB name reaches the DB and every projection).
/// </summary>
public class ActivitiesApiControllerValidationTests
{
    private sealed class RecordingGpsService : IActivityGpsService
    {
        public int LastMaxPoints = int.MinValue;
        public bool Called;

        public Task<List<GpsData>> GetGpsDataForTrackAsync(TrackId trackId, CancellationToken ct = default) =>
            Task.FromResult(new List<GpsData>());
        public Task<List<GpsData>> GetOwnedGpsDataForTrackAsync(TrackId trackId, CancellationToken ct = default) =>
            Task.FromResult(new List<GpsData>());
        public Task<List<GpsCoordinate>> GetTrackCoordinatesAsync(TrackId trackId, CancellationToken ct = default) =>
            Task.FromResult(new List<GpsCoordinate>());
        public Task<List<GpsCoordinate>> GetTrackCoordinatesAsync(TrackId trackId, int maxPoints, CancellationToken ct = default) =>
            Task.FromResult(new List<GpsCoordinate>());
        public Task<List<GpsCoordinate>> GetOwnedTrackCoordinatesAsync(TrackId trackId, int maxPoints, CancellationToken ct = default) =>
            Task.FromResult(new List<GpsCoordinate>());

        public Task<Dictionary<int, List<GpsCoordinate>>> GetBulkTrackCoordinatesAsync(
            List<int> trackIds, int maxPointsPerTrack, CancellationToken ct = default)
        {
            Called = true;
            LastMaxPoints = maxPointsPerTrack;
            return Task.FromResult(new Dictionary<int, List<GpsCoordinate>>());
        }
    }

    /// <summary>Only the two members the gps-data path touches; everything else must stay unreached.</summary>
    private sealed class StubQueryService : IActivityQueryService
    {
        public Task<Dictionary<int, ActivityMapSlice>> GetActivitySlicesByIdsAsync(List<int> trackIds, CancellationToken ct = default) =>
            Task.FromResult(new Dictionary<int, ActivityMapSlice>());
        public Task<List<ActivityEndpoint>> GetActivityEndpointsAsync(List<int> trackIds, CancellationToken ct = default) =>
            Task.FromResult(new List<ActivityEndpoint>());

        public Task<List<Activity>> GetAllActivitiesAsync() => throw new NotImplementedException();
        public Task<Activity?> GetActivityMetadataByIdAsync(int id) => throw new NotImplementedException();
        public Task<Activity?> GetOwnedActivityMetadataByIdAsync(int id) => throw new NotImplementedException();
        public Task<bool> IsActivityOwnedAsync(TrackId trackId) => throw new NotImplementedException();
        public Task<bool> IsActivityVisibleAsync(TrackId trackId) => throw new NotImplementedException();
        public Task<List<string>> GetActivityTypesAsync() => throw new NotImplementedException();
        public Task<List<int>> GetTrackIdsContainingPointAsync(double lat, double lng, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> GetVisibleActivityCountAsync() => throw new NotImplementedException();
        public Task<Dictionary<TrackId, int>> GetPhotoCountsAsync(IReadOnlyCollection<TrackId> trackIds, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(List<Activity> Items, int TotalCount)> GetPagedActivitiesAsync(
            string? activityType, DateTime? startDate, DateTime? endDate, string sortColumn, string sortDirection,
            int page, int pageSize, string? ownerUserId = null, IReadOnlyList<string>? activityTypes = null,
            GridFilterCollection? filters = null, IReadOnlyList<string>? ownerUserIds = null, bool ownerMatchNone = false, IReadOnlyCollection<TrackId>? restrictIds = null, int? knownCount = null) =>
            throw new NotImplementedException();
        public Task<List<TrackId>> GetFilteredActivityIdsAsync(
            string? activityType, DateTime? startDate, DateTime? endDate, string? ownerUserId = null,
            IReadOnlyList<string>? activityTypes = null, GridFilterCollection? filters = null,
            IReadOnlyList<string>? ownerUserIds = null, bool ownerMatchNone = false, IReadOnlyCollection<TrackId>? restrictIds = null) =>
            throw new NotImplementedException();
        public Task<List<Activity>> GetActivitiesWithGpsCoordinatesAsync(int limit = 100) => throw new NotImplementedException();
        public Task<ActivityStatistics> GetStatisticsAsync(
            string? activityType = null, DateTime? startDate = null, DateTime? endDate = null, string? ownerUserId = null) =>
            throw new NotImplementedException();
    }

    private sealed class RecordingCommandService : IActivityCommandService
    {
        public (string? Name, string? Type, string? Visibility)? LastUpdate;
        public (IReadOnlyList<TrackId> Ids, string? Type, string? Visibility)? LastBulkUpdate;
        public IReadOnlyList<TrackId>? LastBulkDelete;

        public Task<bool> UpdateActivityAsync(TrackId trackId, string? name, string? activityType, string? visibility)
        {
            LastUpdate = (name, activityType, visibility);
            return Task.FromResult(true);
        }
        public Task<int> UpdateManyAsync(IReadOnlyList<TrackId> trackIds, string? activityType, string? visibility)
        {
            LastBulkUpdate = (trackIds, activityType, visibility);
            return Task.FromResult(trackIds.Count);
        }
        public Task<bool> DeleteActivityAsync(TrackId trackId) => throw new NotImplementedException();
        public Task<int> DeleteManyAsync(IReadOnlyList<TrackId> trackIds)
        {
            LastBulkDelete = trackIds;
            return Task.FromResult(trackIds.Count);
        }
        public Task<bool> DeleteActivityCoreAsync(TrackId trackId, string ownerId, bool requireTrashed = false) => throw new NotImplementedException();
    }

    private static ActivitiesApiController NewController(
        IActivityGpsService? gps = null, IActivityCommandService? command = null) =>
        new(new StubQueryService(), gps ?? new RecordingGpsService(), command ?? new RecordingCommandService(),
            null!, null!, null!, null!, null!, null!, null!, NullLogger<ActivitiesApiController>.Instance, null!);

    private static GpsDataRequest GpsRequest(int idCount, int maxPoints) => new()
    {
        ActivityIds = Enumerable.Range(1, idCount).ToList(),
        MaxPointsPerTrack = maxPoints,
    };

    // ── gps-data caps ────────────────────────────────────────────────────────

    [Fact]
    public async Task GpsData_MoreThan1000Ids_IsRejected()
    {
        var gps = new RecordingGpsService();
        var result = await NewController(gps).GetGpsData(GpsRequest(1001, 500), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.False(gps.Called);
    }

    [Fact]
    public async Task GpsData_FullRes_MoreThan300Ids_IsRejected()
    {
        var gps = new RecordingGpsService();
        var result = await NewController(gps).GetGpsData(GpsRequest(301, 0), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.False(gps.Called);
    }

    [Fact]
    public async Task GpsData_FullRes_WithinCap_PassesZeroThrough()
    {
        // The zoom ≥ 11 quality upgrade legitimately requests full resolution (0) for the visible
        // tracks, chunked at 150 ids — that path must keep working.
        var gps = new RecordingGpsService();
        var result = await NewController(gps).GetGpsData(GpsRequest(150, 0), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(0, gps.LastMaxPoints);
    }

    [Fact]
    public async Task GpsData_PositiveBudget_StillClamped()
    {
        var gps = new RecordingGpsService();
        await NewController(gps).GetGpsData(GpsRequest(3, 99999), CancellationToken.None);
        Assert.Equal(50000, gps.LastMaxPoints);

        await NewController(gps).GetGpsData(GpsRequest(3, 5), CancellationToken.None);
        Assert.Equal(10, gps.LastMaxPoints);
    }

    // ── PATCH length caps ────────────────────────────────────────────────────

    [Fact]
    public async Task Patch_NameOver200Chars_IsRejected()
    {
        var command = new RecordingCommandService();
        var result = await NewController(command: command).UpdateActivity(
            1, new UpdateActivityRequest { Name = new string('x', 201) });

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Null(command.LastUpdate);
    }

    [Fact]
    public async Task Patch_ActivityTypeOver50Chars_IsRejected()
    {
        var command = new RecordingCommandService();
        var result = await NewController(command: command).UpdateActivity(
            1, new UpdateActivityRequest { ActivityType = new string('x', 51) });

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Null(command.LastUpdate);
    }

    [Fact]
    public async Task Patch_BoundaryLengths_Pass()
    {
        var command = new RecordingCommandService();
        var result = await NewController(command: command).UpdateActivity(
            1, new UpdateActivityRequest { Name = new string('n', 200), ActivityType = new string('t', 50) });

        Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(command.LastUpdate);
    }

    // ── bulk/update + bulk/delete caps + validation ──────────────────────────

    private static List<int> Ids(int n) => Enumerable.Range(1, n).ToList();

    [Fact]
    public async Task BulkUpdate_BadVisibility_IsRejected()
    {
        var command = new RecordingCommandService();
        var result = await NewController(command: command).BulkUpdate(
            new BulkUpdateActivitiesRequest(Ids(3), null, "bogus"));

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Null(command.LastBulkUpdate);
    }

    [Fact]
    public async Task BulkUpdate_NoFields_IsRejected()
    {
        var command = new RecordingCommandService();
        var result = await NewController(command: command).BulkUpdate(
            new BulkUpdateActivitiesRequest(Ids(3), null, null));

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Null(command.LastBulkUpdate);
    }

    [Fact]
    public async Task BulkUpdate_EmptyIds_IsRejected()
    {
        var command = new RecordingCommandService();
        var result = await NewController(command: command).BulkUpdate(
            new BulkUpdateActivitiesRequest(new List<int>(), null, "public"));

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Null(command.LastBulkUpdate);
    }

    [Fact]
    public async Task BulkUpdate_OverCap_IsRejected()
    {
        var command = new RecordingCommandService();
        var result = await NewController(command: command).BulkUpdate(
            new BulkUpdateActivitiesRequest(Ids(10001), null, "public"));

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Null(command.LastBulkUpdate);
    }

    [Fact]
    public async Task BulkUpdate_Valid_ForwardsDistinctIdsAndLowercasedVisibility()
    {
        var command = new RecordingCommandService();
        var result = await NewController(command: command).BulkUpdate(
            new BulkUpdateActivitiesRequest(new List<int> { 1, 1, 2 }, null, "Public"));

        Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(command.LastBulkUpdate);
        Assert.Equal(new[] { new TrackId(1), new TrackId(2) }, command.LastBulkUpdate!.Value.Ids); // deduped
        Assert.Equal("public", command.LastBulkUpdate!.Value.Visibility); // lowercased
    }

    [Fact]
    public async Task BulkDelete_EmptyIds_IsRejected()
    {
        var command = new RecordingCommandService();
        var result = await NewController(command: command).BulkDelete(
            new BulkDeleteActivitiesRequest(new List<int>()));

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Null(command.LastBulkDelete);
    }

    [Fact]
    public async Task BulkDelete_OverCap_IsRejected()
    {
        var command = new RecordingCommandService();
        var result = await NewController(command: command).BulkDelete(
            new BulkDeleteActivitiesRequest(Ids(10001)));

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Null(command.LastBulkDelete);
    }

    [Fact]
    public async Task BulkDelete_Valid_ForwardsToService()
    {
        var command = new RecordingCommandService();
        var result = await NewController(command: command).BulkDelete(
            new BulkDeleteActivitiesRequest(new List<int> { 5, 6 }));

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(new[] { new TrackId(5), new TrackId(6) }, command.LastBulkDelete);
    }
}
