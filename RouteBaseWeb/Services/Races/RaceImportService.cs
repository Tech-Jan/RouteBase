using RouteBaseWeb.Models;
using Microsoft.EntityFrameworkCore;

namespace RouteBaseWeb.Services.Races;

/// <summary>Imports a results CSV into a race's field of simulated participants.</summary>
public interface IRaceImportService
{
    /// <summary>
    /// Replace the race's SIMULATED field from a results CSV. Real participants (own/friends' rides) are
    /// left alone — re-importing a corrected results file must not evict the humans.
    /// Returns null when the caller may not edit the race (see <see cref="IRaceAccess.EditableRaceAsync"/>).
    ///
    /// Takes BYTES, not a string: the encoding is the server's call to make. Blob.text() in the browser is
    /// unconditionally UTF-8, so a Windows-1252 export lost every accented name before it left the page.
    /// </summary>
    Task<RaceCsvImportResult?> ImportAsync(
        RaceId raceId, byte[] csv, RaceCsvMapping mapping, CancellationToken ct = default);

    /// <summary>
    /// What the import WOULD do — same parser, same importer, nothing written. Null when the caller may
    /// not edit the race. <paramref name="proposed"/> may be empty on the first call; the preview fills
    /// the blanks from its own guesses so it has real rows to show.
    /// </summary>
    Task<RaceImportPreviewDto?> PreviewAsync(
        RaceId raceId, byte[] csv, RaceCsvMapping? proposed, CancellationToken ct = default);
}

/// <inheritdoc />
public class RaceImportService : IRaceImportService
{
    private readonly RouteBaseDbContext _context;
    private readonly IRaceAccess _access;
    private readonly ILogger<RaceImportService> _logger;

    public RaceImportService(
        RouteBaseDbContext context, IRaceAccess access, ILogger<RaceImportService> logger)
    {
        _context = context;
        _access = access;
        _logger = logger;
    }

    public async Task<RaceImportPreviewDto?> PreviewAsync(
        RaceId raceId, byte[] csv, RaceCsvMapping? proposed, CancellationToken ct = default)
    {
        // The same authorization rule as the import, via the seam's read-only half: a preview needs the
        // course's SHAPE, not its field. EditableRaceAsync would load and track every participant —
        // 491 of them on Ski to Sea — on a call that re-runs each time a picker changes.
        var checkpoints = await _access.EditableCheckpointsAsync(raceId);
        if (checkpoints == null) return null;

        return RaceImportPreview.Build(RaceCsvDecoder.Decode(csv), proposed, checkpoints, raceId);
    }

    public async Task<RaceCsvImportResult?> ImportAsync(
        RaceId raceId, byte[] csv, RaceCsvMapping mapping, CancellationToken ct = default)
    {
        // Through the slice's single authorization seam — owner OR a permitted collaborator. Returns the
        // tracked race with checkpoints + the field Included, which is exactly what the import mutates.
        var race = await _access.EditableRaceAsync(raceId);
        if (race == null) return null;

        var table = RaceCsvParser.Parse(RaceCsvDecoder.Decode(csv).Text);
        var result = RaceCsvImporter.Build(table, mapping, race.Checkpoints.ToList(), raceId);

        // The delete below removes the WHOLE simulated field, so an import that produced nobody must not
        // reach it: one wrong column mapping (or a delimiter we could not read) would silently wipe 425
        // riders and report success. Thrown before the transaction opens — the caller maps
        // ArgumentException to a 400, which is what a fixable mapping mistake deserves.
        if (result.Participants.Count == 0)
            throw new ArgumentException(
                $"No rows could be imported ({result.Skipped.Count} row(s) skipped). The existing field was "
                + "left unchanged — check the column mapping.", nameof(csv));

        await using var tx = await _context.Database.BeginTransactionAsync(ct);

        // Re-import replaces the simulated field wholesale — the alternative (merging by bib) silently
        // keeps riders the organiser has since removed. Real participants are untouched.
        var stale = race.Participants.Where(p => p.Kind == RaceParticipantKind.Simulated).ToList();
        _context.RaceParticipants.RemoveRange(stale);          // split times cascade
        await _context.SaveChangesAsync(ct);

        foreach (var p in result.Participants) _context.RaceParticipants.Add(p.Participant);
        await _context.SaveChangesAsync(ct);                   // assigns participant ids

        foreach (var p in result.Participants)
        {
            foreach (var s in p.Splits) s.ParticipantId = p.Participant.ParticipantId;
            _context.RaceSplitTimes.AddRange(p.Splits);
        }
        await _context.SaveChangesAsync(ct);

        await tx.CommitAsync(ct);

        _logger.LogInformation(
            "Race {RaceId}: imported {Count} simulated participants ({Skipped} rows skipped), replacing {Stale}",
            raceId, result.Participants.Count, result.Skipped.Count, stale.Count);

        return result;
    }
}
