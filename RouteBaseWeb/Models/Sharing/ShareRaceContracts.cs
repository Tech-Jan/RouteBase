using RouteBaseWeb.Services.Races;

namespace RouteBaseWeb.Models.Sharing;

/// <summary>
/// The static seed embedded in the race share page (<c>&lt;script id="share-race-data"&gt;</c>), the same
/// island idiom as <see cref="Recording.ShareLiveSeedDto"/>.
///
/// It carries no race DATA at all — the replay arrives from <see cref="ApiBase"/> — only what the client
/// cannot work out for itself: which race it is and where to fetch it.
/// <see cref="ApiBase"/> is the token-scoped read root (<c>/s/{token}/api/race</c>), so the client never has
/// to reconstruct the plaintext token.
///
/// No basemap default: display preferences arrive as the #appSettings island that _ShareLayout renders,
/// and on a share page that island holds the GLOBAL defaults (decision 10) — never the owner's.
/// </summary>
public sealed record ShareRaceSeedDto(int RaceId, string ApiBase);

/// <summary>
/// The race replay as an ANONYMOUS link holder receives it.
///
/// A separate record family from <see cref="RaceReplayPayload"/> rather than the same one with fields
/// blanked — and what it withholds is GEOMETRY, not identity. The results list is published as it stands:
/// a race is a public event, its start list and finishing times are already published, and a shared replay
/// that renamed everyone to "Rider 7" was answering a question nobody asked (decided 2026-07-24).
///
/// What the type still guarantees is the half that is genuinely someone else's: a real rider ships his own
/// full GPS, so a real participant survives only when the grant owner could publish that ride on its own —
/// see <see cref="ShareRaceReader"/>, which drops the rest WHOLE. That rule cannot be expressed as an
/// absent property, so it is pinned by <c>ShareRacePayloadRedactionTests</c> instead.
///
/// Structurally identical to the authenticated payload everywhere else, so the browser runs the SAME
/// race-sim modules over it — the fork adapts it in a few lines, it does not re-render anything.
/// </summary>
public sealed record ShareRaceReplayPayload
{
    public required int RaceId { get; init; }

    /// <summary>The race's own name — the owner's chosen title for what they are publishing, not PII.</summary>
    public required string? RaceName { get; init; }

    /// <summary>The course grid. Owner-owned by construction — see <c>ShareRaceReader</c>'s course gate.</summary>
    public required RaceCourseDto Course { get; init; }

    public required IReadOnlyList<RaceCheckpointDto> Checkpoints { get; init; }

    public required IReadOnlyList<ShareRaceParticipantDto> Participants { get; init; }

    /// <summary>Grade→cost curve, as in the authenticated payload. Numbers about the terrain, not a rider.</summary>
    public IReadOnlyList<double>? CostCurve { get; init; }
    public double CostCurveMinBin { get; init; }
}

/// <summary>
/// One entrant, as the results file has them — the same identity columns the owner's own page shows.
///
/// These fields are the imported start list: a name, a bib, a category, a gender and a club, all of which
/// the event itself published. The share carries them verbatim so a visitor reads the race rather than a
/// row of anonymous numbers. What a visitor still does not get is anyone's GPS but the owner's, which is
/// the reader's rule, not this type's.
/// </summary>
public sealed record ShareRaceParticipantDto
{
    public required int ParticipantId { get; init; }

    /// <summary>"real" or "simulated" — see RaceParticipantKind. Drives which renderer the browser uses.</summary>
    public required string Kind { get; init; }

    /// <summary>As imported. Null for an entrant the results file did not name — the client falls back to the bib.</summary>
    public required string? Name { get; init; }
    public required string? Bib { get; init; }
    public required string? Category { get; init; }
    public required string? Gender { get; init; }
    /// <summary>Imported from the CSV, like the rest — the results table filters and paints on it.</summary>
    public required string? Club { get; init; }

    public required int Seed { get; init; }

    /// <summary>The colour the owner painted this rider, or null for the default. A presentation choice.</summary>
    public string? Color { get; init; }

    /// <summary>Whether the owner emphasised this rider — bigger, in front. A presentation choice too.</summary>
    public bool Emphasised { get; init; }

    /// <summary>Whether the owner hid this rider's call-out — no label, no pinned row, no speed line.</summary>
    public bool CalloutOff { get; init; }

    /// <summary>The published finish time, as in the authenticated payload — a duration, not an identity.</summary>
    public double? OfficialFinishSeconds { get; init; }

    /// <summary>
    /// The published status — see <see cref="RaceFinishStatus"/>. Travels with the rest of the start
    /// list for the same reason it does: a DNF is what the EVENT published about its own race, and a
    /// shared replay that hid it would be showing a different result from the official one.
    /// </summary>
    [WireValues(typeof(RaceFinishStatus))]
    public string? FinishStatus { get; init; }

    /// <summary>The file's original token, verbatim — what the UI shows for an <c>unknown</c> status.</summary>
    public string? FinishStatusRaw { get; init; }

    /// <summary>When he started, relative to the gun. A property of the EVENT's schedule, not of him.</summary>
    public double? StartOffsetSeconds { get; init; }

    /// <summary>The start block's published label.</summary>
    public string? Wave { get; init; }

    /// <summary>Which race within the event he rode — published start-list data, like the category.</summary>
    public string? Discipline { get; init; }

    /// <summary>(checkpointId, elapsedSeconds), ordered by checkpoint. The pacing model's anchors.</summary>
    public required IReadOnlyList<RaceSplitDto> Splits { get; init; }

    /// <summary>
    /// Own geometry — set ONLY for the grant owner's own rides. Another real rider's track never reaches
    /// this DTO at all: <c>ShareRaceReader</c> drops the whole participant, so there is no "GPS omitted"
    /// state to get wrong.
    /// </summary>
    public RaceRealTrackDto? Track { get; init; }
}
