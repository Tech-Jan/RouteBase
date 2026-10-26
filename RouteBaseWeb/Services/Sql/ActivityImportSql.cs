namespace RouteBaseWeb.Services.Sql;

/// <summary>
/// Raw SQL for <see cref="ActivityImportService"/> (ID-05 rule: SQL lives behind
/// <c>Services/Sql/</c>, not inside the service). Pure string building only —
/// transactions, execution, and logging stay in the service. Track point data is now written to the
/// packed <c>Track_Blob</c> store (not row-inserted), so the former GPS bulk-insert builder is gone.
/// </summary>
public static class ActivityImportSql
{
    public const string InsertActivitySql = @"
                INSERT INTO Activity (
                    api_ID, track_name, activity_type,
                    start_datetime_loc, start_datetime_utc, datetime_timezone,
                    start_gps_lat, start_gps_long, end_gps_lat, end_gps_long,
                    total_distance, time_total, time_moving, time_elapsed,
                    total_ascent, speed_avg, speed_max,
                    heart_rate_avg, heart_rate_max, cadence_avg, cadence_max,
                    power_avg, power_max,
                    calories, comments, source, user_id
                ) VALUES (
                    {0}, {1}, {2},
                    {3}, {4}, {5},
                    {6}, {7}, {8}, {9},
                    {10}, {11}, {12}, {13},
                    {14}, {15}, {16},
                    {17}, {18}, {19}, {20},
                    {21}, {22},
                    {23}, {24}, {25}, {26}
                );";

    public const string SelectLastRowIdSql = "SELECT last_insert_rowid() as Value";

    public const string UpdateTotalDescentSql =
        "UPDATE Activity SET total_descent = {0} WHERE track_ID = {1};";

    // Value-based expand-or-insert (no GPS scan): the caller computes min/max in C#. Expand-only is sound
    // ONLY for the streaming/import paths where each batch adds points to the same growing truth. It is
    // NOT sound once content is re-derived (finalize outlier rejection, track edits) — those callers use
    // ActivityEditSql.RewriteActivityBboxSql instead. Activity delete cascades the row.
    public const string UpsertActivityBboxSql = @"
            INSERT INTO Activity_Bbox (track_ID, min_lat, max_lat, min_long, max_long)
            VALUES ({0}, {1}, {2}, {3}, {4})
            ON CONFLICT(track_ID) DO UPDATE SET
              min_lat  = MIN(min_lat,  excluded.min_lat),
              max_lat  = MAX(max_lat,  excluded.max_lat),
              min_long = MIN(min_long, excluded.min_long),
              max_long = MAX(max_long, excluded.max_long);";
}
