using System.Text.Json;
using RouteBaseWeb.Models;
using Xunit;

namespace RouteBaseWeb.Tests.Api;

/// <summary>
/// Pins the wire shape of ApiKeyController's success bodies (Models/ApiKeyContracts.cs) — the
/// TS consumer (pages/keys-and-data.ts) declares matching local interfaces.
/// </summary>
public class ApiKeyControllerWireShapeTests
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
    public void Status_KeySet()
    {
        var e = Serialize(new ApiKeyStatusResponse(HasKey: true, CreatedAt: DateTime.UtcNow));
        Assert.Equal(new[] { "createdAt", "hasKey" }, Keys(e));
    }

    [Fact]
    public void Generate_KeySetAndValues()
    {
        var now = DateTime.UtcNow;
        var e = Serialize(new ApiKeyGenerateResponse(Key: "plaintext-key-value", CreatedAt: now));
        Assert.Equal(new[] { "createdAt", "key" }, Keys(e));
        Assert.Equal("plaintext-key-value", e.GetProperty("key").GetString());
    }
}
