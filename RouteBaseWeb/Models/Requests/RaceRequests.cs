namespace RouteBaseWeb.Models.Requests;

/// <summary>Request bodies for the race slice. Mirrored by the TS types in race-sim/race-api.ts.</summary>
public sealed class CreateRaceRequest
{
    /// <summary>The activity whose track is the course. Must be OWNED by the caller.</summary>
    public int CourseTrackId { get; set; }
    public string RaceName { get; set; } = string.Empty;
    public DateTime? RaceDate { get; set; }
    public string? Visibility { get; set; }
}

/// <summary>Fields of the race itself that can be changed after creation.</summary>
public sealed class UpdateRaceRequest
{
    /// <summary>
    /// The real effort the cost curve is fitted from; null clears it (fall back to the course). Settable
    /// after creation on purpose — the natural reference is often a real rider you add LATER, and on a
    /// drawn or merged course there is no usable timing until you do. See Race.ReferenceTrackId.
    /// </summary>
    public int? ReferenceTrackId { get; set; }
}

/// <summary>Set who may EDIT the race — <c>owner</c> | <c>friends</c> | <c>public</c>. Owner-only.</summary>
public sealed class SetEditPolicyRequest
{
    public string Policy { get; set; } = string.Empty;
}

public sealed class AddCheckpointRequest
{
    public string Name { get; set; } = string.Empty;
    public double ChainageMeters { get; set; }
}

/// <summary>
/// Every field nullable: null means "leave it alone". A rename must not have to restate the chainage,
/// and a drag must not have to restate the name — otherwise a stale field in the client silently
/// overwrites the other.
/// </summary>
public sealed class UpdateCheckpointRequest
{
    public string? Name { get; set; }
    public double? ChainageMeters { get; set; }
    /// <summary>Name/colour of the interval that FOLLOWS this checkpoint — the leg. See RaceCheckpoint.</summary>
    public string? LegName { get; set; }
    public string? LegColor { get; set; }
}

public sealed class AddRealParticipantRequest
{
    /// <summary>Own or a friend's activity. Gated on VIEW rights, not ownership.</summary>
    public int TrackId { get; set; }
    public string? DisplayName { get; set; }
}

/// <summary>
/// What a rider is CALLED in this race — "Marco", not "Morning ride".
///
/// Blank means "back to the ride's own name", which is the same fallback the add uses. The client sends
/// the box's raw value including the empty string: the server owns that rule, and a client that
/// substituted the activity name itself would be a second copy of it, free to drift.
///
/// The participant is named in the BODY, like the paint and the emphasis above, rather than as a second
/// route segment — <c>(int raceId, int participantId)</c> is a transposition waiting to happen, and the
/// pin in <c>BackendConventionTests</c> exists to keep that family from growing.
/// </summary>
public sealed class RenameParticipantRequest
{
    public int ParticipantId { get; set; }
    public string? DisplayName { get; set; }
}

/// <summary>
/// Paint a group of riders. The results table filters (gender, division, club — and filters COMPOSE)
/// and paints the whole filtered set in one call, so this list is as large as the field.
/// </summary>
public sealed class SetParticipantColorsRequest
{
    public List<int> ParticipantIds { get; set; } = [];
    /// <summary>A <c>#rrggbb</c> colour, or null to clear back to the default. Validated server-side.</summary>
    public string? Color { get; set; }
}

/// <summary>
/// Emphasise (or stop emphasising) a group of riders — bigger dots, drawn in front. Same filtered set as
/// the paint above, its own request because the two choices are independent.
/// </summary>
public sealed class SetParticipantEmphasisRequest
{
    public List<int> ParticipantIds { get; set; } = [];
    public bool Emphasised { get; set; }
}

/// <summary>
/// Hide (or restore) a group's call-out: the map name label, the pinned sidebar row and the speed-chart
/// line. Phrased as the OFF switch because that is the state worth storing — see
/// <see cref="Models.RaceParticipant.CalloutOff"/>.
/// </summary>
public sealed class SetParticipantCalloutOffRequest
{
    public List<int> ParticipantIds { get; set; } = [];
    public bool CalloutOff { get; set; }
}

/// <summary>
/// Which column feeds which field — the half of the import request that is the MAPPING.
///
/// Its own class because three requests now carry it: the import, the preview, and saving it as a named
/// profile. One shape means the controller has one <c>MappingOf</c>, and a field added here reaches all
/// three or none — which is the failure mode when a status column exists on the import and not on the
/// profile that is supposed to reproduce it.
/// </summary>
public class RaceCsvMappingRequest
{
    /// <summary>
    /// NULLABLE, and that is load-bearing rather than lax.
    ///
    /// The FIRST preview arrives with nothing mapped — the user has only just picked the file — and
    /// <c>RaceImportPreview</c> is built to fill the blanks from its own alias guesses. A non-nullable
    /// string here gets an IMPLICIT [Required] from nullable reference types, and form binding converts
    /// an empty value to null, so that first call was rejected by model validation before the action ran:
    /// "NameColumn: The NameColumn field is required." The guess-filling path was unreachable, and the
    /// panel could never show a preview at all.
    ///
    /// The requirement is real, but it belongs to the IMPORT, not to the request: the controller rejects a
    /// blank one there with a message that says what to do about it.
    /// </summary>
    public string? NameColumn { get; set; }
    /// <summary>Required in the default mode; optional under <see cref="PerLegTimes"/>, where the legs
    /// produce the finish themselves.</summary>
    public string? FinishColumn { get; set; }
    public string? BibColumn { get; set; }
    public string? CategoryColumn { get; set; }
    public string? GenderColumn { get; set; }
    public string? ClubColumn { get; set; }
    /// <summary>The column holding the file's published status. See RaceCsvMapping.StatusColumn — mapping
    /// it both rescues status-only rows and lets a published DNF outrank a finish-mat time.</summary>
    public string? StatusColumn { get; set; }
    /// <summary>Gun and net columns. Mapping BOTH derives each rider's start offset as gun − net, with no
    /// extra user input — see RaceCsvMapping.GunTimeColumn.</summary>
    public string? GunTimeColumn { get; set; }
    public string? NetTimeColumn { get; set; }
    /// <summary>An explicit start offset, which wins over the gun/net derivation.</summary>
    public string? StartOffsetColumn { get; set; }
    /// <summary>The start block's label. Imported as text; the user gives it a number afterwards.</summary>
    public string? WaveColumn { get; set; }
    /// <summary>Which race within the event — eleven of them in the Schauinsland file.</summary>
    public string? DisciplineColumn { get; set; }
    /// <summary>checkpointId → the CSV column holding that checkpoint's time.</summary>
    public Dictionary<int, string>? SplitColumns { get; set; }

    /// <summary>
    /// True when the mapped columns hold LEG DURATIONS (a relay/stage results file) rather than
    /// elapsed-from-start. See RaceCsvMapping.PerLegTimes — this is not a display preference, it
    /// changes what the numbers mean.
    /// </summary>
    public bool PerLegTimes { get; set; }
}

/// <summary>The mapping plus the file it applies to. Multipart — see <see cref="File"/>.</summary>
public sealed class ImportRaceCsvRequest : RaceCsvMappingRequest
{
    /// <summary>
    /// The results file itself, as MULTIPART — never a JSON string.
    ///
    /// The browser used to read it with <c>Blob.text()</c>, which is unconditionally UTF-8, so a
    /// Windows-1252 export arrived with U+FFFD in every accented name and no layer below could tell.
    /// Sending bytes is what lets <see cref="Services.Races.RaceCsvDecoder"/> make that call, and it is
    /// what let the browser stop parsing CSV at all.
    /// </summary>
    public IFormFile? File { get; set; }
}

/// <summary>
/// One offset per start block, typed once by the user.
///
/// A dictionary keyed by the block's own LABEL, because that is the only thing linking the riders that
/// share it — the label is free text the file wrote (<c>Startblock 1: 10 Uhr bis 11 Uhr</c>) and carries
/// no machine-readable time. A null value clears the block back to a mass start.
/// </summary>
public sealed class SetRaceWaveOffsetsRequest
{
    public Dictionary<string, double?> Offsets { get; set; } = [];
}

/// <summary>The mapping plus the name to remember it under. Per USER, not per race.</summary>
public sealed class SaveRaceImportProfileRequest : RaceCsvMappingRequest
{
    /// <summary>
    /// What the user calls this mapping — "Datasport", "RaceResult".
    ///
    /// Nullable for the same reason as <see cref="RaceCsvMappingRequest.NameColumn"/>: a non-nullable
    /// string carries an implicit [Required], which would answer a blank name with the framework's
    /// message instead of <c>RaceImportProfileStore.NormalizeName</c>'s — the one that actually says what
    /// a legal name looks like.
    /// </summary>
    public string? Name { get; set; }
}
