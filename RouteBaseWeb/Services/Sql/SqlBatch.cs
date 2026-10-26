using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace RouteBaseWeb.Services.Sql;

/// <summary>
/// Builds and executes a raw SQL statement without string interpolation.
/// Eliminates EF1002 warnings by keeping SQL bodies as <c>@"..."</c> literals while
/// supporting named SQLite parameters and structural <c>/*{token}*/</c> replacement.
/// </summary>
public sealed class SqlBatch
{
    private string _sql;
    private readonly List<SqliteParameter> _params = new();
    private readonly HashSet<string> _paramNames = new(StringComparer.Ordinal);

    public SqlBatch(string sql) => _sql = sql;

    /// The SQL body after all token replacements. Exposed for testing.
    public string Sql => _sql;

    /// The registered named parameters. Exposed for testing.
    public IReadOnlyList<SqliteParameter> Params => _params;

    /// <summary>
    /// Replaces the <c>/*{<paramref name="token"/>}*/</c> comment placeholder with
    /// <paramref name="fragment"/>. Use only for structural SQL variation that is never
    /// derived from user input.
    /// </summary>
    public SqlBatch Token(string token, string fragment)
    {
        _sql = _sql.Replace($"/*{{{token}}}*/", fragment, StringComparison.Ordinal);
        return this;
    }

    /// <summary>
    /// Registers a named SQLite parameter (e.g. <c>$userId</c>) and returns its name
    /// for embedding in SQL fragments. Duplicate names are silently ignored — first wins.
    /// </summary>
    public string Param(string name, object value)
    {
        // K1 tripwire: a strong-id struct passed raw would bind as a BLOB and match nothing.
        RouteBaseWeb.Services.TrackEdit.SqlBindGuard.ThrowIfUnbindable(value, name);
        if (_paramNames.Add(name))
            _params.Add(new SqliteParameter(name, value));
        return name;
    }

    /// <summary>
    /// Registers a list of values as numbered parameters ($prefix0, $prefix1, …) and
    /// returns the comma-separated name list for use in an SQL IN() clause.
    /// Returns an empty string when <paramref name="values"/> is empty.
    /// </summary>
    public string ParamList(string prefix, IEnumerable<string> values)
    {
        var names = new List<string>();
        int i = 0;
        foreach (var v in values)
            names.Add(Param($"{prefix}{i++}", v));
        return string.Join(",", names);
    }

    /// <summary>Executes the SQL body with all registered parameters.</summary>
    public Task<int> ExecuteAsync(DatabaseFacade db, CancellationToken ct = default)
        => db.ExecuteSqlRawAsync(_sql, _params.Cast<object>(), ct);
}
