namespace RouteBaseWeb.Models;

// Response contracts for FriendsApiController. Serialized camelCase (web defaults);
// wire shapes pinned by FriendsApiWireShapeTests. Each mirrors a TS type — edit both
// in the same commit.

/// <summary>Response of GET /api/friends/pending-count. Mirrors TS <c>PendingCountResponse</c> (dashboard/dashboard-nav.ts).</summary>
public sealed record PendingCountResponse(int Count);

/// <summary>
/// Response of the friend-request lifecycle actions (POST request/accept/decline, DELETE {id}).
/// The TS callers (pages/friends.ts) currently ignore the body — they resolve on 2xx and show
/// their own hardcoded toast text — kept as a named contract per the DTO convention regardless.
/// </summary>
public sealed record FriendActionResponse(string Message);
