using System.Globalization;
using RouteBaseWeb.Services.Segments;
using RouteBaseWeb.Services.Segments.Rules;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Rules;

/// <summary>
/// SP-04: DateRangeFilter must parse "yyyy-MM-dd" under InvariantCulture, not the host's
/// CurrentCulture — a non-Gregorian calendar (e.g. th-TH Buddhist) would otherwise shift
/// or fail the parse of a legacy-v2 date filter.
/// </summary>
public class RuleDocumentDateFilterTests
{
    [Fact]
    public void TryParseFrom_Parses_Iso_Date_Under_NonGregorian_Culture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("th-TH");

            var filter = new DateRangeFilter { From = "2026-07-10" };
            var ok = filter.TryParseFrom(out var value);

            Assert.True(ok);
            Assert.Equal(new DateOnly(2026, 7, 10), value);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    // SC-05: a validated To = "9999-12-31" (DateOnly.MaxValue) must not throw when the SQL filter
    // advances the upper bound — AddDays(1) would overflow. The clamp keeps a well-formed clause.
    [Fact]
    public void DateRangeSqlFilter_MaxDate_DoesNotThrow()
    {
        var filter = new DateRangeSqlFilter(null, DateOnly.MaxValue);

        var ex = Record.Exception(() => filter.ToSql(_ => "@p"));

        Assert.Null(ex);
        Assert.Contains("<=", filter.ToSql(_ => "@p"), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- Validate() from/to combinations
    // These pin the four independent legs of DateRangeFilter.Validate that mutation testing found
    // unasserted: a single valid bound must NOT trip "from is after to" (the composite && chain), an
    // unparseable TO must be flagged (the !hasTo guard), and the no-bounds early-out is the only error.

    [Fact]
    public void Validate_FromOnly_IsValid()
    {
        // A one-sided range is legal. If the "from is after to" composite loses one of its && guards it
        // fires here spuriously — so an empty result pins every && in that chain.
        Assert.Empty(new DateRangeFilter { From = "2025-01-01" }.Validate());
    }

    [Fact]
    public void Validate_ToOnly_IsValid()
    {
        Assert.Empty(new DateRangeFilter { To = "2025-12-31" }.Validate());
    }

    [Fact]
    public void Validate_FromEqualsTo_IsValid()
    {
        // Equal bounds are a single-day window — allowed (from is not strictly after to).
        Assert.Empty(new DateRangeFilter { From = "2025-06-15", To = "2025-06-15" }.Validate());
    }

    [Fact]
    public void Validate_FromAfterTo_Flagged()
    {
        var errors = new DateRangeFilter { From = "2025-12-31", To = "2025-01-01" }.Validate().ToList();
        Assert.Contains(errors, e => e.Contains("from is after to", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_UnparseableTo_Flagged()
    {
        // Pins the To leg specifically (the From leg has its own coverage): an unparseable TO must report.
        var errors = new DateRangeFilter { To = "not-a-date" }.Validate().ToList();
        Assert.Contains(errors, e => e.Contains("unparseable to", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_UnparseableFrom_Flagged()
    {
        var errors = new DateRangeFilter { From = "2025-13-40" }.Validate().ToList();
        Assert.Contains(errors, e => e.Contains("unparseable from", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_FromValid_ToUnparseable_DoesNotReportFromAfterTo()
    {
        // When one bound cannot be parsed the ordering check must be skipped, not compared against garbage.
        var errors = new DateRangeFilter { From = "2025-01-01", To = "not-a-date" }.Validate().ToList();
        Assert.Contains(errors, e => e.Contains("unparseable to", StringComparison.Ordinal));
        Assert.DoesNotContain(errors, e => e.Contains("from is after to", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_NoBounds_IsTheOnlyError()
    {
        // The no-bounds branch yield-breaks: it must be the sole error, never followed by parse errors.
        var errors = new DateRangeFilter().Validate().ToList();
        var only = Assert.Single(errors);
        Assert.Contains("at least one of from / to", only, StringComparison.Ordinal);
    }
}
