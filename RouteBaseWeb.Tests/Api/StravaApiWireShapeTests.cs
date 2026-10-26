using System.Text.Json;
using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using Xunit;

namespace RouteBaseWeb.Tests.Api;

/// <summary>
/// Pins the wire shape of StravaApiController's success/conflict bodies (Models/StravaContracts.cs).
/// import-all's Accepted/Conflict bodies are unread by the TS client (pages/keys-and-data.ts checks
/// response.status only), named per the DTO convention regardless.
/// </summary>
public class StravaApiWireShapeTests
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
    public void ImportConflict_KeySet()
    {
        var e = Serialize(new StravaImportConflictBody("Import is already running", new ActivityImportProgress(TimeProvider.System).GetStatus("u")));
        Assert.Equal(new[] { "code", "message", "status" }, Keys(e)); // D32: { code, message, status }
        Assert.Equal("conflict", e.GetProperty("code").GetString());
    }

    [Fact]
    public void ImportQueued_KeySetAndValue()
    {
        var e = Serialize(new StravaImportQueuedResponse("Import queued in background"));
        Assert.Equal(new[] { "message" }, Keys(e));
        Assert.Equal("Import queued in background", e.GetProperty("message").GetString());
    }

    [Fact]
    public void Status_KeySetAndValues()
    {
        var e = Serialize(new StravaStatusResponse(true, 12345L));
        Assert.Equal(new[] { "athleteId", "isAuthenticated" }, Keys(e));
        Assert.True(e.GetProperty("isAuthenticated").GetBoolean());
        Assert.Equal(12345L, e.GetProperty("athleteId").GetInt64());
    }

    [Fact]
    public void AuthUrl_KeySetAndValue()
    {
        var e = Serialize(new StravaAuthUrlResponse("https://www.strava.com/oauth/authorize?..."));
        Assert.Equal(new[] { "authUrl" }, Keys(e));
    }
}
