using RouteBaseWeb.Models;

namespace RouteBaseWeb.Services.Races;

/// <summary>
/// Which CSV column feeds which field. Every race site exports a different header, so the mapping is
/// data supplied by the user — never a hard-coded schema.
/// </summary>
public sealed record RaceCsvMapping
{
    /// <summary>Required — a participant with no name is not a participant.</summary>
    public required string NameColumn { get; init; }

    /// <summary>
    /// The column holding the FINISH time — elapsed from the start, i.e. the rider's total. Blank/DNF
    /// values are kept as DNF rows (the rider gets whatever splits he does have, and the model holds him
    /// at his last checkpoint).
    ///
    /// Required in the default mode. **Optional under <see cref="PerLegTimes"/>**, where the legs produce
    /// the finish themselves: there it is the rider's PUBLISHED total, which is not an authority — the
    /// leg sum wins where the two disagree, and this is consulted only to place a rider whose legs have
    /// holes. See <see cref="RaceLegAccumulator"/>. The last leg's own duration comes from
    /// <see cref="SplitColumns"/> like every other leg.
    /// </summary>
    public string? FinishColumn { get; init; }

    public string? BibColumn { get; init; }
    public string? CategoryColumn { get; init; }
    public string? GenderColumn { get; init; }
    public string? ClubColumn { get; init; }

    /// <summary>
    /// The column holding the file's PUBLISHED status (<c>Finisher</c>, <c>DNF</c>, <c>Aufg.</c>,
    /// <c>DNS/DNF</c>…), normalized through <see cref="RaceFinishStatusVocabulary.Normalize"/>.
    ///
    /// Two things change when it is mapped, and both are corrections rather than features. A row with a
    /// status and NO time survives (113 of 1387 Schauinsland entrants are exactly that, and used to be
    /// dropped), and the published status BEATS the derived one (four Black Forest riders are published
    /// DNF while carrying a finish-mat time, and used to be reported as finishers).
    /// </summary>
    public string? StatusColumn { get; init; }

    /// <summary>
    /// Seconds (or <c>h:mm:ss</c>) after the gun that this rider started. Black Forest publishes 47
    /// distinct values of exactly this.
    /// </summary>
    public string? StartOffsetColumn { get; init; }

    /// <summary>
    /// The gun (clock) and net (chip) columns.
    ///
    /// Mapping BOTH derives the start offset as <c>gun − net</c>, with no extra user input — and that is
    /// how most files carry it, because gun-vs-net is always two columns
    /// (<c>Brutto</c>/<c>Netto</c>, <c>CLOCK TIME</c>/<c>CHIP TIME</c>) and almost never an offset column.
    /// An explicit <see cref="StartOffsetColumn"/> still wins where both are present.
    ///
    /// The NET time is also the finish when <see cref="FinishColumn"/> is unmapped: net is the rider's own
    /// clock, which is what every other time in this slice is measured on.
    /// </summary>
    public string? GunTimeColumn { get; init; }
    public string? NetTimeColumn { get; init; }

    /// <summary>
    /// The start block's LABEL — <c>Startblock 1: 10 Uhr bis 11 Uhr</c>. Imported as text, because that is
    /// all it is: the user assigns one offset per wave afterwards. The no-code answer for a file whose
    /// blocks carry no machine-readable time, which is Schauinsland's.
    /// </summary>
    public string? WaveColumn { get; init; }

    /// <summary>
    /// Which race within the event — the Schauinsland file runs eleven off one start list. Behaves like
    /// <see cref="CategoryColumn"/>: a facet, stored verbatim.
    /// </summary>
    public string? DisciplineColumn { get; init; }

    /// <summary>
    /// checkpoint id → the CSV column holding that checkpoint's time.
    ///
    /// What the column MEANS depends on <see cref="PerLegTimes"/>: elapsed-from-start (default), or the
    /// duration of the leg ENDING at that checkpoint. Under per-leg the Finish checkpoint takes a column
    /// here too — it ends a leg like any other.
    /// </summary>
    public IReadOnlyDictionary<int, string> SplitColumns { get; init; } = new Dictionary<int, string>();

    /// <summary>
    /// The mapped columns hold LEG DURATIONS, not elapsed times — a relay or stage results file. See
    /// <see cref="RaceLegAccumulator"/> for why this cannot just be a running sum.
    /// </summary>
    public bool PerLegTimes { get; init; }
}

/// <summary>
/// A participant projected from one CSV row, with the split times it carries.
///
/// <paramref name="Line"/> is the file line he came from (1-based, header included), carried so the
/// PREVIEW can put a row next to the line the user is looking at. Skip reasons have always been
/// line-numbered; the rows that succeed had no way to say where they came from.
/// </summary>
public sealed record RaceCsvParticipant(
    RaceParticipant Participant, IReadOnlyList<RaceSplitTime> Splits, int Line);

/// <summary>What the import made of the file — rows that became participants, and rows that did not.</summary>
public sealed record RaceCsvImportResult(
    IReadOnlyList<RaceCsvParticipant> Participants,
    IReadOnlyList<string> Skipped)
{
    /// <summary>
    /// How many actually finished — and the published status WINS over the geometry.
    ///
    /// "Has a split at the finish checkpoint" is only the fallback, because the real files contradict it
    /// in both directions: four Black Forest riders are published DNF and carry a `cp_ziel` finish-mat
    /// time, and 113 Schauinsland entrants carry a status and no time at all. Counting splits alone
    /// reported 827 finishers for a file that says 823.
    /// </summary>
    public int FinisherCount => Participants.Count(p =>
        p.Participant.FinishStatus is { } status
            ? status == RaceFinishStatus.Finished
            : p.Splits.Any(s => s.CheckpointId == FinishCheckpointId));

    public CheckpointId FinishCheckpointId { get; init; }

    /// <summary>
    /// Riders whose legs sum past their published finish time — a forced start overlapped two legs, so
    /// the official clock is compressed. They are replayed on their LEG times (slower, truer); this is
    /// surfaced so the number is visible rather than surprising. See <see cref="RaceLegAccumulator"/>.
    /// </summary>
    public int OverlappedLegCount { get; init; }
}

/// <summary>
/// Projects CSV rows into participants + split times. Pure: no DB, no EF context — the import RULES
/// (what is a DNF, what is a duplicate, what is unusable) are unit-testable on their own.
/// </summary>
public static class RaceCsvImporter
{
    /// <summary>
    /// Build participants for <paramref name="race"/> from a parsed CSV.
    ///
    /// A row survives if it has a name and at least ONE usable time. A row with splits but no finish is
    /// kept as a DNF — the pacing model already holds such a rider at his last checkpoint rather than
    /// teleporting him to the line.
    /// </summary>
    public static RaceCsvImportResult Build(
        RaceCsvTable table, RaceCsvMapping mapping, IReadOnlyList<RaceCheckpoint> checkpoints, RaceId raceId)
    {
        var finish = checkpoints.OrderBy(c => c.Seq).LastOrDefault()
            ?? throw new ArgumentException("Race has no checkpoints.", nameof(checkpoints));

        var nameIdx = RequireColumn(table, mapping.NameColumn);
        // Net IS the finish when no finish column is mapped: it is the rider's own clock, which is the
        // basis every other time in this slice is measured on. Falling back saves the user mapping one
        // column to two pickers on a file that publishes Brutto/Netto and nothing else.
        var finishColumn = string.IsNullOrWhiteSpace(mapping.FinishColumn)
            ? mapping.NetTimeColumn
            : mapping.FinishColumn;
        // Under per-leg times the legs produce the finish themselves, so a total column is optional —
        // a relay export that publishes only leg times is a complete file, not a broken one.
        int? finishIdx = mapping.PerLegTimes && string.IsNullOrWhiteSpace(finishColumn)
            ? null
            : RequireColumn(table, finishColumn ?? string.Empty);
        var bibIdx = OptionalColumn(table, mapping.BibColumn);
        var catIdx = OptionalColumn(table, mapping.CategoryColumn);
        var genderIdx = OptionalColumn(table, mapping.GenderColumn);
        var clubIdx = OptionalColumn(table, mapping.ClubColumn);
        var statusIdx = OptionalColumn(table, mapping.StatusColumn);
        var offsetIdx = OptionalColumn(table, mapping.StartOffsetColumn);
        var gunIdx = OptionalColumn(table, mapping.GunTimeColumn);
        var netIdx = OptionalColumn(table, mapping.NetTimeColumn);
        var waveIdx = OptionalColumn(table, mapping.WaveColumn);
        var disciplineIdx = OptionalColumn(table, mapping.DisciplineColumn);

        var splitIdx = new List<(CheckpointId CheckpointId, int Column)>();
        foreach (var (checkpointId, column) in mapping.SplitColumns)
        {
            var cpId = new CheckpointId(checkpointId);
            if (checkpoints.All(c => c.CheckpointId != cpId))
                throw new ArgumentException($"Checkpoint {checkpointId} is not on this race.", nameof(mapping));
            splitIdx.Add((cpId, RequireColumn(table, column)));
        }

        // Per-leg mode walks the CHECKPOINTS, not the mapping: a leg with no column is a hole the
        // accumulator must see (and recover backward from), not a leg that silently does not exist.
        // Every checkpoint after the start ends a leg — including the Finish. Column -1 = unmapped;
        // Cell() reads that as null, which is exactly "no time for this leg".
        var legColumns = new List<(CheckpointId CheckpointId, int Column)>();
        if (mapping.PerLegTimes)
        {
            foreach (var c in checkpoints.OrderBy(c => c.Seq).Skip(1))
            {
                var mapped = splitIdx.Where(s => s.CheckpointId == c.CheckpointId).Select(s => s.Column);
                legColumns.Add((c.CheckpointId, mapped.DefaultIfEmpty(-1).First()));
            }
        }

        var participants = new List<RaceCsvParticipant>();
        var skipped = new List<string>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var overlapped = 0;

        for (var r = 0; r < table.Rows.Count; r++)
        {
            var row = table.Rows[r];
            var line = r + 2;   // 1-based, and the header is line 1 — so it matches what the user sees

            var name = Cell(row, nameIdx)?.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                skipped.Add($"line {line}: no name");
                continue;
            }

            var statusRaw = Cell(row, statusIdx);
            var status = RaceFinishStatusVocabulary.Normalize(statusRaw);
            var startOffset = StartOffsetOf(row, offsetIdx, gunIdx, netIdx);

            var finishTime = RaceCsvParser.ParseElapsedSeconds(Cell(row, finishIdx));

            List<RaceSplitTime> splits;
            if (mapping.PerLegTimes)
            {
                var accumulation = RaceLegAccumulator.Accumulate(
                    legColumns
                        .Select(l => new RaceLeg(l.CheckpointId, RaceCsvParser.ParseElapsedSeconds(Cell(row, l.Column))))
                        .ToList(),
                    finishTime);

                if (accumulation.OverlapsOfficialTotal) overlapped++;
                splits = accumulation.Splits
                    .Select(s => new RaceSplitTime { CheckpointId = s.CheckpointId, ElapsedSeconds = s.ElapsedSeconds })
                    .ToList();
            }
            else
            {
                splits = ElapsedSplits(row, splitIdx, finish.CheckpointId, finishTime);
            }

            // A row with a PUBLISHED status and no time is a complete result, not a broken one: 113 of
            // 1387 Schauinsland entrants are `DNS/DNF` with an empty finish column, and dropping them
            // silently lost 8% of the field. He has no anchors, so the replay leaves him off the course —
            // but he is on the start list, which is where the results table shows him.
            if (splits.Count == 0 && status == null)
            {
                skipped.Add($"line {line}: '{name}' has no usable time");
                continue;
            }

            // The same person twice would race himself. Bib is the better key when we have one.
            var key = Cell(row, bibIdx) is { Length: > 0 } bib ? $"bib:{bib}" : $"name:{name}";
            if (!seenNames.Add(key))
            {
                skipped.Add($"line {line}: duplicate '{name}'");
                continue;
            }

            participants.Add(new RaceCsvParticipant(
                new RaceParticipant
                {
                    RaceId = raceId,
                    Kind = RaceParticipantKind.Simulated,
                    DisplayName = name,
                    Bib = Cell(row, bibIdx),
                    Category = NormalizeCategory(Cell(row, catIdx)),
                    Gender = Cell(row, genderIdx),
                    Club = Cell(row, clubIdx),
                    // What the file PUBLISHED. Kept even when the replay does not use it (per-leg mode
                    // races on the leg sum), so the two totals stay comparable after import.
                    OfficialFinishSeconds = finishTime,
                    // Null when the column is unmapped or the cell blank — see RaceParticipant.FinishStatus
                    // for why that is a third state rather than a default.
                    FinishStatus = status,
                    FinishStatusRaw = status == null ? null : statusRaw,
                    // Null means 0 — a mass start — so an unmapped column changes nothing.
                    StartOffsetSeconds = startOffset,
                    Wave = Cell(row, waveIdx),
                    Discipline = Cell(row, disciplineIdx),
                    // Seeded from the row identity, not the row ORDER: re-importing a re-sorted file must
                    // give each rider the same jitter, or the replay changes under the user.
                    Seed = StableSeed(key),
                },
                splits,
                line));
        }

        return new RaceCsvImportResult(participants, skipped)
        {
            FinishCheckpointId = finish.CheckpointId,
            OverlappedLegCount = overlapped,
        };
    }

    /// <summary>
    /// The category with a trailing RANK stripped: <c>Sh40F (12.)</c> → <c>Sh40F</c>.
    ///
    /// Datasport embeds the rider's position within his class in the class NAME, which made race 2 carry
    /// 424 distinct categories for 425 participants — a Division filter with one option per rider, i.e.
    /// no filter at all. Nothing is lost: the stripped part is a rank, and a rank is already derivable
    /// from the results.
    ///
    /// Only a parenthesised group of DIGITS is stripped, and only at the end. <c>M40 (Masters)</c> and
    /// <c>Sh40F</c> come through untouched — a category is free text and most of it is meaningful.
    /// </summary>
    internal static string? NormalizeCategory(string? category)
    {
        if (category == null) return null;

        var value = category.TrimEnd();
        if (!value.EndsWith(')')) return category;

        var open = value.LastIndexOf('(');
        if (open <= 0) return category;

        // Trim BOTH sides of the dot: `( 3 . )` leaves a trailing space after TrimEnd('.'), and a space
        // is not a digit — the whole group would then survive as a "category".
        var inner = value[(open + 1)..^1].Trim().TrimEnd('.').Trim();
        if (inner.Length == 0 || !inner.All(char.IsAsciiDigit)) return category;

        var stripped = value[..open].TrimEnd();
        // A category that is ONLY a rank is not a category, but it is also not nothing — keeping the
        // original is more honest than storing an empty string that reads as "no category".
        return stripped.Length == 0 ? category : stripped;
    }

    /// <summary>
    /// When this rider started, relative to the gun.
    ///
    /// An explicit column wins; otherwise gun − net, which is how the offset actually travels in most
    /// files (gun-vs-net is always two columns, an offset column almost never exists). A NEGATIVE
    /// difference is discarded rather than stored: net can only exceed gun through a timing error or a
    /// mis-mapped pair, and a rider who starts before the gun would be placed on the course before the
    /// race — which the pacing model would then have to hold him through.
    /// </summary>
    private static double? StartOffsetOf(IReadOnlyList<string> row, int? offsetIdx, int? gunIdx, int? netIdx)
    {
        if (RaceCsvParser.ParseElapsedSeconds(Cell(row, offsetIdx)) is { } explicitOffset) return explicitOffset;

        var gun = RaceCsvParser.ParseElapsedSeconds(Cell(row, gunIdx));
        var net = RaceCsvParser.ParseElapsedSeconds(Cell(row, netIdx));
        if (gun is not { } g || net is not { } n) return null;

        var derived = g - n;
        return derived >= 0 ? derived : null;
    }

    /// <summary>The default mode: each mapped column already holds elapsed-from-start, so it is read as-is.</summary>
    private static List<RaceSplitTime> ElapsedSplits(
        IReadOnlyList<string> row, List<(CheckpointId CheckpointId, int Column)> splitIdx,
        CheckpointId finishCheckpointId, double? finishTime)
    {
        var splits = new List<RaceSplitTime>();
        foreach (var (checkpointId, col) in splitIdx)
        {
            // The explicit Finish column (added below) owns the finish checkpoint. A split column that also
            // maps to it would emit a SECOND row for the one (participant, finish) pair — a collision on the
            // composite PRIMARY KEY that 500s the whole import. Skip it; the Finish column wins.
            if (checkpointId == finishCheckpointId && finishTime.HasValue) continue;

            var t = RaceCsvParser.ParseElapsedSeconds(Cell(row, col));
            if (t.HasValue) splits.Add(new RaceSplitTime { CheckpointId = checkpointId, ElapsedSeconds = t.Value });
        }
        if (finishTime.HasValue)
            splits.Add(new RaceSplitTime { CheckpointId = finishCheckpointId, ElapsedSeconds = finishTime.Value });
        return splits;
    }

    /// <summary>Deterministic non-negative hash — <c>string.GetHashCode</c> is randomized per process.</summary>
    private static int StableSeed(string key)
    {
        unchecked
        {
            var hash = 2166136261u;                 // FNV-1a
            foreach (var c in key)
            {
                hash ^= c;
                hash *= 16777619u;
            }
            return (int)(hash & 0x7fffffff);
        }
    }

    private static string? Cell(IReadOnlyList<string> row, int? index)
    {
        if (index is not int i || i < 0 || i >= row.Count) return null;
        var v = row[i].Trim();
        return v.Length == 0 ? null : v;
    }

    private static int RequireColumn(RaceCsvTable table, string column)
    {
        var i = IndexOf(table, column);
        return i >= 0 ? i : throw new ArgumentException($"CSV has no column '{column}'.");
    }

    private static int? OptionalColumn(RaceCsvTable table, string? column)
    {
        if (string.IsNullOrWhiteSpace(column)) return null;
        var i = IndexOf(table, column);
        return i >= 0 ? i : null;
    }

    private static int IndexOf(RaceCsvTable table, string column)
    {
        for (var i = 0; i < table.Header.Count; i++)
            if (string.Equals(table.Header[i], column, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }
}
