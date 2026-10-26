using RouteBaseWeb.Helpers.Grid;
using RouteBaseWeb.Models.GridFilter;
using RouteBaseWeb.Services.Segments;
using RouteBaseWeb.Services.Segments.Rules;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Segments;

/// <summary>
/// The matcher-side raw-SQL translation of adopted grid filters. Guards that (a) the registry spec drives
/// the same physical column / scale as the EF path, (b) each column kind renders the mirror SQL, and (c) the
/// registry is a strict allowlist — unknown / non-filterable keys never reach a column.
/// </summary>
public class GridFilterSqlTranslatorTests
{
    /// <summary>A positional-parameter recorder mirroring the matcher's <c>P</c> helper.</summary>
    private static Func<object, string> Recorder(List<object> args)
        => v => { var ph = "{" + args.Count + "}"; args.Add(v); return ph; };

    private static GridColumnSqlSpec Spec(string key)
    {
        Assert.True(ActivityGridColumns.Instance.TryGetSqlSpec(key, out var spec), $"no spec for '{key}'");
        return spec!;
    }

    private static (string? Sql, List<object> Args) Render(string key, ColumnFilter value)
    {
        var args = new List<object>();
        var sql = GridFilterSqlTranslator.Translate(Spec(key), value, Recorder(args));
        return (sql, args);
    }

    // ---------------------------------------------------------------- registry spec guard

    /// <summary>Every filterable activities column resolves a spec with the expected physical column, kind and
    /// display→storage scale. Break this → the EF path and the raw-SQL path have drifted.</summary>
    [Theory]
    [InlineData("date", "start_datetime_loc", GridColumnKind.DateRange, 1.0)]
    [InlineData("name", "track_name", GridColumnKind.Text, 1.0)]
    [InlineData("type", "activity_type", GridColumnKind.Combo, 1.0)]
    [InlineData("distance", "total_distance", GridColumnKind.NumberRange, 1000.0)]
    [InlineData("time", "time_total", GridColumnKind.NumberRange, 60.0)]
    [InlineData("movingtime", "time_moving", GridColumnKind.NumberRange, 60.0)]
    [InlineData("avgspeed", "speed_avg", GridColumnKind.NumberRange, 1.0 / 3.6)]
    [InlineData("maxspeed", "speed_max", GridColumnKind.NumberRange, 1.0 / 3.6)]
    [InlineData("avghr", "heart_rate_avg", GridColumnKind.NumberRange, 1.0)]
    [InlineData("avgpower", "power_avg", GridColumnKind.NumberRange, 1.0)]
    public void Spec_ForFilterableColumn_MatchesDeclaration(string key, string column, GridColumnKind kind, double scale)
    {
        var spec = Spec(key);
        Assert.Equal(column, spec.Column);
        Assert.Equal(kind, spec.Kind);
        Assert.Equal(scale, spec.Scale, precision: 10);
    }

    [Fact]
    public void EveryRegisteredKey_IsFilterable_ExceptOwner()
    {
        // Every filterable column resolves a spec (physical column, or a raw expression for aggregates like
        // photos) so the EF and raw-SQL paths stay in agreement; owner is the only sort-only exception.
        foreach (var key in ActivityGridColumns.Instance.Keys)
        {
            var hasSpec = ActivityGridColumns.Instance.TryGetSqlSpec(key, out _);
            if (key == "owner")
                Assert.False(hasSpec, "owner is sort-only (filterable:false) — it must resolve no SQL spec");
            else
                Assert.True(hasSpec, $"filterable column '{key}' must resolve a SQL spec");
        }
    }

    // ---------------------------------------------------------------- per-kind rendering

    [Fact]
    public void NumberRange_ClosedRange_ScalesBothBoundsToStorage()
    {
        var (sql, args) = Render("distance", new ColumnFilter { Range = "10-20" });   // km → m ×1000
        Assert.Equal("(a.total_distance >= {0} AND a.total_distance <= {1})", sql);
        Assert.Equal(new object[] { 10000.0, 20000.0 }, args);
    }

    [Fact]
    public void NumberRange_OpenLowerBound_ScalesSpeed()
    {
        var (sql, args) = Render("avgspeed", new ColumnFilter { Range = ">18" });      // km/h → m/s ÷3.6
        Assert.Equal("a.speed_avg >= {0}", sql);
        Assert.Equal(5.0, Assert.Single(args));
    }

    [Fact]
    public void NumberRange_MultiRange_OrsEachScaledRange()
    {
        var (sql, args) = Render("time", new ColumnFilter { Range = "30-60, 90-120" }); // min → s ×60
        Assert.Equal(
            "((a.time_total >= {0} AND a.time_total <= {1}) OR (a.time_total >= {2} AND a.time_total <= {3}))",
            sql);
        Assert.Equal(new object[] { 1800.0, 3600.0, 5400.0, 7200.0 }, args);
    }

    [Fact]
    public void NumberRange_LegacyMinMax_Scaled()
    {
        var (sql, args) = Render("avghr", new ColumnFilter { Min = "120", Max = "160" }); // scale 1
        Assert.Equal("(a.heart_rate_avg >= {0} AND a.heart_rate_avg <= {1})", sql);
        Assert.Equal(new object[] { 120.0, 160.0 }, args);
    }

    [Fact]
    public void NumberRange_PhotosAggregate_RendersCorrelatedCountSubquery()
    {
        // The photos column has no physical column: its spec is a raw COUNT subquery expression (IsRawExpression),
        // used verbatim (no `a.` prefix, no unit scale) so the paint/adopt filter matches the grid's "has photos".
        var (sql, args) = Render("photos", new ColumnFilter { Range = ">1" }); // ≥1 photo
        Assert.Equal("(SELECT COUNT(*) FROM Photos ph WHERE ph.track_id = a.track_ID) >= {0}", sql);
        Assert.Equal(1.0, Assert.Single(args));
    }

    [Fact]
    public void Text_WildcardTerm_RendersLikeEscape()
    {
        var (sql, args) = Render("name", new ColumnFilter { Text = "*morning*" });
        Assert.Equal("a.track_name LIKE {0} ESCAPE '\\'", sql);
        Assert.Equal("%morning%", Assert.Single(args));
    }

    [Fact]
    public void Text_NoWildcard_FallsBackToContains()
    {
        var (_, args) = Render("name", new ColumnFilter { Text = "abc" });
        Assert.Equal("%abc%", Assert.Single(args));
    }

    [Fact]
    public void Text_OrTerms_RendersOredLikes()
    {
        var (sql, args) = Render("name", new ColumnFilter { Text = "run|bike" });
        Assert.Equal("(a.track_name LIKE {0} ESCAPE '\\' OR a.track_name LIKE {1} ESCAPE '\\')", sql);
        Assert.Equal(new object[] { "%run%", "%bike%" }, args);
    }

    [Fact]
    public void Text_NegatedTerm_RendersNullSafeNotLike()
    {
        // "!*morning*" → include NULLs (can't contain the term) + NOT LIKE for the rest.
        var (sql, args) = Render("name", new ColumnFilter { Text = "!*morning*" });
        Assert.Equal("(a.track_name IS NULL OR a.track_name NOT LIKE {0} ESCAPE '\\')", sql);
        Assert.Equal("%morning%", Assert.Single(args));
    }

    [Fact]
    public void Text_MixedPositiveAndNegated_AndsExclusionOntoPositives()
    {
        // "run|!*loop*" → (contains run) AND (null-safe NOT contains loop).
        var (sql, args) = Render("name", new ColumnFilter { Text = "run|!*loop*" });
        Assert.Equal(
            "(a.track_name LIKE {0} ESCAPE '\\' AND (a.track_name IS NULL OR a.track_name NOT LIKE {1} ESCAPE '\\'))",
            sql);
        Assert.Equal(new object[] { "%run%", "%loop%" }, args);
    }

    [Fact]
    public void Combo_TextAndIn_OrsLikeWithInSet()
    {
        var (sql, args) = Render("type", new ColumnFilter { Text = "run", In = { "Ride", "Hike" } });
        Assert.Equal("(a.activity_type LIKE {0} ESCAPE '\\' OR a.activity_type IN ({1},{2}))", sql);
        Assert.Equal(new object[] { "%run%", "Ride", "Hike" }, args);
    }

    [Fact]
    public void Combo_InOnly_RendersInSet()
    {
        var (sql, args) = Render("type", new ColumnFilter { In = { "Run" } });
        Assert.Equal("a.activity_type IN ({0})", sql);
        Assert.Equal("Run", Assert.Single(args));
    }

    [Fact]
    public void MultiSelect_RendersInSet()
    {
        var args = new List<object>();
        var spec = new GridColumnSqlSpec("activity_type", GridColumnKind.MultiSelect, 1);
        var sql = GridFilterSqlTranslator.Translate(spec, new ColumnFilter { In = { "Run", "Ride" } }, Recorder(args));
        Assert.Equal("a.activity_type IN ({0},{1})", sql);
        Assert.Equal(new object[] { "Run", "Ride" }, args);
    }

    [Fact]
    public void UnparseableRange_YieldsNull()
    {
        var (sql, args) = Render("distance", new ColumnFilter { Range = "not-a-number" });
        Assert.Null(sql);
        Assert.Empty(args);
    }

    // SC-02: "NaN"/"Infinity" parse to non-finite doubles that TryParse accepts but SQLite refuses to
    // bind — reject them so no predicate (and no throwing SQL param) is produced.
    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    public void NumberRange_NaN_Range_Yields_No_Predicate(string raw)
    {
        var (sql, args) = Render("distance", new ColumnFilter { Range = raw });
        Assert.Null(sql);
        Assert.Empty(args);
    }

    [Fact]
    public void NumberRange_NaN_LegacyMin_Yields_No_Predicate()
    {
        var (sql, args) = Render("avghr", new ColumnFilter { Min = "NaN" });
        Assert.Null(sql);
        Assert.Empty(args);
    }

    // kill the surviving/no-coverage mutants below.

    // Mut 51 (`legacyMin.HasValue || legacyMax.HasValue` → `&&`) + mut 133 (`v * scale` → `v / scale`):
    // a legacy Min alone still produces a lower-bound predicate, and the bound is scaled by MULTIPLICATION
    // (distance km→m ×1000). `&&` would drop the one-sided legacy range; `/` would yield 0.01 not 10000.
    [Fact]
    public void NumberRange_LegacyMinOnly_ProducesScaledLowerBound()
    {
        var (sql, args) = Render("distance", new ColumnFilter { Min = "10" });   // km → m ×1000
        Assert.Equal("a.total_distance >= {0}", sql);
        Assert.Equal(10000.0, Assert.Single(args));
    }

    // Mut 57 (equality `min > max` → `min >= max`): an equal-bounds legacy range (Min == Max) is a valid
    // exact-match constraint and must NOT be dropped as "reversed".
    [Fact]
    public void NumberRange_LegacyEqualBounds_AreKept()
    {
        var (sql, args) = Render("avghr", new ColumnFilter { Min = "10", Max = "10" });
        Assert.Equal("(a.heart_rate_avg >= {0} AND a.heart_rate_avg <= {1})", sql);
        Assert.Equal(new object[] { 10.0, 10.0 }, args);
    }

    // Mut 57 (statement `continue;` removed): a genuinely reversed legacy range (Min > Max) MUST be dropped;
    // without the drop the mutant emits an unsatisfiable `(col >= 20 AND col <= 10)`.
    [Fact]
    public void NumberRange_LegacyReversedBounds_YieldNoPredicate()
    {
        var (sql, args) = Render("avghr", new ColumnFilter { Min = "20", Max = "10" });
        Assert.Null(sql);
        Assert.Empty(args);
    }

    // Mut 80 (statement `continue;` removed): a bare `!` is no constraint — an empty term must be skipped,
    // not turned into a LIKE over an empty pattern.
    [Fact]
    public void Text_BareNegation_YieldsNoPredicate()
    {
        var (sql, args) = Render("name", new ColumnFilter { Text = "!" });
        Assert.Null(sql);
        Assert.Empty(args);
    }

    // Mut 98 (`values is null || values.Count == 0` → `&&`): an EMPTY set renders no predicate. The mutant
    // would emit `col IN ()` — invalid SQL that also silently matches nothing.
    [Fact]
    public void MultiSelect_EmptySet_YieldsNoPredicate()
    {
        var args = new List<object>();
        var spec = new GridColumnSqlSpec("activity_type", GridColumnKind.MultiSelect, 1);
        var sql = GridFilterSqlTranslator.Translate(spec, new ColumnFilter(), Recorder(args));
        Assert.Null(sql);
        Assert.Empty(args);
    }

    // Mut 125 (the three string literals `"("`, `" AND "`, `")"` in And() → `""`): two negated text terms
    // AND their null-safe NOT-LIKE exclusions together, parenthesised. Blanking any literal breaks the SQL.
    [Fact]
    public void Text_TwoNegatedTerms_AndsExclusionsParenthesised()
    {
        var (sql, args) = Render("name", new ColumnFilter { Text = "!*a*|!*b*" });
        Assert.Equal(
            "((a.track_name IS NULL OR a.track_name NOT LIKE {0} ESCAPE '\\') AND " +
            "(a.track_name IS NULL OR a.track_name NOT LIKE {1} ESCAPE '\\'))",
            sql);
        Assert.Equal(new object[] { "%a%", "%b%" }, args);
    }

    // ---------------------------------------------------------------- allowlist / security via Build

    [Fact]
    public void Build_IgnoresOwnerAndUnknownGridKeys_RendersOnlyAllowedColumns()
    {
        var doc = new GridFiltersFilter
        {
            Filters =
            {
                ["owner"] = new ColumnFilter { In = { "attacker-id" } },     // filterable:false → ignored
                ["bogus"] = new ColumnFilter { Text = "x" },                 // unknown key → ignored
                ["distance"] = new ColumnFilter { Range = "10-20" },
            },
        };

        var filters = CandidateSqlFilters.Build(new TrackFilter[] { doc }, legacyOwnerCsv: null, currentUserId: "me");

        // Exactly the mandatory owner-scope filter + the one allowed grid column.
        Assert.Equal(2, filters.Count);
        Assert.IsType<OwnerSqlFilter>(filters[0]);
        var grid = Assert.IsType<GridColumnSqlFilter>(filters[1]);

        var args = new List<object>();
        var sql = grid.ToSql(Recorder(args));
        Assert.Equal("(a.total_distance >= {0} AND a.total_distance <= {1})", sql);
        Assert.DoesNotContain("attacker-id", args);          // the adopted "owner" key never reached user_id
    }

    [Fact]
    public void Build_DateGridFilter_ReusesDateRangeSqlFilter()
    {
        var doc = new GridFiltersFilter
        {
            Filters = { ["date"] = new ColumnFilter { From = "2025-06-01", To = "2025-06-30" } },
        };

        var filters = CandidateSqlFilters.Build(new TrackFilter[] { doc }, legacyOwnerCsv: null, currentUserId: "me");

        Assert.Equal(2, filters.Count);
        var date = Assert.IsType<DateRangeSqlFilter>(filters[1]);

        var args = new List<object>();
        var sql = date.ToSql(Recorder(args));
        Assert.Contains("datetime(a.start_datetime_loc) >=", sql, StringComparison.Ordinal);
        Assert.Contains("datetime(a.start_datetime_loc) <=", sql, StringComparison.Ordinal);
        // End-day inclusive: upper bound advanced to the following midnight.
        Assert.Contains("2025-07-01 00:00:00", args.Select(a => a.ToString()), StringComparer.Ordinal);
    }

    [Fact]
    public void Build_EmptyGridColumnValues_AreSkipped()
    {
        var doc = new GridFiltersFilter
        {
            Filters = { ["distance"] = new ColumnFilter(), ["name"] = new ColumnFilter { Text = "  " } },
        };

        var filters = CandidateSqlFilters.Build(new TrackFilter[] { doc }, legacyOwnerCsv: null, currentUserId: "me");

        Assert.Single(filters);                 // only the mandatory owner-scope filter
        Assert.IsType<OwnerSqlFilter>(filters[0]);
    }
}
