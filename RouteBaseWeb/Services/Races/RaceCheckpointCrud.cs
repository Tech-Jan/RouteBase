using RouteBaseWeb.Models;

namespace RouteBaseWeb.Services.Races;

/// <summary>
/// Checkpoints — the primitive the pacing model works in.
///
/// One invariant runs through every method here: **seq order IS chainage order.** The model walks
/// checkpoints pairwise by seq, so a seq that disagrees with chainage hands it a negative interval.
/// Every insert, move and delete therefore renumbers.
/// </summary>
public interface IRaceCheckpointCrud
{
    /// <summary>Insert an intermediate checkpoint, renumbering seq so it stays chainage-ordered.</summary>
    Task<(RaceWriteResult Result, RaceCheckpoint? Checkpoint)> AddCheckpointAsync(
        RaceId raceId, string checkpointName, double chainageMeters);

    /// <summary>
    /// Rename / move / re-label a checkpoint. Moving renumbers seq exactly as inserting does — dragging
    /// one past its neighbour must not be able to break the invariant.
    ///
    /// EVERY checkpoint may be moved, Start and Finish included: they are where the user says the lines
    /// were, not the ends of whatever GPS the ride happens to contain. What holds them is
    /// <see cref="RaceCheckpointBounds"/> — inside the course grid, and still the chainage extremes.
    /// </summary>
    Task<(RaceWriteResult Result, RaceCheckpoint? Checkpoint)> UpdateCheckpointAsync(
        RaceId raceId, CheckpointId checkpointId, RaceCheckpointEdit edit);

    /// <summary>Remove an intermediate checkpoint. Its split times cascade. Start/Finish cannot be removed.</summary>
    Task<RaceWriteResult> DeleteCheckpointAsync(RaceId raceId, CheckpointId checkpointId);
}

/// <inheritdoc />
public class RaceCheckpointCrud : IRaceCheckpointCrud
{
    private readonly RouteBaseDbContext _context;
    private readonly IRaceAccess _access;
    private readonly IRaceCourseService _courses;

    public RaceCheckpointCrud(RouteBaseDbContext context, IRaceAccess access, IRaceCourseService courses)
    {
        _context = context;
        _access = access;
        _courses = courses;
    }

    /// <summary>
    /// The course's length in the ONE metric chainage is measured in. Null when the course has no gridable
    /// geometry — every move is then refused rather than validated against a number this slice made up.
    /// </summary>
    private async Task<double?> GridTotalAsync(Race race) =>
        (await _courses.GetCourseGridAsync(race.CourseTrackId))?.TotalMeters;

    /// <summary>Every OTHER checkpoint's chainage — what the bounds are computed against.</summary>
    private static List<double> OtherChainages(Race race, CheckpointId exclude) =>
        race.Checkpoints.Where(c => c.CheckpointId != exclude).Select(c => c.ChainageMeters).ToList();

    public async Task<(RaceWriteResult, RaceCheckpoint?)> AddCheckpointAsync(
        RaceId raceId, string checkpointName, double chainageMeters)
    {
        var race = await _access.EditableRaceAsync(raceId);
        if (race == null) return (RaceWriteResult.RaceNotFound, null);

        // A new checkpoint is always an intermediate — strictly between the two lines, off every existing
        // one. RaceCheckpointBounds owns the rule (NaN included; see IsLegal) for adds and moves alike.
        if (await GridTotalAsync(race) is not double gridTotal) return (RaceWriteResult.InvalidChainage, null);
        var others = race.Checkpoints.Select(c => c.ChainageMeters).ToList();
        if (others.Count == 0
            || !RaceCheckpointBounds.IsLegal(chainageMeters, CheckpointRole.Intermediate, others, gridTotal))
            return (RaceWriteResult.InvalidChainage, null);

        var checkpoint = new RaceCheckpoint
        {
            RaceId = raceId,
            CheckpointName = checkpointName,
            ChainageMeters = chainageMeters,
        };
        _context.RaceCheckpoints.Add(checkpoint);

        // Distinct, because EF relationship FIX-UP has already put this row into race.Checkpoints: the race
        // is tracked with its checkpoints included, and setting RaceId is enough for EF to wire it into the
        // parent's collection the moment it is tracked. Appending it on top of that enumerated it TWICE, and
        // Renumber then assigned it two seqs in a row — leaving a hole (Start=0, A=2, Finish=3, no seq 1).
        // Seq order IS chainage order in this slice, and a gapless seq is the invariant every other method
        // here maintains; the leg labels are numbered from it.
        await RenumberAndSaveAsync(race.Checkpoints.Append(checkpoint).Distinct());
        return (RaceWriteResult.Ok, checkpoint);
    }

    public async Task<(RaceWriteResult, RaceCheckpoint?)> UpdateCheckpointAsync(
        RaceId raceId, CheckpointId checkpointId, RaceCheckpointEdit edit)
    {
        var race = await _access.EditableRaceAsync(raceId);
        if (race == null) return (RaceWriteResult.RaceNotFound, null);

        var checkpoint = race.Checkpoints.FirstOrDefault(c => c.CheckpointId == checkpointId);
        if (checkpoint == null) return (RaceWriteResult.RaceNotFound, null);

        if (!RaceColors.IsValid(edit.LegColor)) return (RaceWriteResult.InvalidColor, null);

        // EVERY rejection is decided before ANYTHING is written.
        //
        // The race comes back TRACKED, so assigning to it stages a change on a live change-tracker — and
        // the name/leg writes used to run BEFORE the chainage and endpoint checks could reject the request.
        // A refused move therefore left a renamed checkpoint staged behind it, and any SaveChangesAsync
        // later in the same scoped context would have committed half of a write this method reported as
        // rejected. Validate, then mutate.
        double? newChainage = null;
        if (edit.ChainageMeters is double chainage)
        {
            // The role is decided by IDENTITY against the current extremes, not by the proposed number: a
            // Finish being dragged inward is still the Finish, and must be held above the last checkpoint
            // rather than validated as the intermediate its new chainage would make it.
            if (await GridTotalAsync(race) is not double gridTotal) return (RaceWriteResult.InvalidChainage, null);
            var role = RoleOf(race, checkpoint);
            if (!RaceCheckpointBounds.IsLegal(chainage, role, OtherChainages(race, checkpointId), gridTotal))
                return (RaceWriteResult.InvalidChainage, null);

            newChainage = chainage;
        }

        // Labels are free on every checkpoint, including the ends. Only their POSITION is fixed.
        if (edit.Name is not null) checkpoint.CheckpointName = edit.Name;
        if (edit.LegName is not null) checkpoint.LegName = edit.LegName;
        if (edit.LegColor is not null) checkpoint.LegColor = RaceColors.Normalize(edit.LegColor);

        if (newChainage is double moved)
        {
            checkpoint.ChainageMeters = moved;
            // A drag past a neighbour reorders — seq must follow, and it commits the label edits with it.
            await RenumberAndSaveAsync(race.Checkpoints);
        }
        else
        {
            await _context.SaveChangesAsync();
        }

        return (RaceWriteResult.Ok, checkpoint);
    }

    public async Task<RaceWriteResult> DeleteCheckpointAsync(RaceId raceId, CheckpointId checkpointId)
    {
        var race = await _access.EditableRaceAsync(raceId);
        if (race == null) return RaceWriteResult.RaceNotFound;

        var checkpoint = race.Checkpoints.FirstOrDefault(c => c.CheckpointId == checkpointId);
        if (checkpoint == null) return RaceWriteResult.RaceNotFound;
        if (IsEndpoint(race, checkpoint)) return RaceWriteResult.EndpointCheckpoint;

        _context.RaceCheckpoints.Remove(checkpoint);   // split times cascade
        race.Checkpoints.Remove(checkpoint);

        await RenumberAndSaveAsync(race.Checkpoints);
        return RaceWriteResult.Ok;
    }

    /// <summary>
    /// Which of the three a checkpoint is, by the same extremes rule as <see cref="IsEndpoint"/> — and by
    /// IDENTITY, so the answer does not change under the move being validated.
    /// </summary>
    private static CheckpointRole RoleOf(Race race, RaceCheckpoint checkpoint)
    {
        if (race.Checkpoints.MinBy(c => c.ChainageMeters) is { } first && checkpoint.CheckpointId == first.CheckpointId)
            return CheckpointRole.Start;
        if (race.Checkpoints.MaxBy(c => c.ChainageMeters) is { } last && checkpoint.CheckpointId == last.CheckpointId)
            return CheckpointRole.Finish;
        return CheckpointRole.Intermediate;
    }

    /// <summary>
    /// The Start and Finish rows. Identified by chainage EXTREMES, never by seq: seq is renumbered on
    /// every insert/move, so "seq 0" is only incidentally the start.
    ///
    /// Only DELETE consults this now — a race with no line to leave or reach is not a race. Moving them is
    /// allowed and bounded (<see cref="RaceCheckpointBounds"/>).
    /// </summary>
    private static bool IsEndpoint(Race race, RaceCheckpoint checkpoint) =>
        (race.Checkpoints.MinBy(c => c.ChainageMeters) is { } first && checkpoint.CheckpointId == first.CheckpointId)
        || (race.Checkpoints.MaxBy(c => c.ChainageMeters) is { } last && checkpoint.CheckpointId == last.CheckpointId);

    /// <summary>
    /// Renumber seq to follow chainage, and commit — in TWO passes, because <c>(race_ID, seq)</c> is
    /// UNIQUE and a drag past a neighbour SWAPS two seqs.
    ///
    /// Assigning the final numbers in one SaveChanges asks EF to order two updates that transiently
    /// collide on that index (A takes B's seq while B still holds it). EF will not pick an order — there
    /// isn't one — and throws "a circular dependency was detected in the data to be saved". So the rows are
    /// first parked on temporary NEGATIVE seqs, which are never legal and therefore cannot collide with
    /// anything real, flushed, and only then given their final values.
    ///
    /// This was latent until the insert path stopped leaving GAPS in seq (the EF fix-up double-count in
    /// AddCheckpointAsync): the gaps happened to keep the swap from colliding, so the collision only
    /// surfaced once the numbering was made correct. Both halves are the same invariant — seq order IS
    /// chainage order, gaplessly.
    ///
    /// One transaction: a reader must never see the parking negatives.
    /// </summary>
    private async Task RenumberAndSaveAsync(IEnumerable<RaceCheckpoint> checkpoints)
    {
        var ordered = checkpoints.OrderBy(c => c.ChainageMeters).ToList();

        await using var tx = await _context.Database.BeginTransactionAsync();

        for (var i = 0; i < ordered.Count; i++) ordered[i].Seq = -(i + 1);
        await _context.SaveChangesAsync();

        for (var i = 0; i < ordered.Count; i++) ordered[i].Seq = i;
        await _context.SaveChangesAsync();

        await tx.CommitAsync();
    }
}
