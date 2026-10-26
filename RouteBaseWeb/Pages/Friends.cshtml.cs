using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Interfaces;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace RouteBaseWeb.Pages;

public class FriendsModel : PageModel
{
    private readonly IFriendshipService _friendshipService;
    private readonly ICurrentUserService _currentUser;

    public FriendsModel(
        IFriendshipService friendshipService,
        ICurrentUserService currentUser)
    {
        _friendshipService = friendshipService;
        _currentUser = currentUser;
    }

    public List<FriendRequestItem> PendingRequests { get; set; } = new();
    public List<FriendListItem> Friends { get; set; } = new();

    public async Task OnGetAsync()
    {
        // Sequential on purpose: FriendshipService shares the request-scoped DbContext (EF Core forbids
        // concurrent ops). No try/catch — a DB failure must surface via the error page, not render a
        // silently-empty friends list indistinguishable from the real empty state.
        var userId = _currentUser.GetUserId();
        PendingRequests = await _friendshipService.GetPendingRequestsAsync(userId);
        Friends = await _friendshipService.GetFriendsAsync(userId);
    }
}
