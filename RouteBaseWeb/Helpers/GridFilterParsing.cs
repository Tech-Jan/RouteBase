using System.Globalization;

namespace RouteBaseWeb.Helpers;

/// <summary>
/// Pure parsing of the grid filter expression grammars, shared by the predicate helpers. Mirrors the
/// TypeScript parser in <c>src/js/core/grid/grid-filter-parse.ts</c> — keep the two in sync.
///
/// Grammar:
/// <code>
/// or-list    := term ("|" term)*                ; text: each term keeps *,? + contains fallback
/// term       := ["!" | "!="] pattern            ; leading ! (or !=) negates the term (NOT-contains)
/// range-expr := token (("," | "|") token)*
/// token      := A "-" B   closed [A,B] | ">="N | ">"N | N"-"  (≥N) | "&lt;="N | "&lt;"N | "-"N  (≤N) | N (exact)
/// </code>
/// </summary>
public static class GridFilterParsing
{
    /// <summary>Splits a text filter into OR'd terms on <c>|</c>, trimming and dropping empty terms.</summary>
    public static IReadOnlyList<string> SplitOrTerms(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return Array.Empty<string>();

        return input.Split('|')
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .ToList();
    }

    /// <summary>
    /// Strips a leading negation operator (<c>!</c> or <c>!=</c>) off a text term. A negated term matches
    /// rows that do NOT match the (wildcard) pattern; a bare <c>!</c> leaves an empty pattern (a no-op the
    /// callers drop). Mirrors the TypeScript <c>stripNegation</c> — keep the two in sync.
    /// </summary>
    public static (bool Negated, string Pattern) StripNegation(string term)
    {
        var negated = false;
        var t = term;
        if (t.StartsWith("!", StringComparison.Ordinal))
        {
            negated = true;
            t = t[1..];
            if (t.StartsWith("=", StringComparison.Ordinal)) t = t[1..]; // accept both `!x` and `!=x`
        }
        return (negated, t.Trim());
    }

    /// <summary>
    /// Parses a numeric range expression into a list of (Min, Max) bounds (display units; the caller scales).
    /// Tokens are OR-separated by <c>,</c> or <c>|</c>. A null bound means open-ended. Invalid tokens (parse
    /// failures, min&gt;max, fully unbounded) are dropped; an all-invalid/empty input yields an empty list.
    /// </summary>
    public static IReadOnlyList<(double? Min, double? Max)> ParseRanges(string? input)
    {
        var result = new List<(double? Min, double? Max)>();
        if (string.IsNullOrWhiteSpace(input))
            return result;

        foreach (var raw in input.Split(new[] { ',', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var range = ParseToken(raw);
            if (range is { } r && IsValid(r))
                result.Add(r);
        }

        return result;
    }

    // Classifies one token by its leading characters (operators first), then closed range, then bare number.
    private static (double? Min, double? Max)? ParseToken(string t)
    {
        if (t.StartsWith(">=", StringComparison.Ordinal)) return Bound(t[2..], lower: true);
        if (t.StartsWith("<=", StringComparison.Ordinal)) return Bound(t[2..], lower: false);
        if (t.StartsWith(">", StringComparison.Ordinal)) return Bound(t[1..], lower: true);
        if (t.StartsWith("<", StringComparison.Ordinal)) return Bound(t[1..], lower: false);

        // "5-" → ≥5
        if (t.Length > 1 && t.EndsWith("-", StringComparison.Ordinal)) return Bound(t[..^1], lower: true);

        // "-10" (leading '-' with no further '-') → ≤10. A negative lower bound only appears in an explicit
        // closed range like "-10-5" (handled below).
        if (t.Length > 1 && t[0] == '-' && t.IndexOf('-', 1) < 0) return Bound(t[1..], lower: false);

        // Closed range "A-B": split at the '-' whose left side parses as a number (A may be negative).
        for (var i = 1; i < t.Length; i++)
        {
            if (t[i] != '-') continue;
            var left = ParseNum(t[..i]);
            var right = ParseNum(t[(i + 1)..]);
            if (left.HasValue && right.HasValue)
                return (left, right);
        }

        // Bare number → exact match.
        var n = ParseNum(t);
        return n.HasValue ? (n, n) : null;
    }

    private static (double? Min, double? Max)? Bound(string s, bool lower)
    {
        var n = ParseNum(s);
        if (!n.HasValue) return null;
        return lower ? (n, (double?)null) : ((double?)null, n);
    }

    private static bool IsValid((double? Min, double? Max) r)
    {
        if (r.Min is null && r.Max is null) return false;               // unbounded → no constraint
        if (r.Min.HasValue && r.Max.HasValue && r.Min > r.Max) return false; // reversed
        return true;
    }

    // "," is a range separator, not a decimal — use InvariantCulture ('.' decimal), no thousands grouping.
    // SC-02: reject NaN/±Infinity — TryParse accepts them, but a non-finite bound defeats every IEEE
    // comparison guard downstream and then throws when Microsoft.Data.Sqlite refuses to bind it.
    private static double? ParseNum(string s)
        => double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
           && double.IsFinite(v) ? v : null;
}
