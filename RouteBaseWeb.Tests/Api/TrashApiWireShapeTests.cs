using System.Text.Json;
using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Lifecycle;
using Xunit;

namespace RouteBaseWeb.Tests.Api;

/// <summary>
/// Pins the wire shape of TrashApiController's success/conflict bodies (Models/TrashContracts.cs) —
/// the TS consumer (trash/trash.ts) mirrors MergeSourcesConfirmBody; the gps-summary list reuses
/// LatLngDto (Models/ActivityContracts.cs), already pinned by ActivitiesApiWireShapeTests.
/// </summary>
public class TrashApiWireShapeTests
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
    public void List_KeySet_AndTrashedActivityFields()
    {
        var item = new TrashedActivity(
            TrackId: 7, Name: "Morning Ride", ActivityType: "Ride", StartedIso: "2026-07-11T08:00:00",
            DistanceMeters: 12345.6, AscentMeters: 210,
            Reason: "merge", TrashedAt: DateTime.UtcNow, RestoresTo: "in-work",
            MergedIntoId: 42, MergedIntoName: "Combined Ride");
        var e = Serialize(new TrashListResponse(new[] { item }));

        Assert.Equal(new[] { "items" }, Keys(e));

        // Pin every field of a populated row — MergedIntoId/MergedIntoName are the destructive-purge signal
        // trash/trash.ts reads; an empty response would never have exercised them.
        var row = e.GetProperty("items")[0];
        Assert.Equal(new[]
        {
            "activityType", "ascentMeters", "distanceMeters", "mergedIntoId", "mergedIntoName",
            "name", "reason", "restoresTo", "startedIso", "trackId", "trashedAt"
        }, Keys(row));
        Assert.Equal(42, row.GetProperty("mergedIntoId").GetInt32());
        Assert.Equal("Combined Ride", row.GetProperty("mergedIntoName").GetString());
    }

    [Fact]
    public void Restore_KeySetAndValue()
    {
        var e = Serialize(new RestoreResponse(3));
        Assert.Equal(new[] { "restored" }, Keys(e));
        Assert.Equal(3, e.GetProperty("restored").GetInt32());
    }

    [Fact]
    public void Purge_KeySetAndValue()
    {
        var e = Serialize(new PurgeResponse(2));
        Assert.Equal(new[] { "purged" }, Keys(e));
        Assert.Equal(2, e.GetProperty("purged").GetInt32());
    }

    [Fact]
    public void MergeSourcesConfirm_KeySetAndValues()
    {
        var e = Serialize(new MergeSourcesConfirmBody("some message", new[] { 7, 9 }));
        Assert.Equal(new[] { "code", "mergeSourceIds", "message" }, Keys(e)); // D32: { code, message, mergeSourceIds }
        Assert.Equal("mergeSourcesNeedConfirm", e.GetProperty("code").GetString()); // the token moved from `error` → `code`
        Assert.Equal("some message", e.GetProperty("message").GetString());
        Assert.Equal(2, e.GetProperty("mergeSourceIds").GetArrayLength());
    }
}
