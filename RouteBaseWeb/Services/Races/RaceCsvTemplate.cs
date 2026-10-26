using System.Globalization;
using System.Text;
using RouteBaseWeb.Models;

namespace RouteBaseWeb.Services.Races;

/// <summary>
/// A results CSV template built from THIS race's own checkpoints. Pure — no DB, no HTTP.
///
/// The point is not convenience, it is that adding a race stopped meaning writing code. Every race so far
/// needed a bespoke Python puller to reshape a published results file into something the importer happened
/// to accept; a template the user fills in inverts that. The headers are the canonical alias-table
/// spellings, so a file built from this one maps itself with no clicks at all.
///
/// The example rows are load-bearing, not decoration. They demonstrate the time FORMAT (which is the
/// single most common way an import produces 424 riders with no times), a DNF that keeps the splits it
/// has, and a DNS that carries a status and no time — the row shape that used to be dropped silently.
/// </summary>
public static class RaceCsvTemplate
{
    /// <summary>
    /// The fixed leading columns, in the order a person reads them. Every one is an exact
    /// <see cref="RaceCsvVocabulary"/> alias, so a template-derived file needs no mapping at all.
    /// </summary>
    private static readonly string[] LeadColumns = ["bib", "name", "category", "gender", "club", "status"];

    /// <summary>
    /// Header + example rows for a race's checkpoints.
    ///
    /// Split columns are ELAPSED FROM THE START — one per checkpoint between the start and the finish, in
    /// seq order — because that is the importer's default mode and the one a cumulative published file
    /// already matches. The finish is its own column rather than the last split, so the template reads the
    /// way results files actually do.
    /// </summary>
    public static string Build(IReadOnlyList<RaceCheckpoint> checkpoints)
    {
        var ordered = checkpoints.OrderBy(c => c.Seq).ToList();
        // Between the gun and the line: the start needs no column (everyone leaves at 0) and the finish
        // has its own. A two-checkpoint race yields no split columns at all, which is correct.
        var splits = ordered.Count < 2 ? [] : ordered.Skip(1).SkipLast(1).ToList();

        var headers = LeadColumns
            .Concat(splits.Select((c, i) => ColumnNameFor(c, i)))
            .Append("finish")
            .ToList();
        // A duplicate header resolves to the first column and renders two identical <option>s, so two
        // checkpoints called "Feed zone" would silently share one column. Numbered, not de-duplicated
        // away — the user still needs to tell them apart.
        Deduplicate(headers);

        var sb = new StringBuilder();
        sb.Append(string.Join(',', headers.Select(Quote))).Append('\n');
        foreach (var row in ExampleRows(splits.Count))
            sb.Append(string.Join(',', row.Select(Quote))).Append('\n');
        return sb.ToString();
    }

    /// <summary>
    /// What to call a split column: the leg that ENDS at this checkpoint, else the checkpoint's own name,
    /// else its position. Sanitized, because a checkpoint name is free text and a comma or a quote in a
    /// header is a column that no longer lines up with its data.
    /// </summary>
    private static string ColumnNameFor(RaceCheckpoint checkpoint, int index)
    {
        var raw = Blank(checkpoint.CheckpointName) ?? Blank(checkpoint.LegName) ?? $"cp{index + 1}";
        var cleaned = new string(raw.Select(c => c is ',' or ';' or '"' or '\t' or '|' or '\n' or '\r' ? ' ' : c).ToArray());
        return string.Join(' ', cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>Append " 2", " 3"… to repeats, so every column is addressable.</summary>
    private static void Deduplicate(List<string> headers)
    {
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < headers.Count; i++)
        {
            var name = headers[i];
            if (seen.TryGetValue(name, out var count))
            {
                seen[name] = count + 1;
                headers[i] = $"{name} {count + 1}";
            }
            else
            {
                seen[name] = 1;
            }
        }
    }

    /// <summary>
    /// Three rows, each demonstrating something the importer cares about.
    ///
    /// 1. A finisher, with every split filled — the time format, and that splits are elapsed FROM THE START
    ///    (they increase down the row).
    /// 2. A rider who abandoned: a status, the splits he reached, and a BLANK finish. He imports, and the
    ///    replay holds him where he stopped rather than walking him to a line he never crossed.
    /// 3. A non-starter: a status and nothing else. This row shape was dropped as "no usable time" until
    ///    the status column existed — 113 of 1387 Schauinsland entrants, silently.
    /// </summary>
    private static IEnumerable<string[]> ExampleRows(int splitCount)
    {
        // Elapsed seconds at each split for the two riders who got that far, spread evenly and offset so
        // the numbers are obviously not round — a template full of 1:00:00 teaches nothing about format.
        string At(int index, double finishSeconds) =>
            Clock(finishSeconds * (index + 1) / (splitCount + 1));

        yield return
        [
            "101", "MUSTERMANN Erika", "W40", "F", "RV Beispiel", "Finisher",
            .. Enumerable.Range(0, splitCount).Select(i => At(i, 9798.2)),
            Clock(9798.2),
        ];

        yield return
        [
            "102", "BEISPIEL Max", "M30", "M", "SC Vorlage", "DNF",
            // He reached the first half of the checkpoints and stopped.
            .. Enumerable.Range(0, splitCount).Select(i => i < splitCount / 2 ? At(i, 11400) : string.Empty),
            string.Empty,
        ];

        yield return
        [
            "103", "NICHTSTARTER Anna", "W30", "F", string.Empty, "DNS",
            .. Enumerable.Repeat(string.Empty, splitCount),
            string.Empty,
        ];
    }

    /// <summary>h:mm:ss.f — the format ParseElapsedSeconds reads, written the way the exports do.</summary>
    private static string Clock(double seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds / 100}");
    }

    /// <summary>RFC 4180: quote only what needs it, so the template reads like something a person wrote.</summary>
    private static string Quote(string value) =>
        value.AsSpan().IndexOfAny(",\"\n\r") >= 0
            ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
            : value;
}
