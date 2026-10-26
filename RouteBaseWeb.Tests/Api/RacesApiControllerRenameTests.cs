using RouteBaseWeb.Api;
using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Requests;
using RouteBaseWeb.Services.Races;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace RouteBaseWeb.Tests.Api;

/// <summary>
/// Pins the rename endpoint's RESPONSE BODY, which is the whole reason it has one.
///
/// A blank name means "the ride's own name again", and only the server knows what that is — so the row
/// it answers with carries the name it RESOLVED, not the one that was sent. The browser writes that
/// straight back into the name box (race-riders-panel.ts), so a body that lost the name would leave the
/// user staring at an empty field after a reset and no error to explain it.
///
/// The service half is covered in RaceCrudTests; this is the mapping between it and the wire.
/// </summary>
public class RacesApiControllerRenameTests
{
    private sealed class StubParticipantCrud : IRaceParticipantCrud
    {
        private readonly RaceWriteResult _result;
        private readonly RaceParticipant? _participant;

        public StubParticipantCrud(RaceWriteResult result, RaceParticipant? participant)
        {
            _result = result;
            _participant = participant;
        }

        public Task<(RaceWriteResult Result, RaceParticipant? Participant)> RenameParticipantAsync(
            RaceId raceId, ParticipantId participantId, string? displayName) =>
            Task.FromResult((_result, _participant));

        public Task<(RaceWriteResult Result, RaceParticipant? Participant)> AddRealParticipantAsync(
            RaceId raceId, TrackId trackId, string? displayName) => throw new NotImplementedException();
        public Task<RaceWriteResult> RemoveParticipantAsync(RaceId raceId, ParticipantId participantId) =>
            throw new NotImplementedException();
        public Task<(RaceWriteResult Result, int Updated)> SetWaveOffsetsAsync(
            RaceId raceId, IReadOnlyDictionary<string, double?> offsetsByWave) => throw new NotImplementedException();
        public Task<(RaceWriteResult Result, int Removed)> ClearFieldAsync(RaceId raceId) =>
            throw new NotImplementedException();
        public Task<(RaceWriteResult Result, int Painted)> SetParticipantColorsAsync(
            RaceId raceId, IReadOnlyList<ParticipantId> participantIds, string? color) => throw new NotImplementedException();
        public Task<(RaceWriteResult Result, int Updated)> SetParticipantEmphasisAsync(
            RaceId raceId, IReadOnlyList<ParticipantId> participantIds, bool emphasised) => throw new NotImplementedException();
        public Task<(RaceWriteResult Result, int Updated)> SetParticipantCalloutOffAsync(
            RaceId raceId, IReadOnlyList<ParticipantId> participantIds, bool calloutOff) => throw new NotImplementedException();
    }

    private static RacesApiController NewController(RaceWriteResult result, RaceParticipant? participant) =>
        new(null!, null!, new StubParticipantCrud(result, participant), null!, null!, null!, null!, null!);

    private static RaceParticipant Rider(string? displayName) => new()
    {
        ParticipantId = new ParticipantId(7),
        RaceId = new RaceId(1),
        Kind = RaceParticipantKind.Real,
        TrackId = new TrackId(3),
        DisplayName = displayName,
        Bib = "214",
    };

    private static RenameParticipantRequest Request(string? displayName) => new()
    {
        ParticipantId = 7, DisplayName = displayName,
    };

    [Fact]
    public async Task RenameParticipant_AnswersWithTheResolvedName_NotTheOneThatWasSent()
    {
        // Sent blank; the service resolved it back to the ride's own name. The BODY is how the browser
        // learns that, so it is the thing under test — not the 200.
        var controller = NewController(RaceWriteResult.Ok, Rider("Morning ride"));

        var result = await controller.RenameParticipant(1, Request(""));

        var row = Assert.IsType<RaceParticipantRowDto>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal("Morning ride", row.Name);
        Assert.Equal(7, row.ParticipantId);
        Assert.Equal(RaceParticipantKind.Real, row.Kind);
        Assert.Equal("214", row.Bib);
        Assert.Equal(3, row.TrackId);
    }

    [Fact]
    public async Task RenameParticipant_ASimulatedRiderWithNoRide_AnswersWithANullName()
    {
        // Nothing to fall back to, so the name really is absent — the box goes empty and the board shows
        // his bib. A body that substituted "" or the bib here would be inventing a name the server never
        // stored.
        var controller = NewController(RaceWriteResult.Ok, Rider(null));

        var result = await controller.RenameParticipant(1, Request(""));

        var row = Assert.IsType<RaceParticipantRowDto>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Null(row.Name);
    }

    [Fact]
    public async Task RenameParticipant_TooLong_Is400_WithTheServersOwnSentence()
    {
        // The panel puts this straight in the status line, so it has to be a sentence rather than a code.
        var controller = NewController(RaceWriteResult.InvalidDisplayName, null);

        var result = await controller.RenameParticipant(1, Request(new string('x', 256)));

        var error = Assert.IsType<ApiError>(Assert.IsType<BadRequestObjectResult>(result).Value);
        Assert.Contains("255", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RenameParticipant_NotFound_Is404_AndCarriesNoRow()
    {
        var controller = NewController(RaceWriteResult.RaceNotFound, null);

        var result = await controller.RenameParticipant(1, Request("Marco"));

        Assert.IsType<NotFoundObjectResult>(result);
    }
}
