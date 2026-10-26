using System.Linq.Expressions;
using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Lifecycle;

namespace RouteBaseWeb.Services.Heatmaps;

/// <summary>
/// Which rides a heatmap may paint (SEC-01). ONE tile set is served to every viewer of the segment, so it may
/// only contain rides that EVERY one of those viewers is allowed to see — the rule is keyed on the segment's
/// audience, not on the job owner's. Painting from the owner's point of view (the old self+friends filter)
/// put friends'-only and even private rides into tiles a public segment then showed to everyone.
///
/// Released-only throughout: a trashed or in-work ride is invisible everywhere else, so it is not painted.
/// </summary>
public static class HeatmapPaintScope
{
    /// <summary>Curated, viewer-safe: why a heatmap went offline after its segment's audience grew.</summary>
    public const string WidenedMessage = "The segment's visibility was widened — regenerate the heatmap.";

    /// <param name="segmentVisibility">The segment's visibility — who will be served the tiles.</param>
    /// <param name="ownerId">The segment owner (the only viewer of a private segment).</param>
    /// <param name="ownerFriendIds">The owner's accepted friends; only consulted for a private segment.</param>
    public static Expression<Func<Activity, bool>> For(
        string segmentVisibility, string ownerId, IReadOnlyCollection<string> ownerFriendIds)
    {
        switch (Visibilities.Normalize(segmentVisibility))
        {
            case Visibilities.Private:
                // Audience = the owner alone → exactly what the owner may see.
                return a => a.Lifecycle == LifecycleStates.Released
                    && (a.UserId == ownerId
                        || a.Visibility == Visibilities.Public
                        || (a.Visibility == Visibilities.Friends
                            && a.UserId != null && ownerFriendIds.Contains(a.UserId)));
            case Visibilities.Friends:
                // Audience = owner + owner's friends. A third user's friends-only ride is out: a viewer who is
                // the owner's friend need not be that rider's friend.
                return a => a.Lifecycle == LifecycleStates.Released
                    && (a.Visibility == Visibilities.Public
                        || (a.UserId == ownerId && a.Visibility == Visibilities.Friends));
            default:
                // Public (or an unrecognised value → the strictest reading): every user sees the tiles.
                return a => a.Lifecycle == LifecycleStates.Released && a.Visibility == Visibilities.Public;
        }
    }

    /// <summary>True when the audience grows (private → friends → public): tiles painted for the old, smaller
    /// audience may hold rides the new viewers must not see.</summary>
    public static bool Widens(string? from, string? to) => Breadth(to) > Breadth(from);

    // Unrecognised counts as widest, matching For's strictest-reading default.
    private static int Breadth(string? visibility) => Visibilities.Normalize(visibility) switch
    {
        Visibilities.Private => 0,
        Visibilities.Friends => 1,
        _ => 2,
    };
}
