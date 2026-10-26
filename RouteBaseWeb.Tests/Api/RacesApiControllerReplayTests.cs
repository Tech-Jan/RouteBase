using RouteBaseWeb.Api;
using RouteBaseWeb.Services.Races;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Xunit;
using RouteBaseWeb.Models;

namespace RouteBaseWeb.Tests.Api;

/// <summary>
/// GET /api/RacesApi/{id}/replay used to re-send the full payload (~0.5-1 MB) on every open. It now
/// hashes the serialized bytes into an ETag so an unchanged race gets a 304 instead.
/// </summary>
public class RacesApiControllerReplayTests
{
    private sealed class FakeReplay : IRaceReplayService
    {
        public RaceReplayPayload? Payload;
        public Task<RaceReplayPayload?> GetReplayAsync(RaceId raceId, CancellationToken ct = default) =>
            Task.FromResult(Payload);
    }

    private static RaceReplayPayload SamplePayload(string raceName = "Test Race") => new()
    {
        RaceId = 1,
        RaceName = raceName,
        Course = new RaceCourseDto
        {
            Lat = [1.0], Lng = [2.0], Elevation = [3.0], GradePercent = [0.0],
            StepMeters = 10, TotalMeters = 10,
        },
        Checkpoints = [],
        Participants = [],
    };

    private static RacesApiController NewController(FakeReplay replay)
    {
        var controller = new RacesApiController(null!, null!, null!, replay, null!, null!, null!, Options.Create(new JsonOptions()));
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }

    [Fact]
    public async Task GetReplay_MissingRace_IsNotFound()
    {
        var controller = NewController(new FakeReplay { Payload = null });
        var result = await controller.GetReplay(1, CancellationToken.None);
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task GetReplay_FirstRequest_SetsEtagAndCacheControlAndReturnsBody()
    {
        var controller = NewController(new FakeReplay { Payload = SamplePayload() });
        var result = await controller.GetReplay(1, CancellationToken.None);

        Assert.IsType<FileContentResult>(result);
        Assert.True(controller.Response.Headers.ContainsKey("ETag"));
        Assert.Equal("private, no-cache", controller.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task GetReplay_MatchingIfNoneMatch_Returns304WithNoBody()
    {
        var replay = new FakeReplay { Payload = SamplePayload() };
        var first = NewController(replay);
        await first.GetReplay(1, CancellationToken.None);
        var etag = first.Response.Headers.ETag.ToString();

        // A fresh controller/HttpContext — real requests get their own, this call just re-sends what
        // the browser cached from the first response's ETag.
        var second = NewController(replay);
        second.Request.Headers.IfNoneMatch = etag;
        var result = await second.GetReplay(1, CancellationToken.None);

        var status = Assert.IsType<StatusCodeResult>(result);
        Assert.Equal(StatusCodes.Status304NotModified, status.StatusCode);
    }

    [Fact]
    public async Task GetReplay_ChangedPayload_GetsADifferentEtag()
    {
        var controllerA = NewController(new FakeReplay { Payload = SamplePayload("Original Name") });
        await controllerA.GetReplay(1, CancellationToken.None);
        var etagA = controllerA.Response.Headers.ETag.ToString();

        var controllerB = NewController(new FakeReplay { Payload = SamplePayload("Renamed") });
        await controllerB.GetReplay(1, CancellationToken.None);
        var etagB = controllerB.Response.Headers.ETag.ToString();

        Assert.NotEqual(etagA, etagB, StringComparer.Ordinal);
    }
}
