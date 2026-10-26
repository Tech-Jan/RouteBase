namespace RouteBaseWeb.Models;

public class UserSearchResult
{
    public string UserId { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string? Email { get; set; }
    public string FriendshipStatus { get; set; } = "none";
}
