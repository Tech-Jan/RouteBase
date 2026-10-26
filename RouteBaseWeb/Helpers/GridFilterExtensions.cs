using System.Linq.Expressions;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace RouteBaseWeb.Helpers;

/// <summary>
/// Generic, reusable <see cref="IQueryable{T}"/> filter helpers for the grid filter row. These build
/// EF Core-translatable predicates from a column selector so any grid/entity can reuse them.
/// All return the query unchanged when the filter value is empty.
/// </summary>
public static class GridFilterExtensions
{
    /// <summary>
    /// Wildcard text filter translated to SQL <c>LIKE ... ESCAPE '\'</c>. User <c>*</c> → <c>%</c> and
    /// <c>?</c> → <c>_</c>; literal <c>%</c>/<c>_</c>/<c>\</c> in the input are escaped. When a term
    /// contains no wildcard it falls back to a "contains" match (<c>%term%</c>). The pattern may contain
    /// <c>|</c>-separated alternatives (e.g. <c>*run|bike</c>), which are OR'd together. A single term
    /// produces the same predicate as before.
    /// </summary>
    public static IQueryable<T> WhereTextLike<T>(
        this IQueryable<T> query, Expression<Func<T, string?>> selector, string? pattern)
    {
        var predicate = BuildTextLikePredicate(selector, pattern);
        return predicate is null ? query : query.Where(predicate);
    }

    /// <summary>
    /// Builds the wildcard text-LIKE predicate (the OR of one <c>EF.Functions.Like</c> per <c>|</c>-term),
    /// or <c>null</c> when there is no usable pattern. Shared by <see cref="WhereTextLike{T}"/> and
    /// <see cref="WhereTextLikeOrInSet{T}"/> so the combo column can OR it with a set membership test.
    /// </summary>
    public static Expression<Func<T, bool>>? BuildTextLikePredicate<T>(
        Expression<Func<T, string?>> selector, string? pattern)
    {
        var terms = GridFilterParsing.SplitOrTerms(pattern);
        if (terms.Count == 0)
            return null;

        var likeMethod = typeof(DbFunctionsExtensions).GetMethod(
            nameof(DbFunctionsExtensions.Like),
            new[] { typeof(DbFunctions), typeof(string), typeof(string), typeof(string) })
            ?? throw new InvalidOperationException("EF.Functions.Like(3-arg) vanished");

        Expression Like(string term) => Expression.Call(
            likeMethod,
            Expression.Constant(EF.Functions),
            selector.Body,
            Expression.Constant(ToLikePattern(term)),
            Expression.Constant("\\"));

        // Positive terms OR together; negative terms (leading ! / !=) are exclusions AND'd on top. A negation
        // includes NULL — a NULL column can't contain the term — as (col IS NULL OR NOT col LIKE pattern).
        Expression? posBody = null;
        Expression? negBody = null;
        foreach (var raw in terms)
        {
            var (negated, term) = GridFilterParsing.StripNegation(raw);
            if (term.Length == 0) continue; // bare `!` → no constraint
            if (negated)
            {
                var isNull = Expression.Equal(selector.Body, Expression.Constant(null, typeof(string)));
                var notLike = Expression.OrElse(isNull, Expression.Not(Like(term)));
                negBody = negBody is null ? notLike : Expression.AndAlso(negBody, notLike);
            }
            else
            {
                var like = Like(term);
                posBody = posBody is null ? like : Expression.OrElse(posBody, like);
            }
        }

        Expression? body = (posBody, negBody) switch
        {
            (not null, not null) => Expression.AndAlso(posBody, negBody),
            (not null, null) => posBody,
            (null, not null) => negBody,
            _ => null, // all terms were bare `!` → no constraint
        };
        if (body is null)
            return null;

        return Expression.Lambda<Func<T, bool>>(body, selector.Parameters[0]);
    }

    /// <summary>Inclusive numeric range filter on a nullable-double column. NULLs never match.</summary>
    public static IQueryable<T> WhereInRange<T>(
        this IQueryable<T> query, Expression<Func<T, double?>> selector, double? min, double? max)
    {
        var param = selector.Parameters[0];

        if (min.HasValue)
        {
            // liftToNull: false → NULL operand yields false (matches SQL semantics)
            var body = Expression.GreaterThanOrEqual(
                selector.Body, Expression.Constant(min, typeof(double?)), liftToNull: false, method: null);
            query = query.Where(Expression.Lambda<Func<T, bool>>(body, param));
        }

        if (max.HasValue)
        {
            var body = Expression.LessThanOrEqual(
                selector.Body, Expression.Constant(max, typeof(double?)), liftToNull: false, method: null);
            query = query.Where(Expression.Lambda<Func<T, bool>>(body, param));
        }

        return query;
    }

    /// <summary>
    /// Inclusive multi-range numeric filter: matches when the column falls in ANY of the given (min,max)
    /// ranges (each bound optional/inclusive). NULL columns never match any range (<c>liftToNull:false</c>),
    /// preserving <see cref="WhereInRange{T}"/> semantics under OR. Empty list → unchanged query.
    /// </summary>
    public static IQueryable<T> WhereInRanges<T>(
        this IQueryable<T> query, Expression<Func<T, double?>> selector,
        IReadOnlyList<(double? Min, double? Max)> ranges)
    {
        if (ranges is null || ranges.Count == 0)
            return query;

        var param = selector.Parameters[0];
        Expression? any = null;

        foreach (var (min, max) in ranges)
        {
            Expression? one = null;
            if (min.HasValue)
            {
                one = Expression.GreaterThanOrEqual(
                    selector.Body, Expression.Constant(min, typeof(double?)), liftToNull: false, method: null);
            }
            if (max.HasValue)
            {
                var le = Expression.LessThanOrEqual(
                    selector.Body, Expression.Constant(max, typeof(double?)), liftToNull: false, method: null);
                one = one is null ? le : Expression.AndAlso(one, le);
            }
            if (one is null) continue; // fully unbounded → skip (would otherwise match NULLs too)
            any = any is null ? one : Expression.OrElse(any, one);
        }

        if (any is null)
            return query;

        return query.Where(Expression.Lambda<Func<T, bool>>(any, param));
    }

    /// <summary>
    /// Inclusive date-range filter on a nullable-<see cref="DateTime"/> column. NULLs never match.
    /// Mirrors <see cref="WhereInRange{T}"/> but for dates; callers that want "whole end day inclusive"
    /// should pass <paramref name="to"/> already advanced (e.g. <c>endDate.AddDays(1)</c>).
    /// </summary>
    public static IQueryable<T> WhereDateInRange<T>(
        this IQueryable<T> query, Expression<Func<T, DateTime?>> selector, DateTime? from, DateTime? to)
    {
        var param = selector.Parameters[0];

        if (from.HasValue)
        {
            var body = Expression.GreaterThanOrEqual(
                selector.Body, Expression.Constant(from, typeof(DateTime?)), liftToNull: false, method: null);
            query = query.Where(Expression.Lambda<Func<T, bool>>(body, param));
        }

        if (to.HasValue)
        {
            var body = Expression.LessThanOrEqual(
                selector.Body, Expression.Constant(to, typeof(DateTime?)), liftToNull: false, method: null);
            query = query.Where(Expression.Lambda<Func<T, bool>>(body, param));
        }

        return query;
    }

    /// <summary>
    /// Multi-select membership filter (SQL <c>IN</c>). NULL column values never match.
    /// Generalises the per-entity type filter.
    /// </summary>
    public static IQueryable<T> WhereInSet<T>(
        this IQueryable<T> query, Expression<Func<T, string?>> selector, IReadOnlyList<string>? values)
    {
        var predicate = BuildInSetPredicate(selector, values);
        return predicate is null ? query : query.Where(predicate);
    }

    /// <summary>
    /// Builds the exact set-membership predicate (SQL <c>IN</c>; NULL never matches), or <c>null</c> when
    /// the value list is empty. Shared by <see cref="WhereInSet{T}"/> and <see cref="WhereTextLikeOrInSet{T}"/>.
    /// </summary>
    public static Expression<Func<T, bool>>? BuildInSetPredicate<T>(
        Expression<Func<T, string?>> selector, IReadOnlyList<string>? values)
    {
        if (values is null || values.Count == 0)
            return null;

        var list = values.ToList(); // List.Contains for EF translation to IN (...)
        var containsMethod = typeof(List<string>).GetMethod(nameof(List<string>.Contains), new[] { typeof(string) })
            ?? throw new InvalidOperationException("List<string>.Contains vanished");

        var notNull = Expression.NotEqual(selector.Body, Expression.Constant(null, typeof(string)));
        var contains = Expression.Call(Expression.Constant(list), containsMethod, selector.Body);
        var body = Expression.AndAlso(notNull, contains);

        return Expression.Lambda<Func<T, bool>>(body, selector.Parameters[0]);
    }

    /// <summary>
    /// Combo filter: matches a row when its column matches the wildcard <paramref name="pattern"/> (LIKE,
    /// <c>|</c>-OR) OR equals one of <paramref name="values"/> (exact IN). The two halves are OR'd into a
    /// single predicate. An empty half imposes no constraint; if both are empty the query is unchanged.
    /// Mirrors the client's <c>matchesCombo</c>.
    /// </summary>
    public static IQueryable<T> WhereTextLikeOrInSet<T>(
        this IQueryable<T> query, Expression<Func<T, string?>> selector,
        string? pattern, IReadOnlyList<string>? values)
    {
        var likePredicate = BuildTextLikePredicate(selector, pattern);
        var inPredicate = BuildInSetPredicate(selector, values);

        if (likePredicate is null) return inPredicate is null ? query : query.Where(inPredicate);
        if (inPredicate is null) return query.Where(likePredicate);

        // Both present → OR their bodies under the LIKE predicate's parameter.
        var param = likePredicate.Parameters[0];
        var inBody = new ParameterReplacer(inPredicate.Parameters[0], param).Visit(inPredicate.Body);
        var body = Expression.OrElse(likePredicate.Body, inBody);
        return query.Where(Expression.Lambda<Func<T, bool>>(body, param));
    }

    /// <summary>Rewrites a lambda body to use a different parameter instance, so two predicates can share one.</summary>
    private sealed class ParameterReplacer : ExpressionVisitor
    {
        private readonly ParameterExpression _from;
        private readonly ParameterExpression _to;

        public ParameterReplacer(ParameterExpression from, ParameterExpression to)
        {
            _from = from;
            _to = to;
        }

        protected override Expression VisitParameter(ParameterExpression node)
            => node == _from ? _to : base.VisitParameter(node);
    }

    /// <summary>
    /// Translates a user filter string into a SQL LIKE pattern. Escapes literal <c>%</c>, <c>_</c> and
    /// <c>\</c>, maps <c>*</c>→<c>%</c> and <c>?</c>→<c>_</c>, and wraps in <c>%…%</c> when no wildcard
    /// was supplied. Public: also drives the matcher's raw-SQL <c>GridFilterSqlTranslator</c>.
    /// </summary>
    public static string ToLikePattern(string input)
    {
        var hasWildcard = input.Contains('*', StringComparison.Ordinal) || input.Contains('?', StringComparison.Ordinal);

        var sb = new StringBuilder(input.Length + 2);
        foreach (var c in input)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '%':  sb.Append("\\%");  break;
                case '_':  sb.Append("\\_");  break;
                case '*':  sb.Append('%');    break;
                case '?':  sb.Append('_');    break;
                default:   sb.Append(c);      break;
            }
        }

        return hasWildcard ? sb.ToString() : $"%{sb}%";
    }
}
