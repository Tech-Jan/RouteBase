using System.Text.Json;
using RouteBaseWeb.Models;
using Xunit;

namespace RouteBaseWeb.Tests.Api;

/// <summary>
/// Pins the wire shape of FriendsApiController's success bodies (Models/FriendsContracts.cs) —
/// the TS consumers (pages/dashboard-nav.ts, pages/friends.ts) declare matching local interfaces.
/// </summary>
public class FriendsApiWireShapeTests
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
    public void PendingCount_KeySetAndValue()
    {
        var e = Serialize(new PendingCountResponse(3));
        Assert.Equal(new[] { "count" }, Keys(e));
        Assert.Equal(3, e.GetProperty("count").GetInt32());
    }

    [Fact]
    public void FriendAction_KeySetAndValue()
    {
        var e = Serialize(new FriendActionResponse("Friend request accepted."));
        Assert.Equal(new[] { "message" }, Keys(e));
        Assert.Equal("Friend request accepted.", e.GetProperty("message").GetString());
    }
}
