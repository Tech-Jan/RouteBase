using RouteBaseWeb.Models;

namespace RouteBaseWeb.Services.Races;

/// <summary>
/// What the API returns for the race CRUD surface — never an EF entity.
///
/// An entity carries navigation properties, and EF's relationship fix-up populates the BACK-references
/// as soon as the parent is tracked: Race → Checkpoints → Race is then a cycle System.Text.Json refuses
/// to serialize (it 500s AFTER the write has already committed, which is the worst possible shape of
/// failure). The entities also carry [JsonIgnore] on those back-refs as a second line of defence.
/// </summary>
public sealed record RaceSummaryDto
{
    public required int RaceId { get; init; }
    public required string? RaceName { get; init; }
    public required string? RaceDate { get; init; }
    [WireValues(typeof(Visibilities))]
    public required string Visibility { get; init; }
    public required int CheckpointCount { get; init; }
    public required int ParticipantCount { get; init; }

    /// <summary>
    /// The course activity — the list page renders its shape as a card minimap. Null when the viewer may not
    /// see the course (AUTHZ-P2): a race the caller can see does not authorize its course's geometry, so the
    /// card withholds the track id exactly as the replay 404s it.
    /// </summary>
    public required int? CourseTrackId { get; init; }

    /// <summary>
    /// The RACE's length (metres) for the card's Distance KPI — Finish chainage minus Start chainage, which
    /// is the ride's full geometry only until someone moves an endpoint. Not the course activity's
    /// <c>TotalDistance</c>: that is the source's own scalar and disagrees with the geometry chainage is
    /// measured in.
    ///
    /// Null if the race has no checkpoints OR the viewer may not see the course (AUTHZ-P2, paired with
    /// <see cref="CourseTrackId"/> — with default endpoints this number is the private ride's length).
    /// </summary>
    public required double? RaceDistanceMeters { get; init; }
}

/// <summary>
/// One entry in a "choose a ride" picker (course on /Races, rider or reference effort on /Races/Replay).
/// Three columns — the views read nothing else, so the pages never load (nor change-track) whole
/// Activity entities.
/// </summary>
public sealed record RaceRideOption(int TrackId, string? TrackName, double? TotalDistance);

public sealed record RaceCheckpointRowDto
{
    public required int CheckpointId { get; init; }
    public required int Seq { get; init; }
    public required string? Name { get; init; }
    public required double ChainageMeters { get; init; }
    /// <summary>Name/colour of the interval that FOLLOWS this checkpoint — the leg. See RaceCheckpoint.</summary>
    public string? LegName { get; init; }
    public string? LegColor { get; init; }
}

public sealed record RaceParticipantRowDto
{
    public required int ParticipantId { get; init; }
    public required string Kind { get; init; }
    public required string? Name { get; init; }
    public required string? Bib { get; init; }
    public required int? TrackId { get; init; }
}

/// <summary>
/// What an import did. A named DTO, not an anonymous object: an unnamed contract cannot be grepped,
/// typed or pinned — and this one carries a number the user must actually see.
/// </summary>
public sealed record RaceImportResultDto
{
    /// <summary>Rows that became a participant — every row with at least one usable time, DNFs included.</summary>
    public required int Imported { get; init; }

    /// <summary>
    /// How many of them actually FINISHED (they have a split at the Finish checkpoint).
    ///
    /// Not the same number as <see cref="Imported"/>, and the panel used to report the two as one: it said
    /// "Imported 508 finishers" for a file in which 87 teams never reached the line. RaceCsvImportResult
    /// has computed this correctly all along — nothing read it.
    /// </summary>
    public required int Finishers { get; init; }

    public required IReadOnlyList<string> Skipped { get; init; }

    /// <summary>
    /// Riders whose legs sum PAST their published finish (a forced start overlapped two legs). They are
    /// replayed on their leg times — slower, and truer. Reported so the number is visible, never hidden.
    /// </summary>
    public required int OverlappedLegCount { get; init; }
}

/// <summary>How many riders a paint call actually coloured.</summary>
public sealed record RacePaintResultDto
{
    public required int Painted { get; init; }
}

/// <summary>How many riders an emphasis call actually changed.</summary>
public sealed record RaceEmphasisResultDto
{
    public required int Updated { get; init; }
}

/// <summary>How many riders a call-out call actually changed.</summary>
public sealed record RaceCalloutResultDto
{
    public required int Updated { get; init; }
}

/// <summary>How many riders were removed when the field was emptied.</summary>
public sealed record RaceClearFieldResultDto
{
    public required int Removed { get; init; }
}

/// <summary>How many entrants a wave-offset assignment moved.</summary>
public sealed record RaceWaveResultDto
{
    public required int Updated { get; init; }
}
