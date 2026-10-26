using System.Text.Json;
using RouteBaseWeb.Models;
using Xunit;

namespace RouteBaseWeb.Tests.Api;

/// <summary>
/// Pins the wire shape of GET /api/statistics/summary. StatisticsApiController.GetSummary serves
/// StatisticsSummary (Models/StatisticsSummary.cs) directly — no wrapper needed, since its
/// Overall/ByType properties already camelCase to the exact keys the old anonymous body used.
/// The TS consumer (statistics/statistics-data.ts StatsSummary) mirrors this shape.
/// </summary>
public class StatisticsApiWireShapeTests
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
    public void Summary_KeySet()
    {
        var e = Serialize(new StatisticsSummary
        {
            Overall = new ActivityStatistics(),
            ByType = [new TypeStatistics { Type = "Ride", Stats = new ActivityStatistics() }],
        });

        Assert.Equal(new[] { "byType", "overall" }, Keys(e));
        Assert.Equal(1, e.GetProperty("byType").GetArrayLength());
        Assert.Equal("Ride", e.GetProperty("byType")[0].GetProperty("type").GetString());
    }
}
