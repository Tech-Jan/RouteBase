using RouteBaseWeb.Models;
using System.Text.Json;
using RouteBaseWeb.Api;
using RouteBaseWeb.Services.TrackEdit;
using RouteBaseWeb.Tests.Services.TrackEdit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace RouteBaseWeb.Tests.Api;

/// <summary>
/// HTTP mapping for the TrackEdit session endpoints (A2 §2): create→get round-trip, foreign-session 404 on
/// every endpoint, and the preflight camelCase shape. Direct controller instantiation over the shared
/// in-memory harness (ReviewApiControllerTests pattern).
/// </summary>
public class TrackEditApiControllerTests
{
    private const string Me = "me";
    private const string Other = "other";

    private static TrackEditApiController ControllerFor(TrackEditTestDb db, string userId) =>
        new(db.ServiceFor(userId), db.SaveServiceFor(userId), db.RevertServiceFor(userId), db.SuggestionServiceFor(userId));

    private static async Task<(string SessionId, SaveRequest Save)> SessionWithCropAsync(
        TrackEditTestDb db, TrackEditApiController controller)
    {
        var create = Assert.IsType<SessionStateResponse>(
            Assert.IsType<OkObjectResult>(await controller.Create(new CreateSessionRequest([101]), default)).Value);
        var appended = Assert.IsType<SessionStateResponse>(
            Assert.IsType<OkObjectResult>(await controller.AppendOps(
                create.SessionId, new AppendOpsRequest(1, [new CropToOp(0, 0, 4)]), default)).Value);
        var claims = appended.SlotCounts.Select(c => new SlotCountClaim(c.SlotId, c.PointCount)).ToList();
        return (create.SessionId, new SaveRequest("replace", claims));
    }

    [Fact]
    public async Task CreateThenGet_RoundtripsState()
    {
        await using var db = await TrackEditTestDb.CreateAsync(Me);
        await db.AddActivityAsync(new TrackId(101), Me);
        await db.SeedRampAsync(new TrackId(101), 5);
        var controller = ControllerFor(db, Me);

        var created = Assert.IsType<OkObjectResult>(await controller.Create(new CreateSessionRequest([101]), default));
        var createState = Assert.IsType<SessionStateResponse>(created.Value);

        var got = Assert.IsType<OkObjectResult>(await controller.Get(createState.SessionId, default));
        var getState = Assert.IsType<SessionStateResponse>(got.Value);

        Assert.Equal(createState.SessionId, getState.SessionId);
        Assert.Equal(1, getState.Cursor);
        Assert.Equal(5, Assert.Single(getState.SlotCounts).PointCount);
    }

    [Fact]
    public async Task ForeignSession_AllEndpoints_Return404()
    {
        await using var db = await TrackEditTestDb.CreateAsync(Me, Other);
        await db.AddActivityAsync(new TrackId(101), Me);
        await db.SeedRampAsync(new TrackId(101), 5);
        var mine = Assert.IsType<SessionStateResponse>(
            Assert.IsType<OkObjectResult>(await ControllerFor(db, Me).Create(new CreateSessionRequest([101]), default)).Value);

        var intruder = ControllerFor(db, Other);
        var id = mine.SessionId;

        Assert.IsType<NotFoundResult>(await intruder.Get(id, default));
        Assert.IsType<NotFoundResult>(await intruder.AppendOps(id, new AppendOpsRequest(1, [new CropToOp(0, 0, 3)]), default));
        Assert.IsType<NotFoundResult>(await intruder.MoveCursor(id, new MoveCursorRequest(0), default));
        Assert.IsType<NotFoundResult>(await intruder.Preflight(id, "replace", default));
        Assert.IsType<NotFoundResult>(await intruder.Discard(id, default));
        Assert.IsType<NotFoundResult>(await intruder.Sources(id, default));
        Assert.IsType<NotFoundResult>(await intruder.Save(id, new SaveRequest("replace", []), default));
    }

    [Fact]
    public async Task Save_ErrorMapping()
    {
        await using var db = await TrackEditTestDb.CreateAsync(Me);
        await db.AddActivityAsync(new TrackId(101), Me);
        await db.SeedRampAsync(new TrackId(101), 10);

        // staleTrack → 409 with {code, activityId}
        var controllerA = ControllerFor(db, Me);
        var (staleId, save) = await SessionWithCropAsync(db, controllerA);
        await db.MutateBlobAsync(new TrackId(101));
        var conflict = Assert.IsType<ConflictObjectResult>(await controllerA.Save(staleId, save, default));
        var staleBody = Assert.IsType<TrackEditErrorBody>(conflict.Value);
        Assert.Equal(TrackEditApiErrors.StaleTrack, staleBody.Code);
        Assert.Equal(101, staleBody.ActivityId);

        // replay error → 422 with {code, opIndex}
        // The stale session still has 101 CHECKED OUT (one open edit per ride), and it can never save — abort it
        // so the ride is free for the next scenario. Exactly what the resume banner's Discard does.
        await db.ServiceFor(Me).DiscardAsync(staleId);
        await db.SeedRampAsync(new TrackId(101), 10);
        var create = Assert.IsType<SessionStateResponse>(
            Assert.IsType<OkObjectResult>(await ControllerFor(db, Me).Create(new CreateSessionRequest([101]), default)).Value);
        var session = await db.Sessions.GetAsync(create.SessionId);
        await db.Sessions.UpdateScriptCursorStampsAsync(create.SessionId,
            new EditScript(1, [new LoadActivityOp(101), new CropToOp(0, 0, 999)]), 2, session!.Stamps, DateTime.UtcNow);
        var unprocessable = Assert.IsType<UnprocessableEntityObjectResult>(
            await ControllerFor(db, Me).Save(create.SessionId, new SaveRequest("replace", [new SlotCountClaim(0, 5)]), default));
        var replayBody = Assert.IsType<TrackEditErrorBody>(unprocessable.Value);
        Assert.Equal(TrackEditErrors.RangeOutOfBounds, replayBody.Code);
        Assert.Equal(1, replayBody.OpIndex);
    }

    [Fact]
    public async Task Revert_ErrorMapping()
    {
        await using var db = await TrackEditTestDb.CreateAsync(Me);
        await db.AddActivityAsync(new TrackId(101), Me);
        await db.SeedRampAsync(new TrackId(101), 10);
        var controller = ControllerFor(db, Me);
        var (id, save) = await SessionWithCropAsync(db, controller);
        int commitId = Assert.IsType<SaveResponse>(
            Assert.IsType<OkObjectResult>(await controller.Save(id, save, default)).Value).CommitId;
        await controller.Revert(commitId, default);

        var conflict = Assert.IsType<ConflictObjectResult>(await controller.Revert(commitId, default));
        Assert.Equal(TrackEditApiErrors.AlreadyReverted, Assert.IsType<TrackEditErrorBody>(conflict.Value).Code);
    }

    [Fact]
    public async Task Preflight_HappyPath_Shape()
    {
        await using var db = await TrackEditTestDb.CreateAsync(Me);
        await db.AddActivityAsync(new TrackId(101), Me, name: "Ride");
        await db.SeedRampAsync(new TrackId(101), 5);
        await db.AddActivityAsync(new TrackId(202), Me, name: "Loop");
        await db.SeedRampAsync(new TrackId(202), 5);
        var controller = ControllerFor(db, Me);
        var id = Assert.IsType<SessionStateResponse>(
            Assert.IsType<OkObjectResult>(await controller.Create(new CreateSessionRequest([101, 202]), default)).Value)
            .SessionId;
        // Merge 202 into 101, then split the product. Under Option B a merge creates a NEW ride and trashes
        // BOTH inputs, so Updated is empty here and the wire shape still carries all three lists.
        await controller.AppendOps(id, new AppendOpsRequest(2,
            [new AppendOp(0, 1, TimeMode.Keep, null), new SplitAtOp(0, 5)]), default);

        var ok = Assert.IsType<OkObjectResult>(await controller.Preflight(id, "replace", default));
        var response = Assert.IsType<SavePreflightResponse>(ok.Value);

        Assert.Empty(response.Updated);
        Assert.Equal(new[] { 202, 101 }, response.Trashed.Select(t => t.TrackId));
        Assert.Equal(2, response.Created.Count);
        Assert.Null(response.BlockedBy);

        // camelCase key pin (wire shape) — ASP.NET serializes with JsonSerializerDefaults.Web.
        var json = JsonSerializer.Serialize(response, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"updated\"", json, StringComparison.Ordinal);
        Assert.Contains("\"trashed\"", json, StringComparison.Ordinal);
        Assert.Contains("\"created\"", json, StringComparison.Ordinal);
        Assert.Contains("\"blockedBy\"", json, StringComparison.Ordinal);
    }

    // ---- suggestions / search (A4 §3.1) ----

    [Fact]
    public async Task Suggestions_ForeignSession_Returns404()
    {
        await using var db = await TrackEditTestDb.CreateAsync(Me, Other);
        await db.AddActivityAsync(new TrackId(101), Me);
        await db.SeedRampAsync(new TrackId(101), 5);
        var mine = Assert.IsType<SessionStateResponse>(
            Assert.IsType<OkObjectResult>(await ControllerFor(db, Me).Create(new CreateSessionRequest([101]), default)).Value);

        var intruder = ControllerFor(db, Other);
        Assert.IsType<NotFoundResult>(await intruder.TrackSuggestions(mine.SessionId, 47.0, 8.0, null, default));
        Assert.IsType<NotFoundResult>(await intruder.TrackSearch(mine.SessionId, "ride", null, default));
    }

    [Fact]
    public async Task Search_NoParams_Rejected()
    {
        await using var db = await TrackEditTestDb.CreateAsync(Me);
        await db.AddActivityAsync(new TrackId(101), Me);
        await db.SeedRampAsync(new TrackId(101), 5);
        var controller = ControllerFor(db, Me);
        var id = Assert.IsType<SessionStateResponse>(
            Assert.IsType<OkObjectResult>(await controller.Create(new CreateSessionRequest([101]), default)).Value).SessionId;

        Assert.IsType<BadRequestObjectResult>(await controller.TrackSearch(id, null, null, default));
    }

    [Fact]
    public async Task Suggestions_WireShape_CamelCase()
    {
        await using var db = await TrackEditTestDb.CreateAsync(Me);
        await db.AddActivityAsync(new TrackId(101), Me);
        await db.SeedRampAsync(new TrackId(101), 5);
        await db.AddSuggestibleAsync(new TrackId(202), Me, name: "Loop", startLat: 47.0 + 200 / 111_320.0, startLng: 8.0);
        var controller = ControllerFor(db, Me);
        var id = Assert.IsType<SessionStateResponse>(
            Assert.IsType<OkObjectResult>(await controller.Create(new CreateSessionRequest([101]), default)).Value).SessionId;

        var ok = Assert.IsType<OkObjectResult>(await controller.TrackSuggestions(id, 47.0, 8.0, null, default));
        var response = Assert.IsType<TrackSuggestionsResponse>(ok.Value);
        Assert.NotEmpty(response.Suggestions);

        var json = JsonSerializer.Serialize(response, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"startGapMeters\"", json, StringComparison.Ordinal);
        Assert.Contains("\"alreadyLoaded\"", json, StringComparison.Ordinal);
        Assert.Contains("\"nearEnd\"", json, StringComparison.Ordinal);
        Assert.Contains("\"sameDay\"", json, StringComparison.Ordinal);
    }
}
