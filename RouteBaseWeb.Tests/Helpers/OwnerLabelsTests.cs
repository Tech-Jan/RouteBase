using RouteBaseWeb.Helpers.Grid;
using RouteBaseWeb.Models;
using Xunit;

namespace RouteBaseWeb.Tests.Helpers;

public class OwnerLabelsTests
{
    [Fact]
    public void DisplayName_PrefersDisplayName()
    {
        var u = new AppUser { UserId = "id-1", DisplayName = "Alice", Email = "alice@example.com" };
        Assert.Equal("Alice", OwnerLabels.DisplayName(u));
    }

    [Fact]
    public void DisplayName_NoDisplayName_FallsBackToUserId_NeverEmail()
    {
        // PI-10: the label is serialized into page bootstrap / API responses seen by friends —
        // a display-name-less user's email must never leak there.
        var u = new AppUser { UserId = "id-2", DisplayName = null, Email = "secret@example.com" };
        Assert.Equal("id-2", OwnerLabels.DisplayName(u));
    }

    [Fact]
    public void ToNameMap_NeverContainsEmail()
    {
        var users = new[]
        {
            new AppUser { UserId = "a", DisplayName = "Named", Email = "a@example.com" },
            new AppUser { UserId = "b", DisplayName = null, Email = "b@example.com" },
        };
        var map = OwnerLabels.ToNameMap(users);
        Assert.Equal("Named", map["a"]);
        Assert.Equal("b", map["b"]);
        Assert.DoesNotContain(map.Values, v => v.Contains('@', StringComparison.Ordinal));
    }
}
