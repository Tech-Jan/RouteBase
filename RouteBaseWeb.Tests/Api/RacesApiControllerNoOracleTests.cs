using RouteBaseWeb.Api;
using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Requests;
using RouteBaseWeb.Services.Races;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace RouteBaseWeb.Tests.Api;

/// <summary>
/// The race write surface answers 404 — never 403 — for a ride the caller cannot see, so the response
/// cannot be used to probe whether someone else's activity exists. Both 404s carry the canonical
/// ApiError body (D31/D32), which is also what the actions now declare.
/// </summary>
public class RacesApiControllerNoOracleTests
{
    private sealed class StubRaceCrud(RaceWriteResult result) : IRaceCrud
    {
        public Task<RaceWriteResult> SetReferenceEffortAsync(RaceId raceId, TrackId? referenceTrackId) =>
            Task.FromResult(result);

        public Task<RaceWriteResult> DeleteRaceAsync(RaceId raceId) => Task.FromResult(result);

        public Task<Race?> CreateRaceAsync(TrackId courseTrackId, string raceName, DateTime? raceDate, string visibility) =>
            throw new NotImplementedException();
        public Task<List<RaceSummaryDto>> GetRacesAsync() => throw new NotImplementedException();
        public Task<Race?> GetRaceAsync(RaceId raceId) => throw new NotImplementedException();
        public Task<Race?> DuplicateRaceAsync(RaceId sourceRaceId) => throw new NotImplementedException();
        public Task<RaceWriteResult> SetEditPolicyAsync(RaceId raceId, string policy) => throw new NotImplementedException();
    }

    private static RacesApiController ControllerFor(RaceWriteResult result) =>
        new(new StubRaceCrud(result), null!, null!, null!, null!, null!, null!, null!);

    [Fact]
    public async Task InvisibleActivity_Answers404_NotForbid()
    {
        var result = await ControllerFor(RaceWriteResult.ActivityNotVisible)
            .UpdateRace(1, new UpdateRaceRequest { ReferenceTrackId = 7 });

        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        var body = Assert.IsType<ApiError>(notFound.Value);
        Assert.Equal(ApiErrorCodes.NotFound, body.Code);
    }

    [Fact]
    public async Task MissingRace_AndInvisibleActivity_AreIndistinguishable()
    {
        // The whole point of the no-oracle rule: the two cases must not be tellable apart — and NOT just by
        // status (MED-1). Both used to answer 404 but with DIFFERENT bodies ("Race not found." vs "Activity
        // not found."), so a caller could still distinguish "race missing" from "course not visible". Assert
        // the full ApiError body is identical (record value-equality covers Code + Message).
        var missing = Assert.IsType<NotFoundObjectResult>(
            await ControllerFor(RaceWriteResult.RaceNotFound).DeleteRace(1));
        var invisible = Assert.IsType<NotFoundObjectResult>(
            await ControllerFor(RaceWriteResult.ActivityNotVisible).DeleteRace(1));

        var missingBody = Assert.IsType<ApiError>(missing.Value);
        var invisibleBody = Assert.IsType<ApiError>(invisible.Value);
        Assert.Equal(missingBody, invisibleBody); // same status AND same body — no oracle
    }
}
