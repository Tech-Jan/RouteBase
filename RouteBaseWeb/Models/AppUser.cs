using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace RouteBaseWeb.Models;

[Table("Users")]
public class AppUser
{
    /// <summary>OIDC 'sub' claim — the unique identifier from Authentik.</summary>
    [Key]
    [Column("user_id")]
    public string UserId { get; set; } = string.Empty;

    [Column("display_name")]
    public string? DisplayName { get; set; }

    [Column("email")]
    public string? Email { get; set; }

    [Column("is_admin")]
    public bool IsAdmin { get; set; }

    [Column("created_at")]
    public string CreatedAt { get; set; } = string.Empty;

    [Column("last_login")]
    public string LastLogin { get; set; } = string.Empty;
}
