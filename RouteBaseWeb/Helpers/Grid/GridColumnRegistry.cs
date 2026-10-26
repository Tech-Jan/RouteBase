using System.ComponentModel.DataAnnotations.Schema;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using RouteBaseWeb.Models.GridFilter;

namespace RouteBaseWeb.Helpers.Grid;

/// <summary>
/// Filter behaviour of a grid column, derived from its data type. <see cref="None"/> means the column is
/// not filterable (it may still be sortable).
/// </summary>
public enum GridColumnKind
{
    None,
    Text,
    NumberRange,
    MultiSelect,
    DateRange,
    Combo,
}

/// <summary>
/// The raw-SQL translation spec for one filterable grid column: the physical DB column name (resolved from the
/// selector member's <see cref="ColumnAttribute"/>), its filter <see cref="GridColumnKind"/>, and the
/// display→storage <see cref="Scale"/>. Lets the matcher render the same filters as parameterised WHERE
/// predicates (see <c>GridFilterSqlTranslator</c>) without an EF round-trip, using the same one declaration.
/// When <paramref name="IsRawExpression"/> is true, <paramref name="Column"/> is a full SQL scalar expression
/// (already qualified with the candidate query's <c>a</c> alias — e.g. a correlated <c>COUNT</c> subquery) used
/// verbatim instead of the <c>a.&lt;column&gt;</c> prefix; this is how aggregate columns (photos) filter in the
/// raw-SQL path identically to EF.
/// </summary>
public sealed record GridColumnSqlSpec(string Column, GridColumnKind Kind, double Scale, bool IsRawExpression = false);

/// <summary>
/// Reusable, entity-agnostic descriptor for one grid column. Holds a strongly-typed selector so EF Core can
/// translate the predicate/order-by, and turns a wire <see cref="ColumnFilter"/> into the right predicate via
/// the generic helpers in <see cref="GridFilterExtensions"/>. One subclass per data type keeps the selector
/// at its real type (boxing to <c>Expression&lt;Func&lt;TEntity,object&gt;&gt;</c> would break EF translation).
///
/// This is the single source of truth that makes a column filterable AND sortable by declaring it once; see
/// <c>ActivityGridColumns</c> for the activity instance and <see cref="GridColumnRegistry{TEntity}"/> for the
/// registry that dispatches by key.
/// </summary>
public abstract class GridColumn<TEntity>
{
    protected GridColumn(string key, GridColumnKind kind, bool sortable, LambdaExpression selector)
    {
        Key = key;
        Kind = kind;
        Sortable = sortable;
        Column = ResolvePhysicalColumn(selector);
    }

    /// <summary>
    /// Aggregate/computed column ctor — no physical DB column, so it is excluded from the raw-SQL filter
    /// allowlist (<see cref="SqlSpec"/> is null). The grid's own filter/sort path is pure EF, which
    /// translates the aggregate expression to a subquery, so filtering and sorting still work.
    /// </summary>
    protected GridColumn(string key, GridColumnKind kind, bool sortable)
    {
        Key = key;
        Kind = kind;
        Sortable = sortable;
        Column = string.Empty;
    }

    /// <summary>Stable key — the sort column id and the filter param key on the wire.</summary>
    public string Key { get; }

    public GridColumnKind Kind { get; }

    public bool Sortable { get; }

    /// <summary>Physical DB column name, resolved once from the selector member's <see cref="ColumnAttribute"/>.</summary>
    public string Column { get; }

    /// <summary>Display→storage scale for the raw-SQL spec. 1 except numeric columns (overridden).</summary>
    protected virtual double SqlScale => 1;

    /// <summary>The raw-SQL spec for this column, or null when it is not filterable, or has no physical column
    /// and no expression override (see <see cref="CountColumn{TEntity}"/>). Virtual so aggregate/computed
    /// columns can supply a raw SQL expression instead of a physical column name.</summary>
    public virtual GridColumnSqlSpec? SqlSpec =>
        Kind == GridColumnKind.None || string.IsNullOrEmpty(Column) ? null : new(Column, Kind, SqlScale);

    /// <summary>Applies this column's filter predicate. No-op for empty values or non-filterable columns.</summary>
    public abstract IQueryable<TEntity> ApplyFilter(IQueryable<TEntity> query, ColumnFilter value);

    /// <summary>Orders the query by this column. Only valid when <see cref="Sortable"/> is true.</summary>
    public abstract IOrderedQueryable<TEntity> ApplySort(IQueryable<TEntity> query, bool asc);

    /// <summary>
    /// Resolves the physical column name from a simple property selector's <see cref="ColumnAttribute"/>.
    /// Fails fast on a non-member selector or a property without <c>[Column]</c> — the raw-SQL translator
    /// must never guess a column name.
    /// </summary>
    private static string ResolvePhysicalColumn(LambdaExpression selector)
    {
        var body = selector.Body is UnaryExpression { NodeType: ExpressionType.Convert } u ? u.Operand : selector.Body;
        if (body is not MemberExpression { Member: PropertyInfo prop })
            throw new ArgumentException($"Grid column selector must be a simple property access, got: {selector}", nameof(selector));

        var column = prop.GetCustomAttribute<ColumnAttribute>()?.Name;
        if (string.IsNullOrEmpty(column))
            throw new ArgumentException(
                $"Property {prop.DeclaringType?.Name}.{prop.Name} needs a [Column(\"...\")] attribute for raw-SQL filter translation.", nameof(selector));
        return column;
    }
}

/// <summary>
/// Numeric range column. <paramref name="scale"/> converts the user's display units to the stored units
/// (e.g. distance km→m = 1000, speed km/h→m/s = 1/3.6) — kept here so unit conversion lives in exactly one
/// place. NULL column values never match a range (see <see cref="GridFilterExtensions.WhereInRange{T}"/>).
/// </summary>
public sealed class NumberColumn<TEntity> : GridColumn<TEntity>
{
    private readonly Expression<Func<TEntity, double?>> _selector;
    private readonly double _scale;

    public NumberColumn(
        string key, Expression<Func<TEntity, double?>> selector,
        double scale = 1, bool sortable = true, bool filterable = true)
        : base(key, filterable ? GridColumnKind.NumberRange : GridColumnKind.None, sortable, selector)
    {
        _selector = selector;
        _scale = scale;
    }

    protected override double SqlScale => _scale;

    public override IQueryable<TEntity> ApplyFilter(IQueryable<TEntity> query, ColumnFilter value)
    {
        var ranges = new List<(double? Min, double? Max)>();

        // Single-box multi-range expression (3-6, 8-12, >5, <10, …), each bound scaled to storage units.
        foreach (var (min, max) in GridFilterParsing.ParseRanges(value.Range))
            ranges.Add((Scale(min), Scale(max)));

        // Legacy two-box min/max (and old fDistMin/fDistMax bookmarks): one range, min-only = ≥min.
        if (!string.IsNullOrWhiteSpace(value.Min) || !string.IsNullOrWhiteSpace(value.Max))
        {
            var min = ParseScaled(value.Min);
            var max = ParseScaled(value.Max);
            if (min.HasValue || max.HasValue)
                ranges.Add((min, max));
        }

        // Drop reversed ranges defensively (scaling by a positive factor preserves order).
        ranges = ranges.Where(r => !(r.Min.HasValue && r.Max.HasValue && r.Min > r.Max)).ToList();

        return query.WhereInRanges(_selector, ranges);
    }

    public override IOrderedQueryable<TEntity> ApplySort(IQueryable<TEntity> query, bool asc)
        => asc ? query.OrderBy(_selector) : query.OrderByDescending(_selector);

    private double? Scale(double? v) => v.HasValue ? v.Value * _scale : null;

    // SC-02: reject NaN/±Infinity (TryParse accepts them; a non-finite SQL param throws at bind time).
    private double? ParseScaled(string? raw)
        => double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var v)
           && double.IsFinite(v) ? v * _scale : null;
}

/// <summary>
/// Aggregate count column: sorts and range-filters by the number of related child rows (e.g. photos per
/// activity) through an EF collection-navigation <c>Count</c>, which EF translates to a correlated
/// <c>COUNT</c> subquery over the child's FK index. Counts are never null and carry no unit scale. It has no
/// physical DB column, so for the raw-SQL matcher path it supplies <paramref name="sqlCountExpression"/> — the
/// same correlated <c>COUNT</c> written as a SQL scalar expression (qualified with the candidate query's
/// <c>a</c> alias) — so the paint/adopt filter and segment matching filter it identically to the grid.
/// </summary>
public sealed class CountColumn<TEntity> : GridColumn<TEntity>
{
    private readonly Expression<Func<TEntity, int>> _selector;
    // The int count projected to double? so the shared numeric-range helpers (and their NULL-safe
    // comparisons) can be reused verbatim; a count is never actually null.
    private readonly Expression<Func<TEntity, double?>> _rangeSelector;
    private readonly string _sqlCountExpression;

    public CountColumn(
        string key, Expression<Func<TEntity, int>> selector, string sqlCountExpression,
        bool sortable = true, bool filterable = true)
        : base(key, filterable ? GridColumnKind.NumberRange : GridColumnKind.None, sortable)
    {
        _selector = selector;
        _sqlCountExpression = sqlCountExpression;
        var body = Expression.Convert(selector.Body, typeof(double?));
        _rangeSelector = Expression.Lambda<Func<TEntity, double?>>(body, selector.Parameters);
    }

    // Raw-SQL path: filter by the correlated COUNT subquery expression (no unit scale, no physical column).
    public override GridColumnSqlSpec? SqlSpec =>
        Kind == GridColumnKind.None ? null : new(_sqlCountExpression, Kind, 1, IsRawExpression: true);

    public override IQueryable<TEntity> ApplyFilter(IQueryable<TEntity> query, ColumnFilter value)
    {
        var ranges = new List<(double? Min, double? Max)>();

        // Single-box multi-range expression (>0, 1-5, …). No unit scale for a raw count.
        foreach (var (min, max) in GridFilterParsing.ParseRanges(value.Range))
            ranges.Add((min, max));

        // Legacy two-box min/max.
        if (!string.IsNullOrWhiteSpace(value.Min) || !string.IsNullOrWhiteSpace(value.Max))
        {
            var min = Parse(value.Min);
            var max = Parse(value.Max);
            if (min.HasValue || max.HasValue)
                ranges.Add((min, max));
        }

        ranges = ranges.Where(r => !(r.Min.HasValue && r.Max.HasValue && r.Min > r.Max)).ToList();

        return query.WhereInRanges(_rangeSelector, ranges);
    }

    public override IOrderedQueryable<TEntity> ApplySort(IQueryable<TEntity> query, bool asc)
        => asc ? query.OrderBy(_selector) : query.OrderByDescending(_selector);

    // SC-02: reject NaN/±Infinity (TryParse accepts them; a non-finite SQL param throws at bind time).
    private static double? Parse(string? raw)
        => double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var v)
           && double.IsFinite(v) ? v : null;
}

/// <summary>Free-text wildcard column (SQL <c>LIKE</c>, <c>*</c>/<c>?</c> wildcards).</summary>
public sealed class TextColumn<TEntity> : GridColumn<TEntity>
{
    private readonly Expression<Func<TEntity, string?>> _selector;

    public TextColumn(string key, Expression<Func<TEntity, string?>> selector, bool sortable = true, bool filterable = true)
        : base(key, filterable ? GridColumnKind.Text : GridColumnKind.None, sortable, selector)
        => _selector = selector;

    public override IQueryable<TEntity> ApplyFilter(IQueryable<TEntity> query, ColumnFilter value)
        => query.WhereTextLike(_selector, value.Text);

    public override IOrderedQueryable<TEntity> ApplySort(IQueryable<TEntity> query, bool asc)
        => asc ? query.OrderBy(_selector) : query.OrderByDescending(_selector);
}

/// <summary>
/// Combo column: a wildcard text box AND a checkbox picker over the same property, OR-combined
/// (<see cref="GridFilterExtensions.WhereTextLikeOrInSet{T}"/>). Typed text uses <c>LIKE</c>/<c>|</c>-OR;
/// checked options match exactly (SQL <c>IN</c>).
/// </summary>
public sealed class ComboColumn<TEntity> : GridColumn<TEntity>
{
    private readonly Expression<Func<TEntity, string?>> _selector;

    public ComboColumn(string key, Expression<Func<TEntity, string?>> selector, bool sortable = true)
        : base(key, GridColumnKind.Combo, sortable, selector)
        => _selector = selector;

    public override IQueryable<TEntity> ApplyFilter(IQueryable<TEntity> query, ColumnFilter value)
        => query.WhereTextLikeOrInSet(_selector, value.Text, value.In);

    public override IOrderedQueryable<TEntity> ApplySort(IQueryable<TEntity> query, bool asc)
        => asc ? query.OrderBy(_selector) : query.OrderByDescending(_selector);
}

/// <summary>Multi-select membership column (SQL <c>IN</c>).</summary>
public sealed class MultiSelectColumn<TEntity> : GridColumn<TEntity>
{
    private readonly Expression<Func<TEntity, string?>> _selector;

    public MultiSelectColumn(string key, Expression<Func<TEntity, string?>> selector, bool sortable = true)
        : base(key, GridColumnKind.MultiSelect, sortable, selector)
        => _selector = selector;

    public override IQueryable<TEntity> ApplyFilter(IQueryable<TEntity> query, ColumnFilter value)
        => query.WhereInSet(_selector, value.In);

    public override IOrderedQueryable<TEntity> ApplySort(IQueryable<TEntity> query, bool asc)
        => asc ? query.OrderBy(_selector) : query.OrderByDescending(_selector);
}

/// <summary>
/// Date-range column. Date-only bounds are inclusive of the whole end day (the upper bound is advanced one
/// day, preserving the existing activities-grid behaviour). NULL dates never match.
/// </summary>
public sealed class DateColumn<TEntity> : GridColumn<TEntity>
{
    private readonly Expression<Func<TEntity, DateTime?>> _selector;

    public DateColumn(string key, Expression<Func<TEntity, DateTime?>> selector, bool sortable = true, bool filterable = true)
        : base(key, filterable ? GridColumnKind.DateRange : GridColumnKind.None, sortable, selector)
        => _selector = selector;

    public override IQueryable<TEntity> ApplyFilter(IQueryable<TEntity> query, ColumnFilter value)
    {
        var from = ParseDate(value.From);
        var to = ParseDate(value.To);
        // Inclusive of the whole end day: advance the upper bound by one day (matches the legacy filter).
        // SC-05: a 9999-12-31 bound overflows AddDays(1); clamp so a validated max date can't throw.
        var upper = to is { } t && t < DateTime.MaxValue.Date ? t.AddDays(1) : to;
        return query.WhereDateInRange(_selector, from, upper);
    }

    public override IOrderedQueryable<TEntity> ApplySort(IQueryable<TEntity> query, bool asc)
        => asc ? query.OrderBy(_selector) : query.OrderByDescending(_selector);

    private static DateTime? ParseDate(string? raw)
        => DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
}

/// <summary>
/// A grid's column registry: the allowlist + dispatch table mapping column key → <see cref="GridColumn{TEntity}"/>.
/// Both filtering and sorting go through it, so only registered keys can ever reach a property (security), and
/// a column becomes filterable/sortable by being declared here exactly once. Subclass it per grid (e.g.
/// <c>ActivityGridColumns</c>).
/// </summary>
public abstract class GridColumnRegistry<TEntity>
{
    private readonly IReadOnlyDictionary<string, GridColumn<TEntity>> _columns;

    protected GridColumnRegistry(IEnumerable<GridColumn<TEntity>> columns, string defaultSortKey)
    {
        _columns = columns.ToDictionary(c => c.Key, StringComparer.OrdinalIgnoreCase);
        DefaultSortKey = defaultSortKey;
        if (!_columns.ContainsKey(defaultSortKey))
            throw new ArgumentException($"Default sort key '{defaultSortKey}' is not a registered column.", nameof(defaultSortKey));
    }

    /// <summary>Fallback sort key used when an unknown/non-sortable key is requested.</summary>
    public string DefaultSortKey { get; }

    /// <summary>The registered column keys (the filter/sort allowlist).</summary>
    public IReadOnlyCollection<string> Keys => _columns.Keys.ToArray();

    /// <summary>True when <paramref name="key"/> is a registered column.</summary>
    public bool Contains(string key) => _columns.ContainsKey(key);

    /// <summary>
    /// Gets the raw-SQL spec for a registered, filterable column. Returns false for unknown keys and for
    /// non-filterable columns (e.g. <c>owner</c>) — this is the allowlist the matcher's SQL translation trusts.
    /// </summary>
    public bool TryGetSqlSpec(string key, [NotNullWhen(true)] out GridColumnSqlSpec? spec)
    {
        if (_columns.TryGetValue(key, out var col) && col.SqlSpec is { } s)
        {
            spec = s;
            return true;
        }
        spec = null;
        return false;
    }

    /// <summary>
    /// Applies every supplied column filter whose key is registered and filterable. Unknown keys are ignored
    /// (the registry is the allowlist), and empty values are no-ops.
    /// </summary>
    public IQueryable<TEntity> ApplyFilters(IQueryable<TEntity> query, IReadOnlyDictionary<string, ColumnFilter> filters)
    {
        foreach (var (key, value) in filters)
        {
            if (value is null || value.IsEmpty) continue;
            if (_columns.TryGetValue(key, out var col) && col.Kind != GridColumnKind.None)
                query = col.ApplyFilter(query, value);
        }

        return query;
    }

    /// <summary>
    /// Orders the query by the given column key, falling back to <see cref="DefaultSortKey"/> when the key is
    /// unknown or the column is not sortable.
    /// </summary>
    public IOrderedQueryable<TEntity> ApplySort(IQueryable<TEntity> query, string? key, bool asc)
    {
        if (key is null || !_columns.TryGetValue(key, out var col) || !col.Sortable)
            col = _columns[DefaultSortKey];
        return col.ApplySort(query, asc);
    }
}
