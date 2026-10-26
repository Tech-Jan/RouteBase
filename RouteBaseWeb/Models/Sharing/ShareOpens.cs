namespace RouteBaseWeb.Models.Sharing;

/// <summary>
/// One resource's share history, as the card surfaces need it: how often it was ever opened, and whether a
/// link is live right now.
///
/// The two are deliberately separate answers. <see cref="Opens"/> is LIFETIME — summed across every link
/// the resource ever had, including revoked and expired ones — so revoking a link does not reset a ride's
/// view count. <see cref="Active"/> is the "is it published?" flag the grid filter and the badge's bright
/// state read.
/// </summary>
/// <param name="Opens">Lifetime page opens across all of the resource's links. Never negative; 0 means
/// "published, never opened".</param>
/// <param name="Active">True while at least one link is neither revoked nor expired.</param>
public readonly record struct ShareOpens(int Opens, bool Active);

/// <summary>
/// The two grid flags derived from a <see cref="ShareOpens"/> map, in one place because all three card
/// lists (dashboard, segments, races) derive them and a drift between them would show up as a filter that
/// matches the wrong rows. A missing key means never shared — both flags false.
/// </summary>
public static class ShareOpensLookup
{
    /// <summary>A link is live right now.</summary>
    public static bool IsSharedNow(this IReadOnlyDictionary<int, ShareOpens> map, int resourceId)
        => map.TryGetValue(resourceId, out var s) && s.Active;

    /// <summary>It was shared at some point, and no link is live now. Independent of the view count — a
    /// past share nobody ever opened still counts as "previously shared" here, while the badge stays
    /// hidden (that one answers "how many views?").</summary>
    public static bool WasSharedBefore(this IReadOnlyDictionary<int, ShareOpens> map, int resourceId)
        => map.TryGetValue(resourceId, out var s) && !s.Active;
}
