namespace RouteBaseWeb.Services.Races;

/// <summary>
/// What the server made of the uploaded file BEFORE anything is written.
///
/// This is the single change that removes the most whole classes of bug, and the reason is that the
/// browser stops parsing CSV entirely: the delimiter sniff, the quote rule and the encoding all now
/// happen once, on the bytes, in the code that will actually do the import.
///
/// The load-bearing part is <see cref="Rows"/>. Heuristics cannot tell a cumulative split column from a
/// per-leg one, and getting it wrong turns a 2:43:18 race into 10:27:26 — silently, for the whole field.
/// Five rows showing the COMPUTED finish next to the PUBLISHED one make that unmissable, and they catch
/// the Boston decimal-minutes trap and a column mapping shifted by one at the same time. The user's own
/// eyes are a better oracle than any guess we could write.
/// </summary>
public sealed record RaceImportPreviewDto
{
    /// <summary>The sniffed delimiter, as the literal character — <c>","</c>, <c>";"</c>, a tab, <c>"|"</c>.</summary>
    public required string Delimiter { get; init; }

    /// <summary>What the bytes turned out to be. See <see cref="RaceCsvDecoder"/>.</summary>
    public required string Encoding { get; init; }

    public required IReadOnlyList<string> Header { get; init; }

    /// <summary>Data rows in the file, header excluded. The count the import is about to work through.</summary>
    public required int RowCount { get; init; }

    /// <summary>The canonical fields, their accepted spellings and this file's best guess for each.</summary>
    public required IReadOnlyList<RaceCsvFieldDto> Fields { get; init; }

    /// <summary>One split picker per checkpoint that takes a column, already labelled and guessed.</summary>
    public required IReadOnlyList<RaceSplitColumnDto> SplitColumns { get; init; }

    /// <summary>The first few rows as the importer would build them under the proposed mapping.</summary>
    public required IReadOnlyList<RaceImportPreviewRowDto> Rows { get; init; }

    /// <summary>Why a previewed row would not become a participant — line-numbered, verbatim from the importer.</summary>
    public required IReadOnlyList<string> Skipped { get; init; }

    /// <summary>
    /// Things worth saying out loud about the mapping, computed from the previewed rows: the per-leg
    /// disagreement, a finish that lands implausibly far from the published total, an unreadable status.
    /// </summary>
    public required IReadOnlyList<string> Warnings { get; init; }
}

/// <summary>One canonical field, served to the panel so the browser holds no copy of the alias table.</summary>
public sealed record RaceCsvFieldDto
{
    /// <summary>One of <see cref="RaceCsvFields"/>.</summary>
    public required string Field { get; init; }
    public required string Label { get; init; }
    public required bool Required { get; init; }
    /// <summary>One sentence on what mapping this field buys — shown under the picker.</summary>
    public required string Hint { get; init; }
    /// <summary>The header column this file most likely means, or null. The user overrides every one.</summary>
    public required string? Guess { get; init; }
}

/// <summary>
/// One split picker: which checkpoint it feeds, what to call it, and which column it probably wants.
///
/// Labelled and guessed HERE rather than in the browser, and the reason is a bug that cost a whole
/// import. Under per-leg times a column holds the duration of the SECTION ending at a checkpoint, and a
/// section is named on the checkpoint it FOLLOWS — one row back. Hinting on the checkpoint's own name
/// instead shifted every Ski to Sea column by one and fed the published TOTAL in as the last leg, which
/// doubled 494 riders' finish times. That rule belongs next to the checkpoints, not next to the DOM.
/// </summary>
public sealed record RaceSplitColumnDto
{
    public required int CheckpointId { get; init; }
    /// <summary>The picker's caption — the section's name under per-leg times, the checkpoint's otherwise.</summary>
    public required string Label { get; init; }
    /// <summary>The header column this picker probably wants, or null. Never the published total.</summary>
    public required string? Guess { get; init; }
}

/// <summary>One previewed row, fully built — not a raw cell dump.</summary>
public sealed record RaceImportPreviewRowDto
{
    /// <summary>1-based, header included, so it matches what the user sees in a text editor.</summary>
    public required int Line { get; init; }
    public required string? Name { get; init; }
    public required string? Bib { get; init; }

    /// <summary>The normalized status, or null when the file published none. See <see cref="RaceFinishStatus"/>.</summary>
    [Models.WireValues(typeof(RaceFinishStatus))]
    public required string? Status { get; init; }

    /// <summary>The file's own status token, verbatim.</summary>
    public required string? StatusRaw { get; init; }

    /// <summary>
    /// What this rider would actually be REPLAYED on: his last split under the proposed mapping. Under
    /// per-leg times that is the chained leg sum, which is exactly the number that reads 10:27:26 when a
    /// cumulative file is imported as per-leg.
    /// </summary>
    public required double? ComputedFinishSeconds { get; init; }

    /// <summary>What the file's finish column says. The two agreeing is the whole point of showing both.</summary>
    public required double? PublishedFinishSeconds { get; init; }

    /// <summary>Each mapped checkpoint's computed elapsed-from-start, in course order.</summary>
    public required IReadOnlyList<RaceImportPreviewSplitDto> Splits { get; init; }
}

public sealed record RaceImportPreviewSplitDto
{
    public required int CheckpointId { get; init; }
    public required string? CheckpointName { get; init; }
    public required double ElapsedSeconds { get; init; }
}
