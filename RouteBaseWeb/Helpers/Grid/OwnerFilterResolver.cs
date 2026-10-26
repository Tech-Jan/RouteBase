using System.Text;
using System.Text.RegularExpressions;
using RouteBaseWeb.Constants;
using RouteBaseWeb.Models.GridFilter;

namespace RouteBaseWeb.Helpers.Grid;

/// <summary>
/// Translates an "owner" combo filter into a concrete set of user ids. Owner is stored as <c>UserId</c> but
/// presented/filtered in identity space: the checkbox tokens <see cref="Me"/>/<see cref="Others"/>/
/// <see cref="Everyone"/>, plus a free-text wildcard search of people's display names. Because the visible-user
/// set is small, name matching is done in memory here (no SQL), and the result is applied with a single
/// <c>WhereInSet(a =&gt; a.UserId, …)</c>. Reusable by any grid that has an identity/owner column.
/// </summary>
public static class OwnerFilterResolver
{
    /// <summary>Checkbox token: the current user's own rows.</summary>
    public const string Me = "__me__";

    /// <summary>Checkbox token: everyone else (all visible users except the current one).</summary>
    public const string Others = "__others__";

    /// <summary>Checkbox token: all visible users (no constraint).</summary>
    public const string Everyone = "__everyone__";

    /// <summary>
    /// The outcome of resolving an owner filter.
    /// <list type="bullet">
    ///   <item><see cref="NoConstraint"/> — match all visible rows (empty filter, or <see cref="Everyone"/>).</item>
    ///   <item><see cref="MatchNone"/> — the filter was non-empty but matched nobody → return no rows.</item>
    ///   <item><see cref="UserIds"/> — otherwise, restrict to these user ids (exact <c>IN</c>).</item>
    /// </list>
    /// </summary>
    public sealed record Result(bool NoConstraint, bool MatchNone, IReadOnlyList<string> UserIds)
    {
        public static readonly Result Unconstrained = new(NoConstraint: true, MatchNone: false, Array.Empty<string>());
    }

    /// <summary>
    /// Resolves <paramref name="owner"/> against the current user and the visible users (id → display name).
    /// </summary>
    public static Result Resolve(
        ColumnFilter? owner, string currentUserId, IReadOnlyDictionary<string, string> idToDisplayName)
    {
        if (owner is null || owner.IsEmpty)
            return Result.Unconstrained;

        var ids = new HashSet<string>(StringComparer.Ordinal);

        foreach (var token in owner.In)
        {
            switch (token)
            {
                case Everyone:
                    return Result.Unconstrained;
                case Me:
                    ids.Add(currentUserId);
                    break;
                case Others:
                    foreach (var id in idToDisplayName.Keys)
                        if (!string.Equals(id, currentUserId, StringComparison.Ordinal))
                            ids.Add(id);
                    break;
                default:
                    // A concrete user id (defensive — the UI only offers tokens today). Only honour known ids.
                    if (idToDisplayName.ContainsKey(token))
                        ids.Add(token);
                    break;
            }
        }

        // Free-text wildcard search over display names (|-separated OR terms), mirroring WhereTextLike.
        var terms = GridFilterParsing.SplitOrTerms(owner.Text);
        if (terms.Count > 0)
        {
            var matchers = terms.Select(BuildMatcher).ToList();
            foreach (var (id, name) in idToDisplayName)
                if (name is not null && matchers.Any(m => m.IsMatch(name)))
                    ids.Add(id);
        }

        // Non-empty filter that resolved to nobody → match none (not "all").
        return ids.Count == 0
            ? new Result(NoConstraint: false, MatchNone: true, Array.Empty<string>())
            : new Result(NoConstraint: false, MatchNone: false, ids.ToList());
    }

    // Compiles one user term into a case-insensitive matcher. No wildcard → "contains"; `*`→any, `?`→one char.
    private static Regex BuildMatcher(string term)
    {
        var hasWildcard = term.Contains('*', StringComparison.Ordinal) || term.Contains('?', StringComparison.Ordinal);
        var sb = new StringBuilder();
        foreach (var c in term)
        {
            switch (c)
            {
                case '*': sb.Append(".*"); break;
                case '?': sb.Append('.'); break;
                default: sb.Append(Regex.Escape(c.ToString())); break;
            }
        }

        var body = hasWildcard ? $"^{sb}$" : sb.ToString();
        // The one user-controlled pattern in the codebase: a `*`-heavy term can backtrack pathologically
        // even though every other character is escaped, so the timeout here is load-bearing, not ritual.
        return new Regex(body, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexDefaults.MatchTimeout);
    }
}
