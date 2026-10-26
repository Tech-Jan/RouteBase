using System.Globalization;
using System.Text;

namespace RouteBaseWeb.Services.Races;

/// <summary>
/// A parsed CSV: the header row plus the data rows, all fields as raw strings.
///
/// <paramref name="Delimiter"/> is carried out because the PREVIEW shows it. A user staring at "1 column
/// found" needs to know we read the file with a comma; the sniff being invisible is what made a semicolon
/// export look like a broken file rather than a mis-read one.
/// </summary>
public sealed record RaceCsvTable(
    IReadOnlyList<string> Header, IReadOnlyList<IReadOnlyList<string>> Rows, char Delimiter = ',');

/// <summary>
/// CSV reading for race result files. Pure — no DB, no HTTP.
///
/// Proper RFC 4180 quoting is not optional here: European timing exports write elapsed times with a
/// DECIMAL COMMA inside a comma-separated file (<c>"1:16:59,4"</c>). A naive Split(',') shreds every
/// row in the file we actually have.
/// </summary>
public static class RaceCsvParser
{
    /// <summary>
    /// The delimiters worth sniffing for, in tie-break order. Semicolon is the NORM in DE/Nordic timing
    /// exports (SportIdent OE CSV is semicolon by default) and tab is what a spreadsheet paste produces;
    /// a hard-coded comma read such a file as one column, skipped every row, and then handed the import
    /// an empty field.
    /// </summary>
    private static readonly char[] Delimiters = [',', ';', '\t', '|'];

    /// <summary>Split CSV text into a header and rows. Handles quoted fields, embedded delimiters/newlines
    /// and doubled escape-quotes. Blank lines are skipped.</summary>
    public static RaceCsvTable Parse(string csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) throw new ArgumentException("Empty CSV.", nameof(csv));
        // A UTF-8 BOM survives File.ReadAllText / Blob.text() as U+FEFF and would otherwise glue itself
        // to the first header name, so the user's `bib` column is silently `﻿bib` and unmappable.
        if (csv[0] == '﻿') csv = csv[1..];

        var delimiter = SniffDelimiter(csv);

        var rows = new List<IReadOnlyList<string>>();
        var field = new StringBuilder();
        var row = new List<string>();
        var inQuotes = false;
        // Has this field already consumed a character (or a closing quote)? A `"` may only OPEN a quoted
        // field at its very START; anywhere else it is a literal character, which is what Python's `csv`
        // and Excel both do. Being stricter than them was silent data LOSS, not strictness: a club named
        // `29" Wheelers` opened quote mode mid-field and swallowed every remaining row in the file into
        // one giant cell, and the import reported a handful of riders with no error.
        var started = false;

        for (var i = 0; i < csv.Length; i++)
        {
            var c = csv[i];
            if (inQuotes)
            {
                if (c != '"') { field.Append(c); continue; }
                if (i + 1 < csv.Length && csv[i + 1] == '"') { field.Append('"'); i++; continue; }
                inQuotes = false;
                started = true;
                continue;
            }

            if (c == '"')
            {
                if (started) field.Append('"');   // literal — see `started` above
                else inQuotes = true;
                continue;
            }
            if (c == delimiter)
            {
                row.Add(field.ToString());
                field.Clear();
                started = false;
                continue;
            }

            switch (c)
            {
                case '\r':
                    break;   // CRLF — the \n does the work
                case '\n':
                    row.Add(field.ToString());
                    field.Clear();
                    started = false;
                    if (row.Any(f => f.Length > 0)) rows.Add(row);
                    row = new List<string>();
                    break;
                default:
                    field.Append(c);
                    started = true;
                    break;
            }
        }
        row.Add(field.ToString());
        if (row.Any(f => f.Length > 0)) rows.Add(row);

        if (rows.Count < 2) throw new ArgumentException("CSV has no data rows.", nameof(csv));

        var header = rows[0].Select(h => h.Trim()).ToList();
        return new RaceCsvTable(header, rows.Skip(1).ToList(), delimiter);
    }

    /// <summary>
    /// Which delimiter this file uses, decided on the HEADER line: whichever candidate splits it into the
    /// most fields, comma on a tie (<see cref="Delimiters"/> is in tie-break order).
    ///
    /// The header is the first non-blank line, which is the line <see cref="Parse"/> will treat as the
    /// header too — sniffing a different line than the one that gets used is its own class of bug.
    /// Characters inside a quoted span are not counted, so a club named <c>"Bern, RV"</c> in the header
    /// cannot vote for a comma the file does not use.
    /// </summary>
    internal static char SniffDelimiter(string csv)
    {
        var header = FirstNonBlankLine(csv);
        var best = Delimiters[0];
        var bestCount = 0;
        foreach (var candidate in Delimiters)
        {
            var count = CountOutsideQuotes(header, candidate);
            if (count > bestCount) { best = candidate; bestCount = count; }
        }
        return best;
    }

    private static string FirstNonBlankLine(string csv)
    {
        foreach (var line in csv.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.Trim().Length > 0) return trimmed;
        }
        return string.Empty;
    }

    private static int CountOutsideQuotes(string line, char c)
    {
        var count = 0;
        var inQuotes = false;
        foreach (var ch in line)
        {
            if (ch == '"') inQuotes = !inQuotes;
            else if (ch == c && !inQuotes) count++;
        }
        return count;
    }

    /// <summary>
    /// Elapsed time → seconds. Accepts <c>h:mm:ss</c>, <c>mm:ss</c>, an optional decimal fraction with
    /// either separator (<c>1:16:59,4</c> / <c>1:16:59.4</c>), and a bare number (read as seconds).
    ///
    /// Returns null for anything that isn't a time — blanks, <c>DNF</c>, <c>-</c>, <c>--:--</c>. A DNF is
    /// a legitimate row, not a parse failure: the rider simply has no time at that checkpoint.
    ///
    /// A NON-FINITE or NEGATIVE cell is not a time either, and saying so is this method's job — it is the
    /// one choke point every imported time passes through. <c>double.TryParse</c> has been IEEE-compliant
    /// since .NET Core 3.0 and will happily hand back NaN for "nan" (which is how pandas, R and Excel spell
    /// a missing value) and ±∞ for "Infinity" or an overflowing "1e400". Waved through, such a value is
    /// persisted as a split and then kills the whole replay: System.Text.Json refuses to write it, and it
    /// throws AFTER the response body has begun streaming — so the browser gets HTTP 200 with a truncated
    /// body and reports a bare "Failed to fetch", with nothing in the UI to point at.
    ///
    /// A negative value (a mis-mapped column, a spreadsheet formula gone wrong) is the same class: it is
    /// finite, so it defeats no existing guard, but no rider crosses a checkpoint before the start — and in
    /// per-leg mode a negative leg makes the accumulated splits run BACKWARDS. Rejected here, once.
    /// </summary>
    public static double? ParseElapsedSeconds(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var s = value.Trim();
        if (s is "-" or "--" or "DNF" or "DNS" or "DSQ") return null;

        var parts = s.Split(':');
        if (parts.Length > 3) return null;

        if (parts.Length == 1)
        {
            // Bare number = seconds (what our own exporter writes in `finish_seconds`).
            return Finite(
                double.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var raw)
                    ? raw
                    : null);
        }

        double total = 0;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var unit)) return null;
            total = total * 60 + unit;
        }
        total *= 60;   // ...now in seconds up to the minutes place

        var last = parts[^1].Replace(',', '.');
        if (!double.TryParse(last, NumberStyles.Float, CultureInfo.InvariantCulture, out var secs)) return null;
        // `secs < 0 || secs >= 60` cannot reject a NaN — every comparison with NaN is false — so the
        // finiteness test has to come first, not after.
        if (!double.IsFinite(secs) || secs < 0 || secs >= 60) return null;

        return Finite(total + secs);
    }

    /// <summary>A non-finite OR negative value is no time. Null, exactly like a DNF.</summary>
    private static double? Finite(double? value) =>
        value is { } v && double.IsFinite(v) && v >= 0 ? v : null;
}
