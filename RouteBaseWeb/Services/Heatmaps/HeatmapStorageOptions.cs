namespace RouteBaseWeb.Services.Heatmaps;

/// <summary>
/// Where packed heatmap tile files live. <see cref="Root"/> defaults to the DB's data dir
/// (<c>&lt;exeDir&gt;/database</c>) so tiles back up as a unit with the DB; the store appends the
/// <c>heatmaps/</c> namespace + id fan-out beneath it. Mirrors <c>PhotoStorageOptions</c> — kept as
/// config, not a constant, so a second volume / backend is DI config later and tests point it at a temp dir.
/// </summary>
public sealed class HeatmapStorageOptions
{
    public required string Root { get; init; }
}
