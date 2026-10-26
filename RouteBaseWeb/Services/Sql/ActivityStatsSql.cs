namespace RouteBaseWeb.Services.Sql;

/// <summary>
/// Flow-scoped Activity stat writes (ConceptRefactors S4): each flow executes exactly the column
/// set it owns — never "update everything" (the refresh-stomp class). The ownership matrix IS the set of
/// methods below: one per flow, each naming the exact columns it writes.
/// </summary>
public static class ActivityStatsSql
{
    // Live finalize: every motion + elevation scalar is server-derived → full set.
    // time_elapsed = {3} reuse is deliberate (RF-04 DECIDED: timer stays wall-span).
    public const string UpdateAllStatsSql = @"
        UPDATE Activity SET
            total_distance = {0},
            total_ascent   = {1},
            total_descent  = {2},
            time_total     = {3},
            time_elapsed   = {3},
            time_moving    = {4},
            speed_avg      = {5},
            speed_max      = {6}
        WHERE track_ID = {7};";

    // Elevation refresh: elevation-derived scalars only — device-authored motion scalars
    // (distance/times/speeds) must survive a refresh on imported tracks.
    public const string UpdateElevationStatsSql = @"
        UPDATE Activity SET
            total_ascent  = {0},
            total_descent = {1}
        WHERE track_ID = {2};";
}
