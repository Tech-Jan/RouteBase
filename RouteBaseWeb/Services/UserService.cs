using System.Security.Claims;
using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace RouteBaseWeb.Services;

public class UserService : IUserService
{
    private readonly RouteBaseDbContext _context;
    private readonly ILogger<UserService> _logger;

    private readonly TimeProvider _clock;

    public UserService(RouteBaseDbContext context, ILogger<UserService> logger, TimeProvider clock)
    {
        _clock = clock;
        _context = context;
        _logger = logger;
    }

    public string GetUserIdFromClaims(ClaimsPrincipal principal)
    {
        var sub = principal.FindFirstValue(ClaimTypes.NameIdentifier)
                  ?? principal.FindFirstValue("sub");

        if (string.IsNullOrEmpty(sub))
            throw new InvalidOperationException("OIDC principal has no 'sub' claim.");

        return sub;
    }

    public async Task<string> ProvisionOrUpdateUserAsync(ClaimsPrincipal principal)
    {
        var userId = GetUserIdFromClaims(principal);
        var now = _clock.GetUtcNow().UtcDateTime.ToString("o", System.Globalization.CultureInfo.InvariantCulture);

        var displayName = principal.FindFirstValue("preferred_username")
                          ?? principal.FindFirstValue("name")
                          ?? principal.FindFirstValue(ClaimTypes.Name)
                          ?? "Unknown";

        var email = principal.FindFirstValue(ClaimTypes.Email)
                    ?? principal.FindFirstValue("email");

        var existing = await _context.Users.FindAsync(userId);

        if (existing is null)
        {
            _context.Users.Add(new AppUser
            {
                UserId = userId,
                DisplayName = displayName,
                Email = email,
                IsAdmin = false,
                CreatedAt = now,
                LastLogin = now
            });
            _logger.LogInformation("Provisioned new user {UserId} ({DisplayName})", userId, displayName);
        }
        else
        {
            existing.DisplayName = displayName;
            existing.Email = email;
            existing.LastLogin = now;
            _logger.LogInformation("Updated user {UserId} ({DisplayName})", userId, displayName);
        }

        await _context.SaveChangesAsync();
        return userId;
    }

    public async Task<AppUser?> GetUserByIdAsync(string userId)
    {
        return await _context.Users.FindAsync(userId);
    }

    public async Task<List<AppUser>> GetUsersByIdsAsync(List<string> userIds)
    {
        if (userIds.Count == 0) return [];
        return await _context.Users
            .Where(u => userIds.Contains(u.UserId))
            .ToListAsync();
    }

    public async Task<List<AppUser>> SearchUsersAsync(string query, string excludeUserId)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];

        // Both display name and email match ONLY on the exact value (case-insensitive). Substring
        // matching let any user enumerate accounts (q="b" found "b3n") and harvest emails (q="@").
#pragma warning disable CA1308 // compared against SQL LOWER() below; the .NET side must lowercase to match
        var lower = query.Trim().ToLowerInvariant();
#pragma warning restore CA1308
        // CA1304 false positive: these ToLower() calls sit inside an EF expression tree and
        // translate to SQL LOWER() — no .NET culture is ever consulted. Rewriting to
        // ToLowerInvariant would change the SQL semantics (EF cannot translate it).
#pragma warning disable CA1304, MA0011 // EF expression tree -> SQL LOWER(); no .NET culture consulted, and a CultureInfo overload can't be translated
        return await _context.Users
            .Where(u => u.UserId != excludeUserId
                && (u.DisplayName != null && u.DisplayName.ToLower() == lower
                    || u.Email != null && u.Email.ToLower() == lower))
#pragma warning restore CA1304, MA0011
            .OrderBy(u => u.DisplayName) // deterministic Take(20)
            .Take(20)
            .ToListAsync();
    }
}
