namespace RouteBaseWeb.Services.Races;

/// <summary>
/// Everything the browser needs to replay a race. Mirrored by the TS types in race-sim/race-api.ts —
/// the two change together.
///
/// The shape IS the scaling argument. The course grid is sent ONCE and shared by the whole simulated
/// field; each simulated rider adds only his checkpoint times (a handful of numbers), and the browser
/// runs the pacing model over the shared grid to get his ride. Real riders are the only ones that ship
/// geometry — and there are single digits of them.
///
/// Sending 424 pre-computed position tracks instead would be tens of megabytes for the same picture.
/// </summary>
public sealed record RaceReplayPayload
{
    public required int RaceId { get; init; }
    public required string? RaceName { get; init; }
    public required RaceCourseDto Course { get; init; }
    public required IReadOnlyList<RaceCheckpointDto> Checkpoints { get; init; }
    public required IReadOnlyList<RaceParticipantDto> Participants { get; init; }

    /// <summary>
    /// The grade→cost curve as seconds-per-metre by 1% grade bin, fitted from a real effort over this
    /// course — the race's reference effort if it names one, else the course activity itself. Null when
    /// neither carries usable timing (a drawn or merged route has no timestamps); the browser then falls
    /// back to a default curve. See pacing-model.ts fitCostCurve and Race.ReferenceTrackId.
    /// </summary>
    public IReadOnlyList<double>? CostCurve { get; init; }
    public double CostCurveMinBin { get; init; }
}

/// <summary>Columnar on purpose: 3,892 nodes as 4 flat arrays, not 3,892 objects.</summary>
public sealed record RaceCourseDto
{
    public required double[] Lat { get; init; }
    public required double[] Lng { get; init; }
    public required double[] Elevation { get; init; }
    public required double[] GradePercent { get; init; }
    public required double StepMeters { get; init; }
    public required double TotalMeters { get; init; }
}

public sealed record RaceCheckpointDto
{
    public required int CheckpointId { get; init; }
    public required int Seq { get; init; }
    public required string? Name { get; init; }
    public required double ChainageMeters { get; init; }
    /// <summary>Node index into the course grid — what the pacing model anchors on.</summary>
    public required int Node { get; init; }
    /// <summary>Name/colour of the interval that FOLLOWS this checkpoint — the leg. See RaceCheckpoint.</summary>
    public string? LegName { get; init; }
    public string? LegColor { get; init; }
}

public sealed record RaceParticipantDto
{
    public required int ParticipantId { get; init; }
    /// <summary>"real" or "simulated" — see RaceParticipantKind.</summary>
    public required string Kind { get; init; }
    public required string? Name { get; init; }
    public required string? Bib { get; init; }
    public required string? Category { get; init; }
    public required string? Gender { get; init; }
    /// <summary>Imported from the CSV. Shipped so the results table can filter and paint on it.</summary>
    public required string? Club { get; init; }
    public required int Seed { get; init; }

    /// <summary>The painted marker colour, or null for the default. See RaceParticipant.Color.</summary>
    public string? Color { get; init; }

    /// <summary>
    /// Draw him bigger and in front. A plain bool on the wire even though the column is nullable — null
    /// and false mean the same thing to a reader, and only the writer needs the distinction.
    /// </summary>
    public bool Emphasised { get; init; }

    /// <summary>
    /// His call-out is hidden: no name label, no pinned sidebar row, no speed-chart line. A plain bool
    /// for the same reason as <see cref="Emphasised"/> — null and false read identically.
    /// </summary>
    public bool CalloutOff { get; init; }

    /// <summary>
    /// The finish time the results file PUBLISHED. NOT what he is replayed on under per-leg times — the
    /// accumulated leg sum is, and it is his last split. Shipped so the results table can show both
    /// totals and colour the gap between them.
    /// </summary>
    public double? OfficialFinishSeconds { get; init; }

    /// <summary>
    /// The file's published status, normalized — see <see cref="RaceFinishStatus"/>. Null when the file
    /// carried none, which is what makes the derived rule ("has a split at the finish") the fallback
    /// rather than a second source of truth.
    ///
    /// [WireValues] stamps the closed set into the OpenAPI document, so the browser sees a literal union
    /// and a comparison against a misspelled status fails tsc instead of matching nothing at runtime.
    /// </summary>
    [Models.WireValues(typeof(RaceFinishStatus))]
    public string? FinishStatus { get; init; }

    /// <summary>The file's original token, verbatim. What the UI shows for an <c>unknown</c> status,
    /// rather than a label we would be inventing. See RaceParticipant.FinishStatusRaw.</summary>
    public string? FinishStatusRaw { get; init; }

    /// <summary>
    /// Seconds after the gun that he started. Null (and 0) is a mass start.
    ///
    /// His SPLITS are unaffected — they stay measured from his own start, which is what the results are
    /// decided on. This is only what the replay's GUN-time basis shifts him by, so a time trial can be
    /// watched as it happened on the road instead of as 1,274 riders leaving together.
    /// </summary>
    public double? StartOffsetSeconds { get; init; }

    /// <summary>The start block's label, as imported. What the wave editor groups riders by.</summary>
    public string? Wave { get; init; }

    /// <summary>Which race within the event he rode. A facet, exactly like category. See RaceParticipant.</summary>
    public string? Discipline { get; init; }

    /// <summary>(checkpointId, elapsedSeconds), ordered by checkpoint. The model's anchors.</summary>
    public required IReadOnlyList<RaceSplitDto> Splits { get; init; }

    /// <summary>Set for REAL participants only — his own GPS, which is what his marker rides.</summary>
    public RaceRealTrackDto? Track { get; init; }
}

public sealed record RaceSplitDto
{
    public required int CheckpointId { get; init; }
    public required double ElapsedSeconds { get; init; }
}

/// <summary>A real rider's own geometry. Columnar, same reason as the course.</summary>
public sealed record RaceRealTrackDto
{
    public required double[] Lat { get; init; }
    public required double[] Lng { get; init; }
    public required double[] ElapsedSeconds { get; init; }
    public required double[] ChainageMeters { get; init; }
}
