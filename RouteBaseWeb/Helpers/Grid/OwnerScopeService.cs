using RouteBaseWeb.Models.GridFilter;
using RouteBaseWeb.Services.Interfaces;

namespace RouteBaseWeb.Helpers.Grid;

/// <summary>
/// Resolves an owner combo filter (the same one the activities grid sends) into the concrete, authorised
/// set of owner user-ids a shape segment should match against, plus the auth context the matcher needs to
/// re-check visibility on every run. Centralises the visible-user-dictionary build + <see cref="OwnerFilterResolver"/>
/// call so the segments API and the matcher agree on the meaning of an owner filter.
/// </summary>
public interface IOwnerScopeService
{
    /// <summary>
    /// Resolve the given owner filter for the current user.
    /// <list type="bullet">
    ///   <item>Empty / no filter → <see cref="OwnerScope.OwnerUserIds"/> is <c>null</c> (legacy own-only).</item>
    ///   <item><c>Everyone</c> → a snapshot of all currently visible user ids.</item>
    ///   <item>A name search that matches nobody → an empty list (match nobody).</item>
    ///   <item>Otherwise → the resolved, authorised owner ids.</item>
    /// </list>
    /// </summary>
    Task<OwnerScope> ResolveAsync(ColumnFilter? owner);
}

/// <summary>
/// The outcome of resolving an owner filter for shape-segment matching.
/// </summary>
/// <param name="OwnerUserIds">
/// The owners to match against. <c>null</c> means "own tracks only" (legacy default); a non-null list is
/// used verbatim (an empty list matches nobody).
/// </param>
/// <param name="CurrentUserId">The current user — always allowed to match their own tracks.</param>
/// <param name="FriendIds">The current accepted-friend ids, for the friends-visibility re-authorisation.</param>
public sealed record OwnerScope(
    IReadOnlyList<string>? OwnerUserIds,
    string CurrentUserId,
    IReadOnlyList<string> FriendIds);

public sealed class OwnerScopeService : IOwnerScopeService
{
    private readonly ICurrentUserService _currentUser;
    private readonly IVisibilityService _visibility;
    private readonly IUserService _userService;

    public OwnerScopeService(
        ICurrentUserService currentUser, IVisibilityService visibility, IUserService userService)
    {
        _currentUser = currentUser;
        _visibility  = visibility;
        _userService = userService;
    }

    public async Task<OwnerScope> ResolveAsync(ColumnFilter? owner)
    {
        var currentUserId  = _currentUser.GetUserId();
        var visibleUserIds = await _visibility.GetVisibleUserIdsAsync();
        var friendIds      = visibleUserIds.Where(id => id != currentUserId).ToList();

        // Empty / absent owner filter → keep the historical own-only behaviour.
        if (owner is null || owner.IsEmpty)
            return new OwnerScope(null, currentUserId, friendIds);

        var users  = await _userService.GetUsersByIdsAsync(visibleUserIds);
        var names  = OwnerLabels.ToNameMap(users);
        var result = OwnerFilterResolver.Resolve(owner, currentUserId, names);

        IReadOnlyList<string> ownerIds =
            result.NoConstraint ? visibleUserIds            // Everyone → snapshot of all visible owners
          : result.MatchNone   ? Array.Empty<string>()      // name search matched nobody
          : result.UserIds;                                 // explicit selection (Me / Others / names / ids)

        return new OwnerScope(ownerIds, currentUserId, friendIds);
    }
}
