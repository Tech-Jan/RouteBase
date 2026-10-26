using System.Text.Json;
using RouteBaseWeb.Models;
using Xunit;

namespace RouteBaseWeb.Tests.Api;

/// <summary>
/// Pins the wire shape of every ActivitiesApiController success body (Models/ActivityContracts.cs
/// + ActivityEndpoint + ActivityRowDto): exact camelCase key sets and the load-bearing value
/// conventions. These replaced anonymous objects — the JSON these produce must stay byte-compatible
/// with what the TS consumers (overview-types.ts, activities-gps-loader.ts, core-types.ts, …) declare.
/// Serialized with web defaults (camelCase), matching controller responses.
/// </summary>
public class ActivitiesApiWireShapeTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static JsonElement Serialize(object value) =>
        JsonDocument.Parse(JsonSerializer.Serialize(value, Web)).RootElement.Clone();

    private static string[] Keys(JsonElement e)
    {
        var keys = new List<string>();
        foreach (var p in e.EnumerateObject()) keys.Add(p.Name);
        keys.Sort(StringComparer.Ordinal);
        return keys.ToArray();
    }

    [Fact]
    public void GridResponse_KeySetAndValues()
    {
        var row = ActivityRowDto.From(
            new Activity { TrackId = new TrackId(7), TrackName = "Morning Ride", Visibility = "friends" },
            currentUserId: "me", owner: "Me", photoCount: 2);
        var e = Serialize(new ActivityGridResponse(
            Rows: new[] { row }, FilteredIds: new[] { 7, 9 }, OwnerIds: new[] { 7, 9, 11 },
            Total: 2, UnfilteredTotal: 5, OwnedFilteredCount: 1));

        Assert.Equal(
            new[] { "filteredIds", "ownedFilteredCount", "ownerIds", "rows", "total", "unfilteredTotal" },
            Keys(e));
        Assert.Equal(2, e.GetProperty("total").GetInt32());
        Assert.Equal(5, e.GetProperty("unfilteredTotal").GetInt32());
        // The "Export GPX (N)" count — a subset of filteredIds, not of the grand total.
        Assert.Equal(1, e.GetProperty("ownedFilteredCount").GetInt32());
        Assert.Equal(2, e.GetProperty("filteredIds").GetArrayLength());
        Assert.Equal(3, e.GetProperty("ownerIds").GetArrayLength());
    }

    /// <summary>Rows mirror TS ActivityRow (overview/activities-grid.ts) — the grid column contract.</summary>
    [Fact]
    public void GridRow_KeySet()
    {
        var e = Serialize(ActivityRowDto.From(
            new Activity { TrackId = new TrackId(7), Visibility = "private" }, currentUserId: "me", owner: null));

        Assert.Equal(new[]
        {
            "activityType", "ascentMeters", "cadenceAvg", "canDelete", "descentMeters",
            "distanceMeters", "heartRateAvg", "heartRateMax", "isInEdit", "name", "owner",
            "photoCount", "powerAvg", "speedAvgMs", "speedMaxMs", "startLocal",
            "timeMovingSeconds", "timeTotalSeconds", "trackId", "visibility"
        }, Keys(e));
    }

    [Fact]
    public void OverviewTrack_KeySetAndPointPairs()
    {
        var e = Serialize(new OverviewTrackDto(
            Id: 7, Name: "Morning Ride", Date: "17.07.2026 08:30", Type: "Ride",
            Distance: 12345.6, Ascent: 210,
            Points: new[] { new[] { 48.1, 16.2 } }));

        Assert.Equal(new[] { "ascent", "date", "distance", "id", "name", "points", "type" }, Keys(e));
        // [lat,lng] pairs, not {lat,lng} objects — the parallel-array wire convention.
        var pair = e.GetProperty("points")[0];
        Assert.Equal(JsonValueKind.Array, pair.ValueKind);
        Assert.Equal(48.1, pair[0].GetDouble());
        Assert.Equal(16.2, pair[1].GetDouble());
    }

    [Fact]
    public void GpsPoint_KeySet()
    {
        var e = Serialize(new ActivityGpsPointDto(GpsLat: 48.1, GpsLong: 16.2));

        Assert.Equal(new[] { "gpsLat", "gpsLong" }, Keys(e));
    }

    [Fact]
    public void GpsSummaryPoint_KeySet()
    {
        Assert.Equal(new[] { "lat", "lng" }, Keys(Serialize(new LatLngDto(48.1, 16.2))));
    }

    /// <summary>ActivityEndpoint is served as-is by /bounds — a new property there changes this wire contract.</summary>
    [Fact]
    public void Bounds_KeySet()
    {
        var e = Serialize(new ActivityEndpoint { StartLat = 48.1, StartLng = 16.2, EndLat = null, EndLng = null });

        Assert.Equal(new[] { "endLat", "endLng", "startLat", "startLng" }, Keys(e));
        Assert.Equal(JsonValueKind.Null, e.GetProperty("endLat").ValueKind);
    }

    [Fact]
    public void UpdateActivity_KeySet_UntouchedFieldsNull()
    {
        var e = Serialize(new UpdateActivityResponse(Name: "New name", ActivityType: null, Visibility: null));

        Assert.Equal(new[] { "activityType", "name", "visibility" }, Keys(e));
        Assert.Equal("New name", e.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, e.GetProperty("activityType").ValueKind);
    }

    [Fact]
    public void RefreshElevation_KeySetAndValues()
    {
        var e = Serialize(new RefreshElevationResponse("Elevation refreshed successfully", PointsTotal: 100, PointsReplaced: 0));

        Assert.Equal(new[] { "message", "pointsReplaced", "pointsTotal" }, Keys(e));
        // pointsReplaced: 0 is the EL-08 "DEM yielded nothing" signal activity-actions.ts alerts on.
        Assert.Equal(0, e.GetProperty("pointsReplaced").GetInt32());
    }

    // Bulk Select-mode responses — the `updated`/`deleted` keys the bulk controller reads (activities-bulk-controller.ts).
    [Fact]
    public void BulkUpdate_KeySetAndValue()
    {
        var e = Serialize(new BulkUpdateActivitiesResponse(3));
        Assert.Equal(new[] { "updated" }, Keys(e));
        Assert.Equal(3, e.GetProperty("updated").GetInt32());
    }

    [Fact]
    public void BulkDelete_KeySetAndValue()
    {
        var e = Serialize(new BulkDeleteActivitiesResponse(2));
        Assert.Equal(new[] { "deleted" }, Keys(e));
        Assert.Equal(2, e.GetProperty("deleted").GetInt32());
    }
}
