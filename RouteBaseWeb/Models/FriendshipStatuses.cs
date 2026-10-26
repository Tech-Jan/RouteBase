namespace RouteBaseWeb.Models;

/// <summary>
/// The Friendship.Status vocabulary (D2). pending → accepted | declined; a decline allows
/// re-requesting (row reset to pending). "blocked" is modeled and read defensively but no code
/// path writes it today. Entity-only — never on the response wire, so deliberately NOT stamped;
/// the grid's FriendshipStatus ("none" for strangers) is a different, wire-side concept.
/// </summary>
public static class FriendshipStatuses
{
    public const string Pending  = "pending";
    public const string Accepted = "accepted";
    public const string Declined = "declined";
    public const string Blocked  = "blocked";
}
