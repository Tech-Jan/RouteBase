using System.Globalization;
using RouteBaseWeb.Helpers;
using Xunit;

namespace RouteBaseWeb.Tests.Services;

/// <summary>Tests for the pure grid filter expression parser (<see cref="GridFilterParsing"/>).</summary>
public class GridFilterParsingTests
{
    [Theory]
    [InlineData("run|bike", new[] { "run", "bike" })]
    [InlineData("  morning | trail ", new[] { "morning", "trail" })]
    [InlineData("run|", new[] { "run" })]
    [InlineData("|run", new[] { "run" })]
    [InlineData("a||b", new[] { "a", "b" })]
    [InlineData("run", new[] { "run" })]
    public void SplitOrTerms_SplitsTrimsAndDropsEmpty(string input, string[] expected)
    {
        Assert.Equal(expected, GridFilterParsing.SplitOrTerms(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("|")]
    public void SplitOrTerms_EmptyInput_ReturnsEmpty(string? input)
    {
        Assert.Empty(GridFilterParsing.SplitOrTerms(input));
    }

    // ── ParseRanges ──────────────────────────────────────────────────────────────

    private static string Show(double? d) => d.HasValue ? d.Value.ToString(CultureInfo.InvariantCulture) : "*";

    private static string Fmt(IReadOnlyList<(double? Min, double? Max)> ranges)
        => string.Join(";", ranges.Select(r => $"{Show(r.Min)}..{Show(r.Max)}"));

    [Theory]
    [InlineData("3-6", "3..6")]
    [InlineData("3-6,8-12", "3..6;8..12")]
    [InlineData("3-6|8-12", "3..6;8..12")]
    [InlineData(" 3 - 6 , 8-12 ", "3..6;8..12")]
    [InlineData(">5", "5..*")]
    [InlineData(">=5", "5..*")]
    [InlineData("5-", "5..*")]
    [InlineData("<10", "*..10")]
    [InlineData("<=10", "*..10")]
    [InlineData("-10", "*..10")]
    [InlineData("5", "5..5")]
    [InlineData("-10-5", "-10..5")]
    [InlineData("1.5-2.5", "1.5..2.5")]
    [InlineData("abc, 3-6, 9-1", "3..6")] // bad token + reversed dropped, good kept
    public void ParseRanges_Cases(string input, string expected)
    {
        Assert.Equal(expected, Fmt(GridFilterParsing.ParseRanges(input)));
    }

    [Theory]
    [InlineData("6-3")]   // min > max → dropped
    [InlineData("x")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData(", , ")]
    public void ParseRanges_DroppedOrEmpty_ReturnsEmpty(string? input)
    {
        Assert.Empty(GridFilterParsing.ParseRanges(input));
    }

    // SC-02: NaN/±Infinity parse to non-finite doubles that would otherwise slip through as bounds
    // (and later throw when bound to a SQLite parameter). ParseNum now rejects them → no range.
    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    [InlineData(">NaN")]
    [InlineData("5-Infinity")]
    public void ParseRanges_NonFinite_ReturnsEmpty(string input)
    {
        Assert.Empty(GridFilterParsing.ParseRanges(input));
    }

    // StripNegation — mirror of the TS grid-filter-parse.test.ts cases.
    [Theory]
    [InlineData("*live*", false, "*live*")]
    [InlineData("!*live*", true, "*live*")]
    [InlineData("!=*live*", true, "*live*")]
    [InlineData("! *live* ", true, "*live*")] // trimmed
    [InlineData("!", true, "")]               // bare ! → empty (no-op for callers)
    public void StripNegation_Cases(string input, bool negated, string pattern)
    {
        Assert.Equal((negated, pattern), GridFilterParsing.StripNegation(input));
    }
}
