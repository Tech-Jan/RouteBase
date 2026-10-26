using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace RouteBaseWeb.Models;

[Table("Friendship")]
public class Friendship
{
    [Key]
    [Column("friendship_id")]
    public int FriendshipId { get; set; }

    [Column("requester_id")]
    public string RequesterId { get; set; } = string.Empty;

    [Column("addressee_id")]
    public string AddresseeId { get; set; } = string.Empty;

    [Column("status")]
    public string Status { get; set; } = FriendshipStatuses.Pending;

    [Column("created_at")]
    public string CreatedAt { get; set; } = string.Empty;

    [Column("updated_at")]
    public string UpdatedAt { get; set; } = string.Empty;

    [ForeignKey("RequesterId")]
    public AppUser? Requester { get; set; }

    [ForeignKey("AddresseeId")]
    public AppUser? Addressee { get; set; }
}
