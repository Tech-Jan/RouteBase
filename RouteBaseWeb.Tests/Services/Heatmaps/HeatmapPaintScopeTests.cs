using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Heatmaps;
using RouteBaseWeb.Services.Lifecycle;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Heatmaps;

/// <summary>
/// SEC-01: one tile set is served to every viewer of a segment, so a ride is painted only when EVERY viewer may
/// see it. Table over (segment audience × ride owner × ride visibility × lifecycle), on the compiled expression.
/// </summary>
public class HeatmapPaintScopeTests
{
    private const string Owner = "owner";
    private const string Friend = "friend";
    private const string Stranger = "stranger";
    private static readonly string[] OwnerFriends = [Friend];

    [Theory]
    // Private segment: audience = owner → whatever the owner may see.
    [InlineData(Visibilities.Private, Owner,    Visibilities.Private, true)]
    [InlineData(Visibilities.Private, Owner,    Visibilities.Friends, true)]
    [InlineData(Visibilities.Private, Friend,   Visibilities.Friends, true)]
    [InlineData(Visibilities.Private, Friend,   Visibilities.Private, false)]
    [InlineData(Visibilities.Private, Stranger, Visibilities.Friends, false)]
    [InlineData(Visibilities.Private, Stranger, Visibilities.Public,  true)]
    // Friends segment: audience = owner + owner's friends → owner's non-private rides + public rides.
    [InlineData(Visibilities.Friends, Owner,    Visibilities.Private, false)]
    [InlineData(Visibilities.Friends, Owner,    Visibilities.Friends, true)]
    [InlineData(Visibilities.Friends, Friend,   Visibilities.Friends, false)]   // the original SEC-01 leak
    [InlineData(Visibilities.Friends, Friend,   Visibilities.Private, false)]
    [InlineData(Visibilities.Friends, Friend,   Visibilities.Public,  true)]
    // Public segment: audience = everyone → public rides only, the owner's included.
    [InlineData(Visibilities.Public,  Owner,    Visibilities.Friends, false)]
    [InlineData(Visibilities.Public,  Owner,    Visibilities.Public,  true)]
    [InlineData(Visibilities.Public,  Friend,   Visibilities.Friends, false)]
    [InlineData(Visibilities.Public,  Stranger, Visibilities.Public,  true)]
    // Unrecognised segment visibility → strictest reading.
    [InlineData("bogus",              Owner,    Visibilities.Friends, false)]
    public void Released_ride_painted_only_when_every_viewer_may_see_it(
        string segmentVisibility, string rideOwner, string rideVisibility, bool painted)
    {
        var ride = new Activity { UserId = rideOwner, Visibility = rideVisibility, Lifecycle = LifecycleStates.Released };

        Assert.Equal(painted, HeatmapPaintScope.For(segmentVisibility, Owner, OwnerFriends).Compile()(ride));
    }

    [Theory]
    [InlineData(Visibilities.Private)]
    [InlineData(Visibilities.Friends)]
    [InlineData(Visibilities.Public)]
    public void Unreleased_ride_is_never_painted(string segmentVisibility)
    {
        var trashed = new Activity { UserId = Owner, Visibility = Visibilities.Public, Lifecycle = LifecycleStates.Trashed };

        Assert.False(HeatmapPaintScope.For(segmentVisibility, Owner, OwnerFriends).Compile()(trashed));
    }

    [Theory]
    [InlineData(Visibilities.Private, Visibilities.Friends, true)]
    [InlineData(Visibilities.Friends, Visibilities.Public,  true)]
    [InlineData(Visibilities.Private, Visibilities.Public,  true)]
    [InlineData(Visibilities.Public,  Visibilities.Friends, false)]
    [InlineData(Visibilities.Friends, Visibilities.Friends, false)]
    public void Widens_only_when_the_audience_grows(string from, string to, bool widens) =>
        Assert.Equal(widens, HeatmapPaintScope.Widens(from, to));
}
