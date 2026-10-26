using RouteBaseWeb.Models.GridFilter;
using RouteBaseWeb.Services.Segments.Rules;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Rules;

/// <summary>
/// Boundary + null-guard coverage for the individual <see cref="TrackFilter"/> subtypes'
/// <c>Validate</c> methods and for the document-level duplicate-type message (which is keyed off
/// each filter's <c>Discriminator</c>). Mutation testing (2026-08-02) found the caps, the
/// early-out <c>yield break</c>s, the null-column skips and the discriminator strings all executed
/// but unasserted.
/// </summary>
public class RuleTrackFilterValidationTests
{
    /// <summary>A minimal structurally-valid document carrying the given track filters, so the only
    /// validation errors are the filters' own (and any duplicate-type message).</summary>
    private static RuleDocument DocWith(params TrackFilter[] filters) => new()
    {
        Shapes = { new ShapeDef { No = 1, Kind = "zone", Geom = new RectGeom { South = 1, West = 2, North = 3, East = 4 } } },
        Extent = new WholeExtent(),
        TrackFilters = filters.ToList(),
    };

    // ------------------------------------------------------------------ duplicate-type discriminator

    // Each filter's Discriminator string is surfaced verbatim in the duplicate-type message. A blanked
    // discriminator ("") would still produce "Duplicate track filter of type \"\"" and pass a bare
    // "Duplicate" assert — so we pin the actual discriminator word per type.
    [Fact]
    public void Duplicate_DateRange_MessageNamesDiscriminator()
    {
        var errors = DocWith(
            new DateRangeFilter { From = "2025-01-01" },
            new DateRangeFilter { To = "2025-12-31" }).Validate();
        Assert.Contains(errors, e => e.Contains("Duplicate track filter of type \"dateRange\"", StringComparison.Ordinal));
    }

    [Fact]
    public void Duplicate_ActivityType_MessageNamesDiscriminator()
    {
        var errors = DocWith(
            new ActivityTypeFilter { Types = { "Run" } },
            new ActivityTypeFilter { Types = { "Ride" } }).Validate();
        Assert.Contains(errors, e => e.Contains("Duplicate track filter of type \"activityType\"", StringComparison.Ordinal));
    }

    [Fact]
    public void Duplicate_Owner_MessageNamesDiscriminator()
    {
        var errors = DocWith(new OwnerFilter(), new OwnerFilter()).Validate();
        Assert.Contains(errors, e => e.Contains("Duplicate track filter of type \"owner\"", StringComparison.Ordinal));
    }

    [Fact]
    public void Duplicate_GridFilters_MessageNamesDiscriminator()
    {
        var errors = DocWith(
            new GridFiltersFilter { Filters = { ["name"] = new ColumnFilter { Text = "a" } } },
            new GridFiltersFilter { Filters = { ["type"] = new ColumnFilter { In = { "Run" } } } }).Validate();
        Assert.Contains(errors, e => e.Contains("Duplicate track filter of type \"gridFilters\"", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ ActivityTypeFilter.Validate

    // Types = null (JSON "types": null) must early-out with the "needs at least one" error, NOT fall
    // through to Types.Count and NRE. Pins the yield break.
    [Fact]
    public void ActivityType_NullTypes_YieldsError_NoThrow()
    {
        var filter = new ActivityTypeFilter { Types = null! };

        List<string> errors = new();
        var ex = Record.Exception(() => errors = filter.Validate().ToList());

        Assert.Null(ex);
        Assert.Contains(errors, e => e.Contains("activityType filter needs at least one type", StringComparison.Ordinal));
    }

    [Fact]
    public void ActivityType_ExactlyMax_NotRejected()
    {
        var types = Enumerable.Range(1, RuleDocument.MaxActivityTypes).Select(i => $"t{i}").ToList();
        var errors = new ActivityTypeFilter { Types = types }.Validate().ToList();
        Assert.DoesNotContain(errors, e => e.Contains("too many types", StringComparison.Ordinal));
        Assert.Empty(errors);
    }

    [Fact]
    public void ActivityType_OverMax_Rejected()
    {
        var types = Enumerable.Range(1, RuleDocument.MaxActivityTypes + 1).Select(i => $"t{i}").ToList();
        var errors = new ActivityTypeFilter { Types = types }.Validate().ToList();
        Assert.Contains(errors, e => e.Contains($"too many types (max {RuleDocument.MaxActivityTypes})", StringComparison.Ordinal));
    }

    // A blank type mixed with a real one must be flagged (Any-blank), not require ALL to be blank.
    [Fact]
    public void ActivityType_MixedBlankAndReal_FlagsBlank()
    {
        var errors = new ActivityTypeFilter { Types = { "Run", " " } }.Validate().ToList();
        Assert.Contains(errors, e => e.Contains("blank type", StringComparison.Ordinal));
    }

    [Fact]
    public void ActivityType_AllReal_NoBlankError()
    {
        var errors = new ActivityTypeFilter { Types = { "Run", "Ride" } }.Validate().ToList();
        Assert.Empty(errors);
    }

    // ------------------------------------------------------------------ GridFiltersFilter.Validate

    // Filters = null (JSON "filters": null) must early-out with the guard error, NOT fall through to the
    // foreach and NRE. Pins both the first-|| guard and its yield break.
    [Fact]
    public void GridFilters_NullMap_YieldsError_NoThrow()
    {
        var filter = new GridFiltersFilter { Filters = null! };

        List<string> errors = new();
        var ex = Record.Exception(() => errors = filter.Validate().ToList());

        Assert.Null(ex);
        Assert.Contains(errors, e => e.Contains("needs at least one non-empty column filter", StringComparison.Ordinal));
    }

    // One non-empty column alongside an empty one is a real constraint — All-empty is false, so it must
    // NOT report "needs at least one non-empty column filter" (Any-empty would wrongly trip it).
    [Fact]
    public void GridFilters_MixedEmptyAndNonEmpty_IsValid()
    {
        var filter = new GridFiltersFilter
        {
            Filters =
            {
                ["name"] = new ColumnFilter { Text = "morning" },
                ["distance"] = new ColumnFilter(),   // empty → ignored, not fatal
            },
        };
        var errors = filter.Validate().ToList();
        Assert.DoesNotContain(errors, e => e.Contains("non-empty column filter", StringComparison.Ordinal));
        Assert.Empty(errors);
    }

    // A null column VALUE (JSON "filters": { "x": null }) alongside a real one must be skipped, not NRE'd
    // on f.In. Pins the `if (f is null) continue;`.
    [Fact]
    public void GridFilters_NullColumnValue_Skipped_NoThrow()
    {
        var filter = new GridFiltersFilter
        {
            Filters =
            {
                ["name"] = new ColumnFilter { Text = "morning" },
                ["bad"] = null!,
            },
        };

        List<string> errors = new();
        var ex = Record.Exception(() => errors = filter.Validate().ToList());

        Assert.Null(ex);
        Assert.Empty(errors);
    }

    [Fact]
    public void GridFilters_ExactlyMaxInValues_NotRejected()
    {
        var filter = new GridFiltersFilter
        {
            Filters = { ["type"] = new ColumnFilter { In = Enumerable.Range(1, RuleDocument.MaxInValues).Select(i => $"t{i}").ToList() } },
        };
        var errors = filter.Validate().ToList();
        Assert.DoesNotContain(errors, e => e.Contains("too many values", StringComparison.Ordinal));
        Assert.Empty(errors);
    }

    [Fact]
    public void GridFilters_OverMaxInValues_Rejected()
    {
        var filter = new GridFiltersFilter
        {
            Filters = { ["type"] = new ColumnFilter { In = Enumerable.Range(1, RuleDocument.MaxInValues + 1).Select(i => $"t{i}").ToList() } },
        };
        var errors = filter.Validate().ToList();
        Assert.Contains(errors, e => e.Contains($"too many values (max {RuleDocument.MaxInValues})", StringComparison.Ordinal));
    }

    [Fact]
    public void GridFilters_ExactlyMaxRangeLength_NotRejected()
    {
        var filter = new GridFiltersFilter
        {
            Filters = { ["distance"] = new ColumnFilter { Range = new string('a', RuleDocument.MaxFilterTextLength) } },
        };
        var errors = filter.Validate().ToList();
        Assert.DoesNotContain(errors, e => e.Contains("range too long", StringComparison.Ordinal));
    }

    [Fact]
    public void GridFilters_OverMaxRangeLength_Rejected()
    {
        var filter = new GridFiltersFilter
        {
            Filters = { ["distance"] = new ColumnFilter { Range = new string('a', RuleDocument.MaxFilterTextLength + 1) } },
        };
        var errors = filter.Validate().ToList();
        Assert.Contains(errors, e => e.Contains($"range too long (max {RuleDocument.MaxFilterTextLength})", StringComparison.Ordinal));
    }

    [Fact]
    public void GridFilters_ExactlyMaxTextLength_NotRejected()
    {
        var filter = new GridFiltersFilter
        {
            Filters = { ["name"] = new ColumnFilter { Text = new string('a', RuleDocument.MaxFilterTextLength) } },
        };
        var errors = filter.Validate().ToList();
        Assert.DoesNotContain(errors, e => e.Contains("text too long", StringComparison.Ordinal));
    }

    [Fact]
    public void GridFilters_OverMaxTextLength_Rejected()
    {
        var filter = new GridFiltersFilter
        {
            Filters = { ["name"] = new ColumnFilter { Text = new string('a', RuleDocument.MaxFilterTextLength + 1) } },
        };
        var errors = filter.Validate().ToList();
        Assert.Contains(errors, e => e.Contains($"text too long (max {RuleDocument.MaxFilterTextLength})", StringComparison.Ordinal));
    }
}
