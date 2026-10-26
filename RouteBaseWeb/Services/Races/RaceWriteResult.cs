namespace RouteBaseWeb.Services.Races;

/// <summary>Outcome of a write that can fail on authorization or input, without throwing for control flow.</summary>
public enum RaceWriteResult
{
    Ok,
    /// <summary>Race (or the child row) not found, or not owned by the caller.</summary>
    RaceNotFound,
    /// <summary>Course/participant activity not found, or not visible to the caller.</summary>
    ActivityNotVisible,
    /// <summary>That activity is already a participant in this race.</summary>
    DuplicateParticipant,
    /// <summary>Chainage outside the checkpoint's legal range, or on top of another. See <see cref="RaceCheckpointBounds"/>.</summary>
    InvalidChainage,
    /// <summary>Tried to delete the Start or Finish checkpoint. A race needs a line to leave and one to reach.</summary>
    EndpointCheckpoint,
    /// <summary>A colour that is not <c>#rrggbb</c>. See <see cref="RaceColors"/>.</summary>
    InvalidColor,
    /// <summary>An edit policy that is not <c>owner</c> | <c>friends</c> | <c>public</c>.</summary>
    InvalidEditPolicy,
    /// <summary>A rider name longer than the column holds. See <see cref="RaceParticipantCrud"/>.</summary>
    InvalidDisplayName,
}

/// <summary>
/// A checkpoint edit. Every field optional: null means "leave it alone", so a rename does not have to
/// restate the chainage and a drag does not have to restate the name — otherwise a stale field in the
/// client silently overwrites the other.
///
/// <see cref="LegName"/>/<see cref="LegColor"/> describe the interval that FOLLOWS the checkpoint —
/// see <see cref="Models.RaceCheckpoint"/>.
/// </summary>
public sealed record RaceCheckpointEdit
{
    public string? Name { get; init; }
    public double? ChainageMeters { get; init; }
    public string? LegName { get; init; }
    public string? LegColor { get; init; }
}
