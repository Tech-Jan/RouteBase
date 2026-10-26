using System.Text.Json;
using RouteBaseWeb.Models;
using Xunit;

namespace RouteBaseWeb.Tests.Api;

/// <summary>
/// Pins the wire shape of ReviewApiController's success bodies (Models/ReviewContracts.cs).
/// The TS caller (live-recording/record-review.ts postReview) only checks resp.ok, so these are unread —
/// named per the DTO convention regardless. Restore reuses TrashContracts' RestoreResponse
/// (identical {restored} shape); GetPendingGps reuses ActivityContracts' LatLngDto.
/// </summary>
public class ReviewApiWireShapeTests
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
    public void Confirm_KeySetAndValue()
    {
        var e = Serialize(new ConfirmResponse(4));
        Assert.Equal(new[] { "confirmed" }, Keys(e));
        Assert.Equal(4, e.GetProperty("confirmed").GetInt32());
    }

    [Fact]
    public void TrashCount_KeySetAndValue()
    {
        var e = Serialize(new TrashCountResponse(2));
        Assert.Equal(new[] { "trashed" }, Keys(e));
        Assert.Equal(2, e.GetProperty("trashed").GetInt32());
    }
}
