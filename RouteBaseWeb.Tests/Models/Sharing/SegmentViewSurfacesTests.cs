using System.Reflection;
using RouteBaseWeb.Models.Sharing;
using Xunit;

// Namespace is deliberately flat, matching the other files under Tests/Models: declaring
// RouteBaseWeb.Tests.Models(.*) makes `Models.Foo` inside any RouteBaseWeb.Tests.* file resolve to the TEST
// namespace instead of RouteBaseWeb.Models, which breaks unrelated files (RaceWireContractTests hit this).
namespace RouteBaseWeb.Tests;

/// <summary>
/// Pins the surfaces record's two invariants. The reflection test is the important one: it means a
/// contributor who adds a surface cannot have it default to true on a share view, because the assertion
/// enumerates the record rather than a hand-written list that would silently fall behind.
/// </summary>
public class SegmentViewSurfacesTests
{
    private static IReadOnlyList<PropertyInfo> BoolProperties() =>
        typeof(SegmentViewSurfaces)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(bool))
            .ToList();

    [Fact]
    public void ReadOnlyShare_HasEverySurfaceOff()
    {
        var props = BoolProperties();

        // Anti-vacuity: if the record were ever refactored to non-bool members this test would pass while
        // asserting nothing.
        Assert.NotEmpty(props);

        var on = props.Where(p => (bool)p.GetValue(SegmentViewSurfaces.ReadOnlyShare)!)
                      .Select(p => p.Name)
                      .ToList();

        Assert.True(on.Count == 0,
            $"ReadOnlyShare must offer nothing, but these are on: {string.Join(", ", on)}");
    }

    [Fact]
    public void StatisticsPage_GivesOwnerActionsOnlyToTheOwner()
    {
        var owner = SegmentViewSurfaces.ForStatisticsPage(isOwner: true);
        var other = SegmentViewSurfaces.ForStatisticsPage(isOwner: false);

        Assert.True(owner.CanManageSegment);
        Assert.True(owner.CanGenerateHeatmap);
        Assert.True(owner.CanExcludeEfforts);
        Assert.True(owner.CanShare);

        Assert.False(other.CanManageSegment);
        Assert.False(other.CanGenerateHeatmap);
        Assert.False(other.CanExcludeEfforts);
        Assert.False(other.CanShare);
    }

    [Fact]
    public void StatisticsPage_KeepsViewerSurfacesOnForNonOwnersToo()
    {
        // These are NOT owner questions — they are true for every signed-in viewer and false only on a
        // share render. Deriving them from isOwner would silently change the friend-viewing-a-segment page.
        var other = SegmentViewSurfaces.ForStatisticsPage(isOwner: false);

        Assert.True(other.ShowHeatmap);
        Assert.True(other.ShowRiderIdentity);
        Assert.True(other.CanLinkToActivities);
        Assert.True(other.ShowAppNav);
    }
}
