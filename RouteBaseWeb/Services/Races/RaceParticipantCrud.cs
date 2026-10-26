using RouteBaseWeb.Models;
using Microsoft.EntityFrameworkCore;

namespace RouteBaseWeb.Services.Races;

/// <summary>
/// The field: real riders in and out, and the colours a group is painted with.
/// </summary>
public interface IRaceParticipantCrud
{
    /// <summary>
    /// Add an activity (own or a friend's) to the field as a real participant. Its geometry is NOT
    /// projected onto the course — the marker rides its own line; chainage is derived later.
    /// </summary>
    Task<(RaceWriteResult Result, RaceParticipant? Participant)> AddRealParticipantAsync(
        RaceId raceId, TrackId trackId, string? displayName);

    /// <summary>Remove a participant (either kind) from the field.</summary>
    Task<RaceWriteResult> RemoveParticipantAsync(RaceId raceId, ParticipantId participantId);

    /// <summary>
    /// What a rider is CALLED in this race — "Marco", not the name of the ride he is drawn from.
    ///
    /// A blank name resets to the ride's own name, the same fallback the add uses. Returns the
    /// participant, because a reset means the caller cannot know the resulting name without being told.
    /// </summary>
    Task<(RaceWriteResult Result, RaceParticipant? Participant)> RenameParticipantAsync(
        RaceId raceId, ParticipantId participantId, string? displayName);

    /// <summary>
    /// Assign a start offset to every entrant in a wave — one number per BLOCK, typed once.
    ///
    /// This is the no-code answer for a file whose start blocks carry no machine-readable time, which is
    /// most of them: Schauinsland's are called <c>Startblock 1: 10 Uhr bis 11 Uhr</c>. The offsets a file
    /// DOES publish (an explicit column, or gun − net) are read at import and never come through here.
    ///
    /// Returns how many participants were moved.
    /// </summary>
    Task<(RaceWriteResult Result, int Updated)> SetWaveOffsetsAsync(
        RaceId raceId, IReadOnlyDictionary<string, double?> offsetsByWave);

    /// <summary>
    /// Empty the field — every participant of BOTH kinds, and their split times with them. The race, its
    /// course and its checkpoints survive: this is "re-import the results CSV from scratch", not "delete
    /// the race". Returns how many riders were removed.
    /// </summary>
    Task<(RaceWriteResult Result, int Removed)> ClearFieldAsync(RaceId raceId);

    /// <summary>
    /// Paint a group. <paramref name="color"/> null clears back to the default (real = yellow,
    /// simulated = blue). Returns the number of rows actually painted.
    ///
    /// This is how the results table colours the map: the user filters a group — gender, division, club,
    /// and the filters COMPOSE — and paints the whole filtered set in one call. So the id list is as
    /// large as the field, and that is the hazard this method exists to contain (see the implementation).
    /// </summary>
    Task<(RaceWriteResult Result, int Painted)> SetParticipantColorsAsync(
        RaceId raceId, IReadOnlyList<ParticipantId> participantIds, string? color);

    /// <summary>
    /// Emphasise a group — bigger dots, drawn in front. Driven by the same filtered set as the paint
    /// above, and deliberately a SEPARATE call rather than a second field on it: painting a division and
    /// emphasising two riders inside it are independent choices, and one request would force the user to
    /// re-state the colour every time he changed the emphasis.
    ///
    /// Returns the number of rows actually changed.
    /// </summary>
    Task<(RaceWriteResult Result, int Updated)> SetParticipantEmphasisAsync(
        RaceId raceId, IReadOnlyList<ParticipantId> participantIds, bool emphasised);

    /// <summary>
    /// Hide a group's CALL-OUT — the name label, the pinned sidebar row and the speed-chart line. The
    /// riders stay in the field and keep their dots.
    ///
    /// The switch a real rider had no way to reach: he is called out by virtue of being real, so before
    /// this the only way to get a second rider off the speed chart was to delete him from the race.
    /// Returns the number of rows actually changed.
    /// </summary>
    Task<(RaceWriteResult Result, int Updated)> SetParticipantCalloutOffAsync(
        RaceId raceId, IReadOnlyList<ParticipantId> participantIds, bool calloutOff);
}

/// <inheritdoc />
public class RaceParticipantCrud : IRaceParticipantCrud
{
    /// <summary>
    /// Ids per UPDATE. SQLite caps a statement at 999 host parameters, and EF renders `Contains` as an
    /// `IN (@p0, @p1, …)` — one parameter per id. A 424-id `IN` already crashed this slice once
    /// and a painted 508-team field would be worse. Chunk, always.
    /// </summary>
    private const int IdsPerBatch = 200;

    /// <summary>Mirrors <see cref="RaceParticipant.DisplayName"/>'s [MaxLength]. SQLite does not enforce
    /// it, so the check has to live here — and it REJECTS rather than truncates: a silently shortened
    /// name is a name the user did not choose.</summary>
    private const int MaxDisplayName = 255;

    private readonly RouteBaseDbContext _context;
    private readonly IRaceAccess _access;

    public RaceParticipantCrud(RouteBaseDbContext context, IRaceAccess access)
    {
        _context = context;
        _access = access;
    }

    public async Task<(RaceWriteResult, RaceParticipant?)> AddRealParticipantAsync(
        RaceId raceId, TrackId trackId, string? displayName)
    {
        var race = await _access.EditableRaceAsync(raceId);
        if (race == null) return (RaceWriteResult.RaceNotFound, null);

        // VIEW, not own: the whole point is racing a friend's ride. CanViewActivityAsync also enforces
        // the lifecycle gate (a non-owner only ever sees 'released').
        if (!await _access.CanViewActivityAsync(trackId))
            return (RaceWriteResult.ActivityNotVisible, null);

        if (race.Participants.Any(p => p.TrackId == trackId))
            return (RaceWriteResult.DuplicateParticipant, null);

        var name = Trimmed(displayName);
        if (name is { Length: > MaxDisplayName }) return (RaceWriteResult.InvalidDisplayName, null);

        var activity = await _context.Activities.AsNoTracking().FirstAsync(a => a.TrackId == trackId);
        var participant = new RaceParticipant
        {
            RaceId = raceId,
            Kind = RaceParticipantKind.Real,
            TrackId = trackId,
            DisplayName = name ?? activity.TrackName,
            // Painted on ADD rather than left to the kind default, because that default is one colour:
            // two real riders came out identically gold on the map, in the results swatches and as two
            // solid gold lines on the speed chart, where telling them apart is the entire point.
            Color = RaceColors.NextRealColor(race.Participants.Select(p => p.Color)),
        };
        _context.RaceParticipants.Add(participant);
        await _context.SaveChangesAsync();
        return (RaceWriteResult.Ok, participant);
    }

    /// <inheritdoc />
    public async Task<(RaceWriteResult, RaceParticipant?)> RenameParticipantAsync(
        RaceId raceId, ParticipantId participantId, string? displayName)
    {
        if (!await _access.CanEditRaceAsync(raceId)) return (RaceWriteResult.RaceNotFound, null);

        var participant = await ParticipantInAsync(raceId, participantId);
        if (participant == null) return (RaceWriteResult.RaceNotFound, null);

        var name = Trimmed(displayName);
        if (name is { Length: > MaxDisplayName }) return (RaceWriteResult.InvalidDisplayName, null);

        // Blank = back to the ride's own name. A SIMULATED rider has no ride to fall back to, so blank
        // leaves it nameless and the display chain shows his bib instead — no special case needed.
        participant.DisplayName = name ?? await RideNameAsync(participant.TrackId);
        await _context.SaveChangesAsync();
        return (RaceWriteResult.Ok, participant);
    }

    /// <summary>Trim, and treat a blank name as absent so the caller's fallback fires. ONE rule, shared by
    /// the add and the rename — two copies would be free to drift.</summary>
    private static string? Trimmed(string? displayName) =>
        string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();

    /// <summary>
    /// One participant, scoped to the race that is being edited — the id came from the REQUEST, so
    /// race_ID is part of the lookup and an id belonging to someone else's race is simply not found.
    ///
    /// Deliberately NOT <see cref="IRaceAccess.EditableRaceAsync"/>: that loads the whole field plus every
    /// checkpoint, tracked, and a 508-team import makes that the expensive way to touch one row. Same
    /// reasoning as the paint and the emphasis below; the gate is the same rule either way.
    /// </summary>
    private Task<RaceParticipant?> ParticipantInAsync(RaceId raceId, ParticipantId participantId) =>
        _context.RaceParticipants
            .FirstOrDefaultAsync(p => p.RaceId == raceId && p.ParticipantId == participantId);

    /// <summary>The name of the ride a real rider is drawn from — what a blank name resets to.</summary>
    private async Task<string?> RideNameAsync(TrackId? trackId) =>
        trackId is { } id
            ? (await _context.Activities.AsNoTracking().FirstOrDefaultAsync(a => a.TrackId == id))?.TrackName
            : null;

    public async Task<RaceWriteResult> RemoveParticipantAsync(RaceId raceId, ParticipantId participantId)
    {
        if (!await _access.CanEditRaceAsync(raceId)) return RaceWriteResult.RaceNotFound;

        var participant = await ParticipantInAsync(raceId, participantId);
        if (participant == null) return RaceWriteResult.RaceNotFound;

        _context.RaceParticipants.Remove(participant);   // split times cascade
        await _context.SaveChangesAsync();
        return RaceWriteResult.Ok;
    }

    /// <inheritdoc />
    public async Task<(RaceWriteResult Result, int Updated)> SetWaveOffsetsAsync(
        RaceId raceId, IReadOnlyDictionary<string, double?> offsetsByWave)
    {
        var race = await _access.EditableRaceAsync(raceId);
        if (race == null) return (RaceWriteResult.RaceNotFound, 0);

        // Ordinal-ignore-case, because the wave label is matched against text the FILE wrote and the user
        // then saw rendered — CurrentCulture equality would change which riders a block contains under a
        // Turkish or Thai server culture.
        var offsets = new Dictionary<string, double?>(offsetsByWave, StringComparer.OrdinalIgnoreCase);

        var updated = 0;
        foreach (var p in race.Participants)
        {
            if (p.Wave is not { Length: > 0 } wave) continue;
            if (!offsets.TryGetValue(wave, out var offset)) continue;
            // A non-finite or negative offset is not a start time — nobody leaves before the gun, and a
            // NaN would reach the browser's clock arithmetic and take the whole field with it. Rejected
            // per rider rather than for the whole request: one bad cell must not discard the four blocks
            // that were fine.
            if (offset is { } value && (!double.IsFinite(value) || value < 0)) continue;

            p.StartOffsetSeconds = offset;
            updated++;
        }

        if (updated > 0) await _context.SaveChangesAsync();
        return (RaceWriteResult.Ok, updated);
    }

    public async Task<(RaceWriteResult, int)> ClearFieldAsync(RaceId raceId)
    {
        var race = await _access.EditableRaceAsync(raceId);
        if (race == null) return (RaceWriteResult.RaceNotFound, 0);

        var field = race.Participants.ToList();
        if (field.Count == 0) return (RaceWriteResult.Ok, 0);

        _context.RaceParticipants.RemoveRange(field);   // split times cascade, as everywhere else here
        await _context.SaveChangesAsync();
        return (RaceWriteResult.Ok, field.Count);
    }

    public async Task<(RaceWriteResult, int)> SetParticipantColorsAsync(
        RaceId raceId, IReadOnlyList<ParticipantId> participantIds, string? color)
    {
        // Editable-gated, and the race must exist — but the ids are NOT trusted from the request: every
        // UPDATE is scoped by race_ID, so an id belonging to someone else's race cannot be painted.
        // Gate-only check (same MayEdit rule): a paint touches no loaded entity, so loading the race
        // with its full checkpoints + participants graph here was pure waste.
        if (!await _access.CanEditRaceAsync(raceId)) return (RaceWriteResult.RaceNotFound, 0);

        if (!RaceColors.IsValid(color)) return (RaceWriteResult.InvalidColor, 0);
        var normalized = RaceColors.Normalize(color);

        if (participantIds.Count == 0) return (RaceWriteResult.Ok, 0);

        // ONE transaction across the chunks. ExecuteUpdateAsync bypasses the change tracker and commits on
        // the spot, so a 508-team paint is three independent statements — and if the second one fails
        // (SQLite busy, a dropped connection), the first 200 riders are already painted while the caller
        // sees the whole call throw. The browser then reports "Paint failed" and never repaints anything,
        // so the map, the profile and the swatches all still show the old colours: 200 riders are pink in
        // the database and nothing on screen says so until a reload. A paint is one user action; it
        // commits once or not at all. (RaceImportService wraps its writes for exactly this reason.)
        await using var tx = await _context.Database.BeginTransactionAsync();

        var painted = 0;
        foreach (var batch in participantIds.Distinct().Chunk(IdsPerBatch))
        {
            painted += await _context.RaceParticipants
                .Where(p => p.RaceId == raceId && batch.Contains(p.ParticipantId))
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Color, normalized));
        }

        await tx.CommitAsync();
        return (RaceWriteResult.Ok, painted);
    }

    public async Task<(RaceWriteResult, int)> SetParticipantEmphasisAsync(
        RaceId raceId, IReadOnlyList<ParticipantId> participantIds, bool emphasised)
    {
        // Same gate, same chunking and the same one-transaction rule as the paint above, for the same
        // reasons — this call takes the same filtered set and is the same size.
        if (!await _access.CanEditRaceAsync(raceId)) return (RaceWriteResult.RaceNotFound, 0);

        if (participantIds.Count == 0) return (RaceWriteResult.Ok, 0);

        // OFF is written as NULL, not false, so the column has exactly ONE representation of "not
        // emphasised" — rows predating it are already null, and a mixed column would make every hand-run
        // `WHERE emphasised` query subtly wrong. The read side never sees the difference (== true).
        bool? value = emphasised ? true : null;

        await using var tx = await _context.Database.BeginTransactionAsync();

        var updated = 0;
        foreach (var batch in participantIds.Distinct().Chunk(IdsPerBatch))
        {
            updated += await _context.RaceParticipants
                .Where(p => p.RaceId == raceId && batch.Contains(p.ParticipantId))
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Emphasised, value));
        }

        await tx.CommitAsync();
        return (RaceWriteResult.Ok, updated);
    }

    public async Task<(RaceWriteResult, int)> SetParticipantCalloutOffAsync(
        RaceId raceId, IReadOnlyList<ParticipantId> participantIds, bool calloutOff)
    {
        // Third call on this shape: same gate, same chunking, same one-transaction rule as the paint and
        // the emphasis above, because it takes the same filtered set and can be the same size.
        if (!await _access.CanEditRaceAsync(raceId)) return (RaceWriteResult.RaceNotFound, 0);

        if (participantIds.Count == 0) return (RaceWriteResult.Ok, 0);

        // The DEFAULT is written as NULL — same one-representation rule as the emphasis above, applied to
        // the other polarity: here the default is "called out", so `false` never reaches the column and
        // `WHERE callout_off` means exactly what it reads as.
        bool? value = calloutOff ? true : null;

        await using var tx = await _context.Database.BeginTransactionAsync();

        var updated = 0;
        foreach (var batch in participantIds.Distinct().Chunk(IdsPerBatch))
        {
            updated += await _context.RaceParticipants
                .Where(p => p.RaceId == raceId && batch.Contains(p.ParticipantId))
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.CalloutOff, value));
        }

        await tx.CommitAsync();
        return (RaceWriteResult.Ok, updated);
    }
}
