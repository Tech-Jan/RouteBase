using RouteBaseWeb.Models;

namespace RouteBaseWeb.Helpers.Grid;

/// <summary>
/// Shared owner-label helpers for the grid page models (Dashboard, Activities, Segments). The
/// "Me"/display-name/null resolution, the display-name map build, and the option-list Distinct/OrderBy
/// blocks used to be copied verbatim across all three page models (GL-07, rule of three).
/// </summary>
public static class OwnerLabels
{
    /// <summary>Best available display name for a user: DisplayName → UserId. Email is deliberately
    /// NOT a fallback — these labels serialize into pages/APIs seen by friends (PI-10).</summary>
    public static string DisplayName(AppUser u) => u.DisplayName ?? u.UserId;

    /// <summary>UserId → display-name lookup for a set of users.</summary>
    public static Dictionary<string, string> ToNameMap(IEnumerable<AppUser> users) =>
        users.ToDictionary(u => u.UserId, DisplayName, StringComparer.Ordinal);

    /// <summary>
    /// Owner label for a row: "Me" for the viewer, the mapped display name for a known other user,
    /// or null when the owner is unknown / legacy null-UserId.
    /// </summary>
    public static string? Label(string? ownerUserId, string currentUserId, IReadOnlyDictionary<string, string> names)
    {
        if (string.IsNullOrEmpty(ownerUserId)) return null;
        if (ownerUserId == currentUserId) return "Me";
        return names.TryGetValue(ownerUserId, out var name) ? name : null;
    }

    /// <summary>Non-empty values, distinct, alphabetically sorted (grid type-option lists).</summary>
    public static List<string> DistinctSorted(IEnumerable<string?> values) =>
        values.OfType<string>().Where(v => v.Length > 0).Distinct(StringComparer.Ordinal).OrderBy(v => v, StringComparer.CurrentCulture).ToList();

    /// <summary>Like <see cref="DistinctSorted"/> but keeps "Me" first (owner-option lists).</summary>
    public static List<string> DistinctSortedMeFirst(IEnumerable<string?> values) =>
        values.OfType<string>().Where(v => v.Length > 0).Distinct(StringComparer.Ordinal).OrderBy(v => v == "Me" ? "" : v, StringComparer.CurrentCulture).ToList();
}
