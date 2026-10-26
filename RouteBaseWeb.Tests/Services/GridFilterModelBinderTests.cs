using RouteBaseWeb.Models.GridFilter;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Metadata;
using Microsoft.AspNetCore.Routing;
using Xunit;

namespace RouteBaseWeb.Tests.Services;

/// <summary>
/// Tests for <see cref="GridFilterModelBinder"/> — the generic <c>f.&lt;key&gt;.&lt;suffix&gt;</c> wire format
/// plus the legacy page/API aliases.
/// </summary>
public class GridFilterModelBinderTests
{
    private static GridFilterCollection Bind(string queryString)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.QueryString = new QueryString(queryString);

        var bindingContext = new DefaultModelBindingContext
        {
            ModelMetadata = new EmptyModelMetadataProvider().GetMetadataForType(typeof(GridFilterCollection)),
            ModelName = "Filters",
            ModelState = new ModelStateDictionary(),
            ActionContext = new ActionContext { HttpContext = httpContext, RouteData = new RouteData() },
        };

        new GridFilterModelBinder().BindModelAsync(bindingContext).GetAwaiter().GetResult();

        Assert.True(bindingContext.Result.IsModelSet);
        return Assert.IsType<GridFilterCollection>(bindingContext.Result.Model);
    }

    [Fact]
    public void Generic_TextAndNumberRange()
    {
        var c = Bind("?f.name.text=run&f.distance.min=5&f.distance.max=40");
        Assert.Equal("run", c.Items["name"].Text);
        Assert.Equal("5", c.Items["distance"].Min);
        Assert.Equal("40", c.Items["distance"].Max);
    }

    [Fact]
    public void Generic_NumberRangeExpression()
    {
        var c = Bind("?f.distance.range=3-6,8-12");
        Assert.Equal("3-6,8-12", c.Items["distance"].Range);
    }

    [Fact]
    public void Generic_MultiSelect_RepeatedIn()
    {
        var c = Bind("?f.type.in=Run&f.type.in=Ride");
        Assert.Equal(new[] { "Run", "Ride" }, c.Items["type"].In, StringComparer.Ordinal);
    }

    [Fact]
    public void Generic_DateRange()
    {
        var c = Bind("?f.date.from=2024-01-01&f.date.to=2024-03-01");
        Assert.Equal("2024-01-01", c.Items["date"].From);
        Assert.Equal("2024-03-01", c.Items["date"].To);
    }

    [Fact]
    public void Legacy_PageAliases()
    {
        var c = Bind("?fName=run&fDistMin=5&fAscMax=600");
        Assert.Equal("run", c.Items["name"].Text);
        Assert.Equal("5", c.Items["distance"].Min);
        Assert.Equal("600", c.Items["ascent"].Max);
    }

    [Fact]
    public void OldApiParamNames_AreNotBound()
    {
        // The transitional pre-generic API spelling (Name/DistanceMinKm/…) was dropped once the client moved
        // to f.* — these must no longer bind (only the f.* format and the page-URL aliases do).
        var c = Bind("?Name=run&DistanceMinKm=10&AscentMin=100");
        Assert.True(c.IsEmpty);
    }

    [Fact]
    public void UnrelatedParams_ProduceEmptyCollection()
    {
        var c = Bind("?SortColumn=date&Page=2&foo=bar");
        Assert.True(c.IsEmpty);
    }

    [Fact]
    public void MalformedGenericKey_IsIgnored()
    {
        // "f.distance" has no suffix → not "f.<key>.<suffix>".
        var c = Bind("?f.distance=5");
        Assert.True(c.IsEmpty);
    }
}
