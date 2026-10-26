using RouteBaseWeb.Helpers;
using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Requests;
using RouteBaseWeb.Services.Races;
using Microsoft.AspNetCore.Mvc;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RouteBaseWeb.Api;

/// <summary>
/// The race slice's HTTP surface. Thin: authorization lives in IRaceAccess (the slice's single seam) and
/// the domain rules in the CRUD services, so this only maps results to status codes.
/// </summary>
[ApiController]
[Route("api/RacesApi")]
public class RacesApiController : ControllerBase
{
    private readonly IRaceCrud _races;
    private readonly IRaceCheckpointCrud _checkpoints;
    private readonly IRaceParticipantCrud _participants;
    private readonly IRaceReplayService _replay;
    private readonly IRaceImportService _import;
    // The slice's single authorization seam, used directly by the template and profile endpoints: both
    // need the race's CHECKPOINTS as well as the permission, and EditableRaceAsync returns exactly that.
    private readonly IRaceAccess _access;
    private readonly IRaceImportProfileStore _profiles;
    private readonly Microsoft.Extensions.Options.IOptions<JsonOptions> _jsonOptions;

    public RacesApiController(
        IRaceCrud races,
        IRaceCheckpointCrud checkpoints,
        IRaceParticipantCrud participants,
        IRaceReplayService replay,
        IRaceImportService import,
        IRaceAccess access,
        IRaceImportProfileStore profiles,
        Microsoft.Extensions.Options.IOptions<JsonOptions> jsonOptions)
    {
        _races = races;
        _checkpoints = checkpoints;
        _participants = participants;
        _replay = replay;
        _import = import;
        _access = access;
        _profiles = profiles;
        _jsonOptions = jsonOptions;
    }

    [HttpGet]
    [ProducesResponseType<List<RaceSummaryDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetRaces() =>
        Ok(await _races.GetRacesAsync());   // already a materialized List<RaceSummaryDto> — never an iterator

    /// <summary>
    /// Everything needed to replay the race: course grid, checkpoints, and the whole field. Re-fetched in
    /// full (~0.5-1 MB) on every open with no conditional-GET support. The ETag hashes the actual
    /// serialized bytes rather than a stored version/updated-at column: the payload's inputs span Race,
    /// Race_Checkpoint, Race_Participant, Race_Split_Times, the course activity's GPS, and (per real
    /// participant) their own activity's GPS — a stored version would need every one of those mutation
    /// paths to remember to bump it, and a missed one means a silently stale replay. Hashing the bytes
    /// is correct by construction; Cache-Control: no-cache makes the browser always revalidate (send
    /// If-None-Match) rather than serve from cache unconditionally.
    /// </summary>
    [HttpGet("{raceId:int}/replay")]
    [Produces("application/json")] // manual ETag + File(body); schema declared via metadata (D14 closure)
    [ProducesResponseType<RaceReplayPayload>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetReplay(int raceId, CancellationToken ct)
    {
        var payload = await _replay.GetReplayAsync(new RaceId(raceId), ct);
        if (payload == null) return NotFound();

        // Same options Ok(payload) would use (MVC's configured JsonSerializerOptions) — needed here
        // because the ETag path serializes by hand, before deciding whether to send the body at all.
        var body = JsonSerializer.SerializeToUtf8Bytes(payload, _jsonOptions.Value.JsonSerializerOptions);
        var etag = $"\"{Convert.ToHexString(SHA256.HashData(body))}\"";

        Response.Headers.CacheControl = "private, no-cache";
        Response.Headers.ETag = etag;

        if (Request.Headers.IfNoneMatch == etag)
            return StatusCode(StatusCodes.Status304NotModified);

        return File(body, "application/json");
    }

    [HttpPost]
    [ProducesResponseType<RaceSummaryDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateRace([FromBody] CreateRaceRequest request)
    {
        // Normalize + validate like PATCH /api/activities: RaceAccess gates on exact lowercase strings,
        // so an unvalidated value would be stored verbatim and silently behave as owner-only.
        var visibility = request.Visibility is null
            ? Visibilities.Default
            : Visibilities.Normalize(request.Visibility.Trim());
        if (visibility is null)
            return BadRequest(ApiError.BadRequest("Visibility must be 'private', 'friends', or 'public'"));

        var race = await _races.CreateRaceAsync(
            new TrackId(request.CourseTrackId), request.RaceName, request.RaceDate, visibility);
        // Null means the course activity isn't OWNED — you may race over a friend's ride, but you may
        // not define a course from it.
        return race == null ? Forbid() : Ok(Summary(race));
    }

    /// <summary>"Save as": deep-copy a visible race into a new one owned by the caller — same course,
    /// checkpoints, field and imported results, named "&lt;source&gt; (copy)".</summary>
    [HttpPost("{raceId:int}/duplicate")]
    [RejectCrossSiteBrowserRequests] // route-only POST — cross-site form/fetch-able without preflight (PI-01)
    [ProducesResponseType<RaceSummaryDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> DuplicateRace(int raceId)
    {
        var race = await _races.DuplicateRaceAsync(new RaceId(raceId));
        return race == null ? NotFound() : Ok(Summary(race));
    }

    /// <summary>Point the race at the real effort its cost curve is fitted from.</summary>
    [HttpPut("{raceId:int}")]
    [ProducesResponseType(StatusCodes.Status200OK)] // bodyless Ok() on success (RaceWriteResult.Ok)
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiError>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiError>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateRace(int raceId, [FromBody] UpdateRaceRequest request) =>
        Map(await _races.SetReferenceEffortAsync(new RaceId(raceId), request.ReferenceTrackId is { } rt ? new TrackId(rt) : (TrackId?)null));

    /// <summary>Set who may edit this race (owner/friends/public). Owner-only.</summary>
    [HttpPut("{raceId:int}/edit-policy")]
    [ProducesResponseType(StatusCodes.Status200OK)] // bodyless Ok() on success (RaceWriteResult.Ok)
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiError>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiError>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetEditPolicy(int raceId, [FromBody] SetEditPolicyRequest request) =>
        Map(await _races.SetEditPolicyAsync(new RaceId(raceId), request.Policy));

    [HttpDelete("{raceId:int}")]
    [ProducesResponseType(StatusCodes.Status200OK)] // bodyless Ok() on success (RaceWriteResult.Ok)
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiError>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiError>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteRace(int raceId) =>
        Map(await _races.DeleteRaceAsync(new RaceId(raceId)));

    // ── checkpoints ────────────────────────────────────────────────────────────────────────────────

    [HttpPost("{raceId:int}/checkpoints")]
    [ProducesResponseType<RaceCheckpointRowDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiError>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiError>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AddCheckpoint(int raceId, [FromBody] AddCheckpointRequest request)
    {
        var (result, checkpoint) = await _checkpoints.AddCheckpointAsync(
            new RaceId(raceId), request.Name, request.ChainageMeters);
        return result == RaceWriteResult.Ok
            ? Ok(Row(checkpoint ?? throw new InvalidOperationException("an Ok result carries the checkpoint")))
            : Map(result);
    }

    /// <summary>Rename, move (drag) or re-label a checkpoint. Absent fields are left alone.</summary>
    [HttpPut("{raceId:int}/checkpoints/{checkpointId:int}")]
    [ProducesResponseType<RaceCheckpointRowDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiError>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiError>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateCheckpoint(
        int raceId, int checkpointId, [FromBody] UpdateCheckpointRequest request)
    {
        var (result, checkpoint) = await _checkpoints.UpdateCheckpointAsync(new RaceId(raceId), new CheckpointId(checkpointId), new RaceCheckpointEdit
        {
            Name = request.Name,
            ChainageMeters = request.ChainageMeters,
            LegName = request.LegName,
            LegColor = request.LegColor,
        });
        return result == RaceWriteResult.Ok
            ? Ok(Row(checkpoint ?? throw new InvalidOperationException("an Ok result carries the checkpoint")))
            : Map(result);
    }

    [HttpDelete("{raceId:int}/checkpoints/{checkpointId:int}")]
    [ProducesResponseType(StatusCodes.Status200OK)] // bodyless Ok() on success (RaceWriteResult.Ok)
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiError>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiError>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteCheckpoint(int raceId, int checkpointId) =>
        Map(await _checkpoints.DeleteCheckpointAsync(new RaceId(raceId), new CheckpointId(checkpointId)));

    // ── participants ───────────────────────────────────────────────────────────────────────────────

    /// <summary>Add a real rider — own or a friend's activity. Gated on VIEW rights, not ownership.</summary>
    [HttpPost("{raceId:int}/participants")]
    [ProducesResponseType<RaceParticipantRowDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiError>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiError>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AddRealParticipant(int raceId, [FromBody] AddRealParticipantRequest request)
    {
        var (result, participant) = await _participants.AddRealParticipantAsync(
            new RaceId(raceId), new TrackId(request.TrackId), request.DisplayName);
        return result == RaceWriteResult.Ok ? Ok(Row(participant)) : Map(result);
    }

    /// <summary>
    /// Rename a rider — what he is called in THIS race. A blank name resets to the ride's own name, so the
    /// resolved name comes back in the body rather than being assumed by the caller.
    /// </summary>
    [HttpPut("{raceId:int}/participants/name")]
    [ProducesResponseType<RaceParticipantRowDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiError>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiError>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RenameParticipant(
        int raceId, [FromBody] RenameParticipantRequest request)
    {
        var (result, participant) = await _participants.RenameParticipantAsync(
            new RaceId(raceId), new ParticipantId(request.ParticipantId), request.DisplayName);
        return result == RaceWriteResult.Ok ? Ok(Row(participant)) : Map(result);
    }

    /// <summary>
    /// The wire row for one participant. Both writes that answer with a participant share it — the
    /// <c>Ok</c>-carries-a-participant invariant is stated once rather than copied into each initialiser.
    /// </summary>
    private static RaceParticipantRowDto Row(RaceParticipant? participant)
    {
        var p = participant ?? throw new InvalidOperationException("an Ok result carries the participant");
        return new RaceParticipantRowDto
        {
            ParticipantId = p.ParticipantId.Value,
            Kind = p.Kind,
            Name = p.DisplayName,
            Bib = p.Bib,
            TrackId = p.TrackId?.Value,
        };
    }

    [HttpDelete("{raceId:int}/participants/{participantId:int}")]
    [ProducesResponseType(StatusCodes.Status200OK)] // bodyless Ok() on success (RaceWriteResult.Ok)
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiError>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiError>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RemoveParticipant(int raceId, int participantId) =>
        Map(await _participants.RemoveParticipantAsync(new RaceId(raceId), new ParticipantId(participantId)));

    /// <summary>Empty the field — simulated AND real, with their splits. The race and its checkpoints stay,
    /// so a corrected results CSV can be imported over a clean slate.</summary>
    [HttpDelete("{raceId:int}/participants")]
    [ProducesResponseType<RaceClearFieldResultDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiError>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiError>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ClearField(int raceId)
    {
        var (result, removed) = await _participants.ClearFieldAsync(new RaceId(raceId));
        return result == RaceWriteResult.Ok
            ? Ok(new RaceClearFieldResultDto { Removed = removed })
            : Map(result);
    }

    /// <summary>Paint a group of riders (null colour clears). Driven by the results table's filter.</summary>
    [HttpPut("{raceId:int}/participants/color")]
    [ProducesResponseType<RacePaintResultDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiError>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiError>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetParticipantColors(
        int raceId, [FromBody] SetParticipantColorsRequest request)
    {
        var (result, painted) = await _participants.SetParticipantColorsAsync(
            new RaceId(raceId), request.ParticipantIds.Select(i => new ParticipantId(i)).ToList(), request.Color);
        return result == RaceWriteResult.Ok
            ? Ok(new RacePaintResultDto { Painted = painted })
            : Map(result);
    }

    /// <summary>Emphasise a group of riders — bigger dots, drawn in front. Same filtered set as the paint.</summary>
    [HttpPut("{raceId:int}/participants/emphasis")]
    [ProducesResponseType<RaceEmphasisResultDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiError>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiError>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetParticipantEmphasis(
        int raceId, [FromBody] SetParticipantEmphasisRequest request)
    {
        var (result, updated) = await _participants.SetParticipantEmphasisAsync(
            new RaceId(raceId), request.ParticipantIds.Select(i => new ParticipantId(i)).ToList(), request.Emphasised);
        return result == RaceWriteResult.Ok
            ? Ok(new RaceEmphasisResultDto { Updated = updated })
            : Map(result);
    }

    /// <summary>Hide a group's call-out — no name label, no pinned row, no speed-chart line. Dots stay.</summary>
    [HttpPut("{raceId:int}/participants/callout-off")]
    [ProducesResponseType<RaceCalloutResultDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiError>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiError>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetParticipantCalloutOff(
        int raceId, [FromBody] SetParticipantCalloutOffRequest request)
    {
        var (result, updated) = await _participants.SetParticipantCalloutOffAsync(
            new RaceId(raceId), request.ParticipantIds.Select(i => new ParticipantId(i)).ToList(), request.CalloutOff);
        return result == RaceWriteResult.Ok
            ? Ok(new RaceCalloutResultDto { Updated = updated })
            : Map(result);
    }

    // ── import ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// What the import WOULD do: the sniffed delimiter and encoding, the header, an alias-driven guess per
    /// canonical field, and the first few rows fully built under the proposed mapping.
    ///
    /// Same authorization and the same importer as the write below — a preview computed a second way
    /// would be a second implementation to keep honest.
    /// </summary>
    [HttpPost("{raceId:int}/import/preview")]
    [RequestSizeLimit(ImportSizeLimit)]
    [ProducesResponseType<RaceImportPreviewDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiError>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ImportPreview(
        int raceId, [FromForm] ImportRaceCsvRequest request, CancellationToken ct)
    {
        var bytes = await ReadUploadAsync(request.File, ct);
        if (bytes == null) return BadRequest(ApiError.BadRequest("Choose a results file first."));

        RaceImportPreviewDto? preview;
        try
        {
            preview = await _import.PreviewAsync(new RaceId(raceId), bytes, MappingOf(request), ct);
        }
        catch (ArgumentException ex)
        {
            // An unreadable FILE is a 400. An unmappable one is not — RaceImportPreview returns that as a
            // warning, because the header and the guesses are exactly what the user needs to fix it.
            return BadRequest(ApiError.BadRequest(ex.Message));
        }

        return preview == null ? NotFound(ApiError.NotFound("Race not found.")) : Ok(preview);
    }

    /// <summary>Import a results CSV. Replaces the simulated field; real riders are untouched.</summary>
    [HttpPost("{raceId:int}/import")]
    [RequestSizeLimit(ImportSizeLimit)]
    [ProducesResponseType<RaceImportResultDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiError>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Import(
        int raceId, [FromForm] ImportRaceCsvRequest request, CancellationToken ct)
    {
        var bytes = await ReadUploadAsync(request.File, ct);
        if (bytes == null) return BadRequest(ApiError.BadRequest("Choose a results file first."));

        // The preview tolerates a blank mapping (it fills the blanks from its own guesses); the IMPORT
        // must not, and this is where that requirement lives now that the request type no longer carries
        // it. Without it the importer answers "CSV has no column ''", which names nothing the user can act on.
        if (string.IsNullOrWhiteSpace(request.NameColumn))
            return BadRequest(ApiError.BadRequest("Choose which column holds the rider or team name."));

        RaceCsvImportResult? result;
        try
        {
            result = await _import.ImportAsync(new RaceId(raceId), bytes, MappingOf(request), ct);
        }
        catch (ArgumentException ex)
        {
            // A bad file or an unmapped column is the user's problem to fix, not a 500.
            return BadRequest(ApiError.BadRequest(ex.Message));
        }

        // Forbid() is NOT a 403 here: with cookie auth it hands back a 302 to /Auth/AccessDenied, so the
        // panel's fetch sees `ok` and dies on a JSON parse error instead of showing the reason. 404 with
        // the canonical body, byte-identical to Map()'s RaceNotFound — EditableRaceAsync already returns
        // null for "missing", "not visible" and "not editable" alike, and the slice's no-oracle rule
        // (RacesApiControllerNoOracleTests) is that this surface answers 404, never 403.
        return result == null
            ? NotFound(ApiError.NotFound("Race not found."))
            : Ok(new RaceImportResultDto
            {
                Imported = result.Participants.Count,
                Finishers = result.FinisherCount,   // NOT the same number — a DNF is imported but did not finish
                Skipped = result.Skipped,
                OverlappedLegCount = result.OverlappedLegCount,
            });
    }

    /// <summary>
    /// Assign a start offset to every entrant in a wave — one number per block, typed once.
    ///
    /// The offsets a file publishes (an explicit column, or gun − net) are read at import. This is for the
    /// files that publish only a LABEL, which is most of them: Schauinsland's blocks are called
    /// "Startblock 1: 10 Uhr bis 11 Uhr" and carry no machine-readable time at all.
    /// </summary>
    [HttpPut("{raceId:int}/waves")]
    [ProducesResponseType<RaceWaveResultDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiError>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiError>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetWaveOffsets(
        int raceId, [FromBody] SetRaceWaveOffsetsRequest request)
    {
        var (result, updated) = await _participants.SetWaveOffsetsAsync(new RaceId(raceId), request.Offsets);
        return result == RaceWriteResult.Ok ? Ok(new RaceWaveResultDto { Updated = updated }) : Map(result);
    }

    // ── template + saved mappings ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// A results CSV template built from THIS race's own checkpoints, with example rows.
    ///
    /// <c>[ProducesResponseType(typeof(void), 200, "text/csv")]</c> and NOT a bare <c>[Produces]</c>:
    /// that attribute is a result FILTER, and it would turn the declared JSON error bodies below into
    /// empty 406s — which BinaryErrorBodyPipelineTests already pins for the binary endpoints.
    /// </summary>
    [HttpGet("{raceId:int}/import/template.csv")]
    [ProducesResponseType(typeof(void), StatusCodes.Status200OK, "text/csv")]
    [ProducesResponseType<ApiError>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ImportTemplate(int raceId)
    {
        var checkpoints = await _access.EditableCheckpointsAsync(new RaceId(raceId));
        if (checkpoints == null) return NotFound(ApiError.NotFound("Race not found."));

        var csv = RaceCsvTemplate.Build(checkpoints);
        // UTF-8 WITH a BOM, and it is not decoration: Excel opens a BOM-less UTF-8 CSV as the system code
        // page, so every accented example name would arrive mojibake in the one file whose whole job is to
        // be opened in a spreadsheet. Our own parser strips it (RaceCsvDecoder), so the round trip is clean.
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(csv)).ToArray();
        return File(bytes, "text/csv", $"race-{raceId}-results-template.csv");
    }

    /// <summary>
    /// This user's saved column mappings, resolved onto THIS race's checkpoints.
    ///
    /// Per USER, not per race — that is the whole point: next year's file from the same timing platform
    /// imports in one click. The raceId in the path is what the stored (portable) split mapping is
    /// resolved against, and the authorization seam.
    /// </summary>
    [HttpGet("{raceId:int}/import/profiles")]
    [ProducesResponseType<List<RaceImportProfileDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ImportProfiles(int raceId)
    {
        var checkpoints = await _access.EditableCheckpointsAsync(new RaceId(raceId));
        var userId = _access.CurrentUserId();
        if (checkpoints == null || userId == null) return NotFound(ApiError.NotFound("Race not found."));

        return Ok(await _profiles.ListAsync(userId, checkpoints));
    }

    /// <summary>Save (or replace) a mapping under a name of the user's choosing.</summary>
    [HttpPut("{raceId:int}/import/profiles")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiError>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SaveImportProfile(
        int raceId, [FromBody] SaveRaceImportProfileRequest request)
    {
        var name = RaceImportProfileStore.NormalizeName(request.Name);
        if (name == null)
            return BadRequest(ApiError.BadRequest("A profile name is required, without ':' and under 60 characters."));

        var checkpoints = await _access.EditableCheckpointsAsync(new RaceId(raceId));
        var userId = _access.CurrentUserId();
        if (checkpoints == null || userId == null) return NotFound(ApiError.NotFound("Race not found."));

        await _profiles.SaveAsync(userId, name, MappingOf(request), checkpoints);
        return Ok();
    }

    /// <summary>Forget a saved mapping. 404 when this user has none by that name.</summary>
    [HttpDelete("{raceId:int}/import/profiles/{name}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteImportProfile(int raceId, string name)
    {
        var normalized = RaceImportProfileStore.NormalizeName(name);
        var userId = _access.CurrentUserId();
        if (normalized == null || userId == null
            || await _access.EditableCheckpointsAsync(new RaceId(raceId)) == null)
            return NotFound(ApiError.NotFound("Race not found."));

        return await _profiles.DeleteAsync(userId, normalized)
            ? Ok()
            : NotFound(ApiError.NotFound("No saved mapping by that name."));
    }

    /// <summary>
    /// Kestrel's default body cap is 30 MB, and a 50,000-finisher file with splits is roughly 15 MB — so
    /// this is generous headroom and still a bound, which is the point: every sibling upload endpoint
    /// (Photos, Segments, Import) declares one, and without it a too-large file gets an opaque 413 rather
    /// than the mapped 400 every other rejection here gets.
    /// </summary>
    private const int ImportSizeLimit = 32 * 1024 * 1024;

    /// <summary>The uploaded bytes, or null when no file was attached. Buffered: the parser needs the
    /// whole thing, and the size limit above is what bounds it.</summary>
    private static async Task<byte[]?> ReadUploadAsync(IFormFile? file, CancellationToken ct)
    {
        if (file == null || file.Length == 0) return null;
        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);
        return buffer.ToArray();
    }

    private static RaceCsvMapping MappingOf(RaceCsvMappingRequest request) => new()
    {
        // Blank is legal on the way IN (see RaceCsvMappingRequest.NameColumn) — the preview fills it from
        // its guesses, and the import path rejects it above with a message of its own.
        NameColumn = request.NameColumn ?? string.Empty,
        FinishColumn = request.FinishColumn,
        BibColumn = request.BibColumn,
        CategoryColumn = request.CategoryColumn,
        GenderColumn = request.GenderColumn,
        ClubColumn = request.ClubColumn,
        StatusColumn = request.StatusColumn,
        GunTimeColumn = request.GunTimeColumn,
        NetTimeColumn = request.NetTimeColumn,
        StartOffsetColumn = request.StartOffsetColumn,
        WaveColumn = request.WaveColumn,
        DisciplineColumn = request.DisciplineColumn,
        SplitColumns = request.SplitColumns ?? new Dictionary<int, string>(),
        PerLegTimes = request.PerLegTimes,
    };

    // ── mapping ────────────────────────────────────────────────────────────────────────────────────

    private static RaceCheckpointRowDto Row(Models.RaceCheckpoint c) => new()
    {
        CheckpointId = c.CheckpointId.Value,
        Seq = c.Seq,
        Name = c.CheckpointName,
        ChainageMeters = c.ChainageMeters,
        LegName = c.LegName,
        LegColor = c.LegColor,
    };

    /// <summary>
    /// Entities never leave this controller — see RaceSummaryDto for why. Only the create path needs
    /// this: it is fed by GetRaceAsync, which DOES Include both collections. The list path projects its
    /// counts in SQL instead (RaceCrud.GetRacesAsync).
    /// </summary>
    private static RaceSummaryDto Summary(Models.Race race) => new()
    {
        RaceId = race.RaceId.Value,
        RaceName = race.RaceName,
        RaceDate = race.RaceDate.ToWallClockIso(),
        Visibility = race.Visibility,
        CheckpointCount = race.Checkpoints.Count,
        ParticipantCount = race.Participants.Count,
        CourseTrackId = race.CourseTrackId.Value,
        // The create path returns the just-made race; the list page (GetRacesAsync) is the only
        // card-rendering reader, so the card's distance stays null on this response.
        RaceDistanceMeters = null,
    };

    // TODO(openapi-review): D21 — every Map-routed action is blanket-annotated 400+409 (what this
    // switch can emit with a body), not per-endpoint reachability; mirrors the TrackEdit D19 ruling.
    private IActionResult Map(RaceWriteResult result) => result switch
    {
        RaceWriteResult.Ok => Ok(),
        RaceWriteResult.RaceNotFound => NotFound(ApiError.NotFound("Race not found.")),
        // 404, not 403, AND byte-identical to RaceNotFound above (MED-1): a ride the caller cannot see must
        // be indistinguishable from a race that does not exist — not just in status but in body, or the two
        // messages ("Race not found." vs the old "Activity not found.") let a caller tell "race missing"
        // from "course not visible", the exact oracle the no-oracle convention forbids.
        RaceWriteResult.ActivityNotVisible => NotFound(ApiError.NotFound("Race not found.")),
        RaceWriteResult.DuplicateParticipant => Conflict(ApiError.Conflict("Already in this race.")),
        RaceWriteResult.InvalidChainage =>
            BadRequest(ApiError.BadRequest("Checkpoint is off the course, or too close to another one.")),
        RaceWriteResult.EndpointCheckpoint =>
            BadRequest(ApiError.BadRequest("Start and Finish cannot be removed.")),
        RaceWriteResult.InvalidColor => BadRequest(ApiError.BadRequest("Colour must be #rrggbb.")),
        RaceWriteResult.InvalidEditPolicy =>
            BadRequest(ApiError.BadRequest("Edit policy must be owner, friends or public.")),
        RaceWriteResult.InvalidDisplayName =>
            BadRequest(ApiError.BadRequest("A rider's name must be 255 characters or fewer.")),
        _ => StatusCode(500),
    };
}
