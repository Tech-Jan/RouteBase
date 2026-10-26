namespace RouteBaseWeb.Models;

public class FriendRequestItem
{
    public int FriendshipId { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string? Email { get; set; }
    public string RequestedAt { get; set; } = string.Empty;
}
