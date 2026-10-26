using RouteBaseWeb.Models;

namespace RouteBaseWeb.Services.Interfaces;

public interface IFriendshipService
{
    Task<List<string>> GetAcceptedFriendIdsAsync(string userId);
    Task<List<FriendListItem>> GetFriendsAsync(string userId);
    Task<List<FriendRequestItem>> GetPendingRequestsAsync(string userId);
    Task<int> GetPendingRequestCountAsync(string userId);
    Task SendRequestAsync(string requesterId, string addresseeId);
    Task AcceptRequestAsync(string currentUserId, int friendshipId);
    Task DeclineRequestAsync(string currentUserId, int friendshipId);
    Task RemoveFriendAsync(string currentUserId, int friendshipId);
    Task<List<UserSearchResult>> SearchUsersAsync(string query, string excludeUserId);
}
