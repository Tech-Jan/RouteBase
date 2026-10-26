using System.Text.Json;
using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Tracks;
using Xunit;

namespace RouteBaseWeb.Tests.Api;

/// <summary>
/// Pins the wire shape of ActivityLifecycleApiController's success bodies
/// (Models/ActivityLifecycleContracts.cs). Park/Release/Reset bodies are unread by the TS caller
/// (activity/version-history.ts — checks resp.ok and reloads), named per the DTO convention
/// regardless. RestoreVersion has its own {versionId} body — it used to stuff a version id into
/// TrashContracts' RestoreResponse, whose `restored` field means "count restored" elsewhere.
/// </summary>
public class ActivityLifecycleApiWireShapeTests
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
    public void LifecycleTransition_KeySetAndValue()
    {
        var e = Serialize(new LifecycleTransitionResponse("in-work"));
        Assert.Equal(new[] { "lifecycle" }, Keys(e));
        Assert.Equal("in-work", e.GetProperty("lifecycle").GetString());
    }

    [Fact]
    public void VersionsList_KeySet()
    {
        var view = new TrackVersionView(
            VersionId: 1, Seq: 0, IsHead: true, CreatedAt: DateTime.UtcNow,
            PointCount: 100, DistanceMeters: 500, AscentMeters: 10, DurationSeconds: 60,
            Provenance: "original", ParentNames: [], SameAsParent: false, CommitId: null);
        var e = Serialize(new VersionsListResponse([view]));

        Assert.Equal(new[] { "versions" }, Keys(e));
        Assert.Equal(1, e.GetProperty("versions").GetArrayLength());

        // Pin every field of a version row — CommitId carries the un-merge/un-split door (version-history.ts);
        // a key-set check on the wrapper alone never exercised the row's 12-field shape.
        var row = e.GetProperty("versions")[0];
        Assert.Equal(new[]
        {
            "ascentMeters", "commitId", "createdAt", "distanceMeters", "durationSeconds", "isHead",
            "parentNames", "pointCount", "provenance", "sameAsParent", "seq", "versionId"
        }, Keys(row));
        Assert.Equal(JsonValueKind.Null, row.GetProperty("commitId").ValueKind);
    }

    [Fact]
    public void Reset_KeySetAndValue()
    {
        var e = Serialize(new ResetResponse(true));
        Assert.Equal(new[] { "reset" }, Keys(e));
        Assert.True(e.GetProperty("reset").GetBoolean());
    }

    [Fact]
    public void RestoreVersion_KeySetAndValue()
    {
        var e = Serialize(new RestoreVersionResponse(7038));
        Assert.Equal(new[] { "versionId" }, Keys(e));
        Assert.Equal(7038, e.GetProperty("versionId").GetInt32());
    }
}
