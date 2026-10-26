namespace RouteBaseWeb.Models;

/// <summary>
/// Resolved filter driving every Statistics-page query. Built by the API controller from the shared
/// grid-filter wire format (owner combo, raw-type multiselect, date range) — the service stays free of
/// identity/name resolution.
/// </summary>
/// <param name="Types">Raw activity types to include (exact match). Null/empty = all types.</param>
/// <param name="From">Inclusive lower bound on <c>StartDateTimeLocal</c> (local date). Null = unbounded.</param>
/// <param name="To">Inclusive upper bound on <c>StartDateTimeLocal</c> (whole local day). Null = unbounded.</param>
/// <param name="OwnerUserIds">User ids to include. Null = no owner constraint (all visible).</param>
/// <param name="OwnerMatchNone">True when the owner filter resolved to nobody → return zeroed/empty results.</param>
/// <param name="Visibilities">Visibility values to include (private/friends/public, exact match). Null/empty = all.</param>
public record StatisticsFilter(
    IReadOnlyList<string>? Types,
    DateTime? From,
    DateTime? To,
    IReadOnlyList<string>? OwnerUserIds,
    bool OwnerMatchNone,
    IReadOnlyList<string>? Visibilities = null);
