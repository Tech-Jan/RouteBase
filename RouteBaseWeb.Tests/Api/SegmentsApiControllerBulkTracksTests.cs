using RouteBaseWeb.Api;
using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Requests;
using RouteBaseWeb.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace RouteBaseWeb.Tests.Api;

/// <summary>
/// SA-05 pin: <see cref="SegmentsApiController.GetBulkActivityTracks"/> treats a
/// <c>MaxPointsPerTrack</c> of 0 (or negative) as the 2000-point safety cap, NOT full resolution —
/// otherwise one request could force a full-res decode of up to 1000 tracks (hundreds of MB / minutes
/// of CPU). The clamp lives entirely in the controller, so a recording fake for the GPS service
/// captures the budget it was handed.
/// </summary>
public class SegmentsApiControllerBulkTracksTests
{
    private sealed class RecordingGpsService : IActivityGpsService
    {
        public int LastMaxPoints = int.MinValue;

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
            LastMaxPoints = maxPointsPerTrack;
            return Task.FromResult(new Dictionary<int, List<GpsCoordinate>>());
        }
    }

    private static SegmentsApiController NewController(IActivityGpsService gps) =>
        new(null!, null!, null!, gps, null!, null!, null!, null!, null!, null!, null!);

    private static async Task<int> BudgetHandedTo(int requested)
    {
        var gps = new RecordingGpsService();
        var req = new BulkTrackRequest { TrackIds = new List<int> { 1, 2, 3 }, MaxPointsPerTrack = requested };
        var result = await NewController(gps).GetBulkActivityTracks(req, CancellationToken.None);
        Assert.IsType<OkObjectResult>(result);
        return gps.LastMaxPoints;
    }

    [Fact]
    public async Task BulkTracks_ZeroBudget_ClampsTo2000() =>
        Assert.Equal(2000, await BudgetHandedTo(0));

    [Fact]
    public async Task BulkTracks_OverCap_ClampsTo2000() =>
        Assert.Equal(2000, await BudgetHandedTo(9999));

    [Fact]
    public async Task BulkTracks_NormalBudget_PassesThrough() =>
        Assert.Equal(200, await BudgetHandedTo(200));
}
