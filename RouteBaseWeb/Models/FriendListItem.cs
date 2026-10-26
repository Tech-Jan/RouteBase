namespace RouteBaseWeb.Models;

public class FriendListItem
{
    public string UserId { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string? Email { get; set; }
    public int FriendshipId { get; set; }
    public string FriendsSince { get; set; } = string.Empty;
}
