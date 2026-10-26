using RouteBaseWeb.Api;
using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Requests;
using RouteBaseWeb.Services.Races;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace RouteBaseWeb.Tests.Api;

/// <summary>
/// Pins CreateRace's visibility vocabulary: private | friends | public, normalized like the PATCH
/// /api/activities path (trim + lowercase). Anything else must be a 400 — RaceAccess gates on exact
/// lowercase strings, so an unvalidated value would be stored verbatim and silently behave as
/// owner-only (the user believes they shared the race; their friends never see it).
/// </summary>
public class RacesApiControllerCreateTests
{
    private sealed class RecordingRaceCrud : IRaceCrud
    {
        public string? LastVisibility;

        public Task<Race?> CreateRaceAsync(TrackId courseTrackId, string raceName, DateTime? raceDate, string visibility)
        {
            LastVisibility = visibility;
            return Task.FromResult<Race?>(null); // null → controller answers Forbid; fine for these pins
        }

        public Task<List<RaceSummaryDto>> GetRacesAsync() => throw new NotImplementedException();
        public Task<Race?> GetRaceAsync(RaceId raceId) => throw new NotImplementedException();
        public Task<Race?> DuplicateRaceAsync(RaceId sourceRaceId) => throw new NotImplementedException();
        public Task<RaceWriteResult> SetReferenceEffortAsync(RaceId raceId, TrackId? referenceTrackId) => throw new NotImplementedException();
        public Task<RaceWriteResult> SetEditPolicyAsync(RaceId raceId, string policy) => throw new NotImplementedException();
        public Task<RaceWriteResult> DeleteRaceAsync(RaceId raceId) => throw new NotImplementedException();
    }

    private static (RacesApiController Controller, RecordingRaceCrud Crud) NewController()
    {
        var crud = new RecordingRaceCrud();
        return (new RacesApiController(crud, null!, null!, null!, null!, null!, null!, null!), crud);
    }

    private static CreateRaceRequest Request(string? visibility) => new()
    {
        CourseTrackId = 1, RaceName = "Test race", Visibility = visibility,
    };

    [Fact]
    public async Task CreateRace_UnknownVisibility_IsRejected()
    {
        var (controller, crud) = NewController();
        var result = await controller.CreateRace(Request("everyone"));

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Null(crud.LastVisibility);
    }

    [Fact]
    public async Task CreateRace_Visibility_IsNormalizedLikeActivityPatch()
    {
        var (controller, crud) = NewController();
        await controller.CreateRace(Request(" Friends "));

        Assert.Equal("friends", crud.LastVisibility);
    }

    [Fact]
    public async Task CreateRace_NullVisibility_DefaultsToFriends()
    {
        var (controller, crud) = NewController();
        await controller.CreateRace(Request(null));

        Assert.Equal("friends", crud.LastVisibility);
    }
}
