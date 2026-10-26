using System.Linq.Expressions;
using System.Reflection;
using RouteBaseWeb.Models;

namespace RouteBaseWeb.Helpers;

public static class ActivityTypeHelper
{
    // Single source of truth for the substring keywords defining each type group. Used by both the
    // in-memory GetTypeGroup mapping and the EF-translated ApplyTypeGroupFilter predicates so the two
    // can never drift. Order matters for GetTypeGroup only (first match wins → "trail_hike" is a run).
    private static readonly string[] BikeKeywords = ["cycl", "bik", "ride", "mtb"];
    private static readonly string[] RunKeywords  = ["run", "trail"];
    private static readonly string[] WalkKeywords = ["walk", "hik"];
    private static readonly string[] AllGroupKeywords = [.. BikeKeywords, .. RunKeywords, .. WalkKeywords];

    /// <summary>
    /// Maps a raw activity type string to a canonical group name.
    /// Returns "bike", "run", "walk", or "other".
    /// </summary>
    public static string GetTypeGroup(string? activityType)
    {
        if (string.IsNullOrWhiteSpace(activityType))
            return "other";

        // CA1308: lowercase is load-bearing — the keyword sets are lowercase and the EF path (KeywordPredicate)
        // matches SQL LOWER(); ToUpperInvariant would never match "cycl"/"bik"/… and would drift the two paths.
#pragma warning disable CA1308
        var t = activityType.ToLowerInvariant();
#pragma warning restore CA1308

        if (BikeKeywords.Any(t.Contains)) return "bike";
        if (RunKeywords.Any(t.Contains))  return "run";
        if (WalkKeywords.Any(t.Contains)) return "walk";

        return "other";
    }

    /// <summary>
    /// Filters an activity query by type group ("bike", "run", "walk", "other") using the same
    /// substring matching as <see cref="GetTypeGroup"/>. Any other non-empty value falls back to an
    /// exact <c>ActivityType</c> match. A null/empty <paramref name="typeGroup"/> returns the query unchanged.
    /// </summary>
    public static IQueryable<Activity> ApplyTypeGroupFilter(IQueryable<Activity> query, string? typeGroup)
    {
        if (string.IsNullOrEmpty(typeGroup))
            return query;

        return typeGroup switch
        {
            "bike"  => query.Where(KeywordPredicate(BikeKeywords, negate: false)),
            "run"   => query.Where(KeywordPredicate(RunKeywords, negate: false)),
            "walk"  => query.Where(KeywordPredicate(WalkKeywords, negate: false)),
            "other" => query.Where(KeywordPredicate(AllGroupKeywords, negate: true)),
            _       => query.Where(a => a.ActivityType == typeGroup),
        };
    }

    private static readonly MethodInfo ToLowerMethod =
        typeof(string).GetMethod(nameof(string.ToLower), Type.EmptyTypes)
        ?? throw new InvalidOperationException("string lower-casing method vanished from the BCL");

    private static readonly MethodInfo ContainsMethod =
        typeof(string).GetMethod(nameof(string.Contains), new[] { typeof(string) })
        ?? throw new InvalidOperationException("string.Contains(string) overload vanished from the BCL");

    /// <summary>
    /// Builds an EF-translatable predicate over <see cref="Activity.ActivityType"/>. Positive form:
    /// <c>ActivityType != null &amp;&amp; (ToLower().Contains(kw) OR …)</c>. Negated form ("other"):
    /// <c>ActivityType == null || !(ToLower().Contains(kw) OR …)</c>. Uses <c>ToLower()</c> (not
    /// <c>ToLowerInvariant</c>) so EF Core emits the same SQL the hand-written chain did.
    /// </summary>
    private static Expression<Func<Activity, bool>> KeywordPredicate(string[] keywords, bool negate)
    {
        var param = Expression.Parameter(typeof(Activity), "a");
        var activityType = Expression.Property(param, nameof(Activity.ActivityType));
        var nullConst = Expression.Constant(null, typeof(string));
        var lowered = Expression.Call(activityType, ToLowerMethod);

        Expression? chain = null;
        foreach (var kw in keywords)
        {
            var call = Expression.Call(lowered, ContainsMethod, Expression.Constant(kw));
            chain = chain is null ? call : Expression.OrElse(chain, call);
        }

        if (chain is null)
            throw new InvalidOperationException("keyword groups are never empty — see AllGroupKeywords");
        var body = negate
            ? Expression.OrElse(Expression.Equal(activityType, nullConst), Expression.Not(chain))
            : Expression.AndAlso(Expression.NotEqual(activityType, nullConst), chain);

        return Expression.Lambda<Func<Activity, bool>>(body, param);
    }

    /// <summary>
    /// Filters an activity query to the given exact raw <c>ActivityType</c> values (multi-select).
    /// A null/empty list returns the query unchanged. EF Core translates the membership test to a
    /// SQL <c>IN</c> clause.
    /// </summary>
    public static IQueryable<Activity> ApplyTypesFilter(IQueryable<Activity> query, IReadOnlyList<string>? types)
        => query.WhereInSet(a => a.ActivityType, types);
}
