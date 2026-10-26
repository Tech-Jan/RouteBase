using System.Security.Claims;

namespace RouteBaseWeb.Services.Interfaces;

/// <summary>
/// Manages local user records. Auto-provisions on first OIDC login,
/// updates display name / email / last_login on subsequent logins.
/// </summary>
public interface IUserService
{
    /// <summary>
    /// Upserts a user row from OIDC claims. Returns the user_id (sub claim).
    /// Called from the OIDC OnTicketReceived event.
    /// </summary>
    Task<string> ProvisionOrUpdateUserAsync(ClaimsPrincipal principal);

    /// <summary>Extracts the OIDC 'sub' claim from a ClaimsPrincipal.</summary>
    string GetUserIdFromClaims(ClaimsPrincipal principal);

    /// <summary>Returns a single user by ID, or null if not found.</summary>
    Task<RouteBaseWeb.Models.AppUser?> GetUserByIdAsync(string userId);

    /// <summary>Returns users matching the given IDs.</summary>
    Task<List<RouteBaseWeb.Models.AppUser>> GetUsersByIdsAsync(List<string> userIds);

    /// <summary>Searches users by display name or email (case-insensitive).</summary>
    Task<List<RouteBaseWeb.Models.AppUser>> SearchUsersAsync(string query, string excludeUserId);
}
