using System.Text.Json;
using RouteBaseWeb.Models.GridFilter;
using RouteBaseWeb.Models.Requests;
using Xunit;

// Namespace deliberately NOT RouteBaseWeb.Tests.Models — that would shadow RouteBaseWeb.Models for
// every `Models.X` reference in sibling test namespaces (same gotcha as the other files here).
namespace RouteBaseWeb.Tests;

/// <summary>
/// Pins the JSON wire shape of <see cref="GridFilterCollection"/> (via
/// <see cref="GridFilterCollectionJsonConverter"/>): a plain column-key→<see cref="ColumnFilter"/>
/// map, as sent by the activities grid's POST body (TS <c>ActivityGridRequestBody</c>,
/// overview/activities-grid.ts — edit both in the same commit).
/// </summary>
public class GridFilterCollectionJsonConverterTests
{
    // POST bodies are deserialized with ASP.NET Core's web defaults (camelCase, case-insensitive).
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Deserializes_PlainKeyToFilterMap()
    {
        var json = """{"name":{"text":"*gravel*"},"distance":{"range":"3-6, >20"},"type":{"in":["Ride","Run"]}}""";

        var filters = JsonSerializer.Deserialize<GridFilterCollection>(json, Web)!;

        Assert.Equal(3, filters.Items.Count);
        Assert.Equal("*gravel*", filters.Items["name"].Text);
        Assert.Equal("3-6, >20", filters.Items["distance"].Range);
        Assert.Equal(new[] { "Ride", "Run" }, filters.Items["type"].In, StringComparer.Ordinal);
    }

    [Fact]
    public void Keys_AreCaseInsensitive_LikeTheQueryBinder()
    {
        var filters = JsonSerializer.Deserialize<GridFilterCollection>("""{"Name":{"text":"x"}}""", Web)!;

        Assert.True(filters.Items.TryGetValue("name", out var f));
        Assert.Equal("x", f!.Text);
    }

    [Fact]
    public void EmptyMap_YieldsEmptyCollection()
    {
        var filters = JsonSerializer.Deserialize<GridFilterCollection>("{}", Web)!;
        Assert.True(filters.IsEmpty);
    }

    [Fact]
    public void ActivityGridRequest_Body_BindsAllFields()
    {
        // The exact body shape the TS grid sends (buildApiBody in overview/activities-grid.ts).
        var json = """
        {
            "filters": {"name": {"text": "*tour*"}, "owner": {"in": ["__me__"]}},
            "startDate": "2026-01-01",
            "endDate": "2026-07-20",
            "sortColumn": "distance",
            "sortDirection": "asc",
            "page": 2,
            "pageSize": 50,
            "shape": "{\"shapes\":[]}"
        }
        """;

        var req = JsonSerializer.Deserialize<ActivityGridRequest>(json, Web)!;

        Assert.Equal("*tour*", req.Filters.Items["name"].Text);
        Assert.Equal(new[] { "__me__" }, req.Filters.Items["owner"].In, StringComparer.Ordinal);
        Assert.Equal(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified), req.StartDate);
        Assert.Equal(new DateTime(2026, 7, 20, 0, 0, 0, DateTimeKind.Unspecified), req.EndDate);
        Assert.Equal("distance", req.SortColumn);
        Assert.Equal("asc", req.SortDirection);
        Assert.Equal(2, req.Page);
        Assert.Equal(50, req.PageSize);
        Assert.Equal("""{"shapes":[]}""", req.Shape);
    }

    [Fact]
    public void Deserializes_MinMaxFromTo_FromThePostMap()
    {
        // The activities grid POSTs numbers as `range` and dates as startDate/endDate, so the existing
        // tests only read text/in/range from the map. But the JSON-map shape is the REUSABLE grid contract
        // (any table can adopt it — "Reusing the grid on another table") and the query binder already reads
        // f.<key>.min/.max/.from/.to on the URL side. This pins the POST side reads the same four wire fields,
        // so a reusable grid that carries a legacy two-box number range or an in-map date range round-trips.
        var json = """{"avghr":{"min":"5","max":"40"},"day":{"from":"2024-01-01","to":"2024-03-01"}}""";

        var filters = JsonSerializer.Deserialize<GridFilterCollection>(json, Web)!;

        Assert.Equal("5", filters.Items["avghr"].Min);
        Assert.Equal("40", filters.Items["avghr"].Max);
        Assert.Equal("2024-01-01", filters.Items["day"].From);
        Assert.Equal("2024-03-01", filters.Items["day"].To);
    }

    [Fact]
    public void Serializes_BackToThePlainMap()
    {
        var filters = new GridFilterCollection();
        filters.GetOrAdd("name").Text = "*x*";

        var json = JsonSerializer.Serialize(filters, Web);

        Assert.Equal("""{"name":{"text":"*x*","in":[],"min":null,"max":null,"range":null,"from":null,"to":null}}""", json);
    }
}
