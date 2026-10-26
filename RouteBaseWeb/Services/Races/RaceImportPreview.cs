using RouteBaseWeb.Models;

namespace RouteBaseWeb.Services.Races;

/// <summary>
/// Builds the import preview. Pure — no DB, no HTTP — and it goes through
/// <see cref="RaceCsvImporter.Build"/> rather than re-deriving anything, which is the whole point: a
/// preview that computed its numbers differently from the import would be a second implementation to
/// keep honest, and the first place it drifted is the place the user trusted it most.
/// </summary>
public static class RaceImportPreview
{
    /// <summary>How many data rows the preview builds. Five is enough to see a shifted column mapping.</summary>
    public const int RowLimit = 5;

    /// <summary>
    /// A published total and a computed finish this far apart are not rounding. 25% over a 2:43 race is
    /// 40 minutes — well past any legitimate leg-overlap, and the exact signature of a cumulative file
    /// read as per-leg (which sums to nearly 4× on Black Forest).
    /// </summary>
    private const double DisagreementFactor = 1.25;

    public static RaceImportPreviewDto Build(
        RaceCsvText text, RaceCsvMapping? proposed, IReadOnlyList<RaceCheckpoint> checkpoints, RaceId raceId)
    {
        var table = RaceCsvParser.Parse(text.Text);

        var fields = RaceCsvVocabulary.Fields
            .Select(f => new RaceCsvFieldDto
            {
                Field = f.Field,
                Label = f.Label,
                Required = f.Required,
                Hint = f.Hint,
                Guess = RaceCsvVocabulary.Guess(table.Header, f.Field),
            })
            .ToList();

        // The FIRST preview arrives with nothing mapped — the user has only just picked the file. Filling
        // the blanks from the guesses is what makes that call show real rows instead of an error, which is
        // what turns the preview into the thing you check rather than the thing you dismiss.
        var mapping = WithGuesses(proposed, fields);

        var head = new RaceCsvTable(table.Header, table.Rows.Take(RowLimit).ToList(), table.Delimiter);
        var warnings = new List<string>();
        List<RaceImportPreviewRowDto> rows = [];
        IReadOnlyList<string> skipped = [];

        try
        {
            var built = RaceCsvImporter.Build(head, mapping, checkpoints, raceId);
            var nameOf = checkpoints.ToDictionary(c => c.CheckpointId, c => c.CheckpointName);
            rows = built.Participants.Select(p => Row(p, nameOf)).ToList();
            skipped = built.Skipped;
        }
        catch (ArgumentException ex)
        {
            // An unmappable file is a preview RESULT, not a failure: the header and the guesses are
            // exactly what the user needs in order to fix it, and throwing would hide both.
            warnings.Add(ex.Message);
        }

        warnings.AddRange(MappingWarnings(rows, mapping, head, checkpoints));

        return new RaceImportPreviewDto
        {
            Delimiter = table.Delimiter.ToString(),
            Encoding = text.Encoding,
            Header = table.Header,
            RowCount = table.Rows.Count,
            Fields = fields,
            SplitColumns = SplitPickers(checkpoints, table.Header, mapping.PerLegTimes),
            Rows = rows,
            Skipped = skipped,
            Warnings = warnings,
        };
    }

    /// <summary>Every blank column in the proposed mapping filled from this file's best guess.</summary>
    private static RaceCsvMapping WithGuesses(RaceCsvMapping? proposed, IReadOnlyList<RaceCsvFieldDto> fields)
    {
        string? Guess(string field) => fields.FirstOrDefault(f => f.Field == field)?.Guess;
        string? Or(string? chosen, string field) => string.IsNullOrWhiteSpace(chosen) ? Guess(field) : chosen;

        return new RaceCsvMapping
        {
            NameColumn = Or(proposed?.NameColumn, RaceCsvFields.Name) ?? string.Empty,
            FinishColumn = Or(proposed?.FinishColumn, RaceCsvFields.Finish),
            BibColumn = Or(proposed?.BibColumn, RaceCsvFields.Bib),
            CategoryColumn = Or(proposed?.CategoryColumn, RaceCsvFields.Category),
            GenderColumn = Or(proposed?.GenderColumn, RaceCsvFields.Gender),
            ClubColumn = Or(proposed?.ClubColumn, RaceCsvFields.Club),
            StatusColumn = Or(proposed?.StatusColumn, RaceCsvFields.Status),
            GunTimeColumn = Or(proposed?.GunTimeColumn, RaceCsvFields.GunTime),
            NetTimeColumn = Or(proposed?.NetTimeColumn, RaceCsvFields.NetTime),
            StartOffsetColumn = Or(proposed?.StartOffsetColumn, RaceCsvFields.StartOffset),
            WaveColumn = Or(proposed?.WaveColumn, RaceCsvFields.Wave),
            DisciplineColumn = Or(proposed?.DisciplineColumn, RaceCsvFields.Discipline),
            SplitColumns = proposed?.SplitColumns ?? new Dictionary<int, string>(),
            PerLegTimes = proposed?.PerLegTimes ?? false,
        };
    }

    /// <summary>
    /// A picker per checkpoint that takes a column, labelled and guessed for the CURRENT mode.
    ///
    /// Which checkpoints those are is itself mode-dependent, and subtly: under per-leg times the Finish
    /// ends a section like any other checkpoint and takes a column here, while the "Finish time" picker
    /// above means the PUBLISHED TOTAL. Under elapsed times the Finish is fed by that picker instead, so
    /// it must not appear twice — a second column mapped to the same checkpoint is a primary-key
    /// collision that 500s the whole import.
    ///
    /// The guess hints on the SECTION's own name (one checkpoint back) under per-leg, never on the
    /// checkpoint's — see RaceSplitColumnDto for the import that cost.
    /// </summary>
    private static List<RaceSplitColumnDto> SplitPickers(
        IReadOnlyList<RaceCheckpoint> checkpoints, IReadOnlyList<string> header, bool perLeg)
    {
        var ordered = checkpoints.OrderBy(c => c.Seq).ToList();
        if (ordered.Count < 2) return [];

        var targets = perLeg ? ordered.Skip(1).ToList() : ordered.Skip(1).SkipLast(1).ToList();
        var pickers = new List<RaceSplitColumnDto>(targets.Count);

        for (var i = 0; i < targets.Count; i++)
        {
            var checkpoint = targets[i];
            // targets[i] is ordered[i + 1] in both modes, so the checkpoint BEFORE it is ordered[i].
            var previous = ordered[i];
            var legName = perLeg ? previous.LegName : null;
            var km = $"{checkpoint.ChainageMeters / 1000:0.0} km";
            var label = perLeg
                ? legName ?? $"Section {i + 1} — ends at {Nonblank(checkpoint.CheckpointName) ?? km}"
                : Nonblank(checkpoint.CheckpointName) ?? $"CP {checkpoint.Seq}";

            // An unguessed picker is a question; a confidently wrong one is a silent corruption. So a
            // per-leg section with no name gets NO guess rather than a guess off the checkpoint's name.
            var hint = perLeg ? legName : Nonblank(checkpoint.CheckpointName);
            pickers.Add(new RaceSplitColumnDto
            {
                CheckpointId = checkpoint.CheckpointId.Value,
                Label = label,
                Guess = hint == null ? null : MatchColumn(header, hint),
            });
        }
        return pickers;
    }

    /// <summary>Exact header match first, then a substring — the same order the field vocabulary uses.</summary>
    private static string? MatchColumn(IReadOnlyList<string> header, string hint) =>
        header.FirstOrDefault(h => h.Equals(hint, StringComparison.OrdinalIgnoreCase))
        ?? header.FirstOrDefault(h => h.Contains(hint, StringComparison.OrdinalIgnoreCase));

    private static string? Nonblank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static RaceImportPreviewRowDto Row(
        RaceCsvParticipant p, IReadOnlyDictionary<CheckpointId, string?> nameOf) =>
        new()
        {
            Line = p.Line,
            Name = p.Participant.DisplayName,
            Bib = p.Participant.Bib,
            Status = p.Participant.FinishStatus,
            StatusRaw = p.Participant.FinishStatusRaw,
            // His LAST split is what he is replayed on — and under per-leg times that is the chained sum,
            // which is the number that reads 10:27:26 for a 2:43:18 race.
            ComputedFinishSeconds = p.Splits.Count == 0 ? null : p.Splits[^1].ElapsedSeconds,
            PublishedFinishSeconds = p.Participant.OfficialFinishSeconds,
            Splits = p.Splits.Select(s => new RaceImportPreviewSplitDto
            {
                CheckpointId = s.CheckpointId.Value,
                CheckpointName = nameOf.GetValueOrDefault(s.CheckpointId),
                ElapsedSeconds = s.ElapsedSeconds,
            }).ToList(),
        };

    /// <summary>
    /// The three mistakes that are silent, systematic and expensive — named from the previewed rows
    /// rather than guessed at from the header.
    /// </summary>
    private static IEnumerable<string> MappingWarnings(
        IReadOnlyList<RaceImportPreviewRowDto> rows, RaceCsvMapping mapping,
        RaceCsvTable head, IReadOnlyList<RaceCheckpoint> checkpoints)
    {
        // 1. Cumulative columns read as per-leg. Black Forest's row 1 sums to 10:27:26 against a published
        //    2:43:18 — the accumulator chained five columns that were already elapsed-from-start.
        if (mapping.PerLegTimes)
        {
            var overshot = rows.FirstOrDefault(r =>
                r.PublishedFinishSeconds is > 0 && r.ComputedFinishSeconds is { } c
                && c > r.PublishedFinishSeconds * DisagreementFactor);
            if (overshot != null)
                yield return
                    $"Line {overshot.Line}: the section times add up to {Clock(overshot.ComputedFinishSeconds)}, "
                    + $"but the file publishes {Clock(overshot.PublishedFinishSeconds)}. These columns look "
                    + "CUMULATIVE (each already measured from the start) — untick “that section only”.";
        }
        else
        {
            // 2. Per-leg columns read as elapsed. Elapsed times only ever increase along the course; leg
            //    durations do not, so a decrease is the giveaway.
            var backwards = rows.FirstOrDefault(r => r.Splits.Zip(r.Splits.Skip(1)).Any(pair => pair.Second.ElapsedSeconds < pair.First.ElapsedSeconds));
            if (backwards != null)
                yield return
                    $"Line {backwards.Line}: the split times do not increase along the course. They look like "
                    + "SECTION durations rather than times from the start — tick “that section only”.";
        }

        // 3. A bare decimal in a time column. Boston 2014 publishes decimal MINUTES (`85.25`), and
        //    ParseElapsedSeconds reads a bare number as SECONDS — a 60× error that looks like a plausible
        //    number rather than a broken one.
        if (!string.IsNullOrWhiteSpace(mapping.FinishColumn)
            && BareDecimal(head, mapping.FinishColumn)
            && rows.Any(r => r.PublishedFinishSeconds is > 0 and < 600))
        {
            yield return
                "The finish column holds bare numbers, which are read as SECONDS. If this file publishes "
                + "decimal MINUTES (Boston writes `85.25` for 1:25:15), every time will be 60× too short.";
        }

        // 4. A status token we could not read. Not an error — it imports as Unknown and keeps the original
        //    token — but the user should hear it from us rather than discover it in the results table.
        var unreadable = rows.Where(r => r.Status == RaceFinishStatus.Unknown).Select(r => r.StatusRaw).Distinct().ToList();
        if (unreadable.Count > 0)
            yield return
                $"Status token(s) not recognised: {string.Join(", ", unreadable)}. They import as “Unknown” "
                + "and keep the file's own wording.";

        // 5. Checkpoints with no column at all. Legal (a file need not time every checkpoint) but it is
        //    also what a mapping shifted by one looks like, so it is worth saying once.
        var mapped = mapping.SplitColumns.Count;
        var mappable = checkpoints.Count - 1;   // the start needs none — everyone leaves at 0
        if (mapped > 0 && mapped < mappable)
            yield return $"{mappable - mapped} of {mappable} checkpoints have no column mapped.";
    }

    /// <summary>Is every non-blank cell in this column a bare number — no <c>:</c> anywhere?</summary>
    private static bool BareDecimal(RaceCsvTable table, string column)
    {
        var index = -1;
        for (var i = 0; i < table.Header.Count; i++)
            if (string.Equals(table.Header[i], column, StringComparison.OrdinalIgnoreCase)) index = i;
        if (index < 0) return false;

        var seen = false;
        foreach (var row in table.Rows)
        {
            if (index >= row.Count) continue;
            var cell = row[index].Trim();
            if (cell.Length == 0) continue;
            if (cell.Contains(':', StringComparison.Ordinal)) return false;
            seen = true;
        }
        return seen;
    }

    /// <summary>h:mm:ss for a warning message. Null reads as a dash rather than as 0:00:00.</summary>
    private static string Clock(double? seconds) =>
        seconds is { } s ? TimeSpan.FromSeconds(s).ToString(@"h\:mm\:ss") : "—";
}
