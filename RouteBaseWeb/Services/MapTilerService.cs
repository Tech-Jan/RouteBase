using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace RouteBaseWeb.Services;

public class MapTilerService : IMapTilerService
{
    private readonly RouteBaseDbContext _context;
    private readonly ILogger<MapTilerService> _logger;
    private readonly ICurrentUserService _currentUser;

    private readonly TimeProvider _clock;

    public MapTilerService(
        RouteBaseDbContext context,
        ILogger<MapTilerService> logger,
        ICurrentUserService currentUser,
        TimeProvider clock)
    {
        _clock = clock;
        _context = context;
        _logger = logger;
        _currentUser = currentUser;
    }

    public async Task<string?> GetApiKeyAsync()
    {
        try
        {
            var userId = _currentUser.GetUserId();
            var credentials = await _context.AppCredentials
                .Where(c => c.Type == CredentialTypes.MapTilerCredentials && c.UserId == userId)
                .OrderByDescending(c => c.UpdatedAt)
                .FirstOrDefaultAsync();

            if (credentials != null && !string.IsNullOrWhiteSpace(credentials.ApiKey))
                return credentials.ApiKey;

            // Normal state for keyless users (keyless map default) — not a warning.
            _logger.LogDebug("No MapTiler API key found in database");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving MapTiler API key");
            return null;
        }
    }

    public async Task<bool> SaveApiKeyAsync(string apiKey)
    {
        try
        {
            var userId = _currentUser.GetUserId();
            var existing = await _context.AppCredentials
                .Where(c => c.Type == CredentialTypes.MapTilerCredentials && c.UserId == userId)
                .OrderByDescending(c => c.UpdatedAt)
                .FirstOrDefaultAsync();

            if (existing != null)
            {
                existing.ApiKey = apiKey;
                existing.UpdatedAt = _clock.GetUtcNow().UtcDateTime;
            }
            else
            {
                _context.AppCredentials.Add(new AppCredentials
                {
                    Type = CredentialTypes.MapTilerCredentials,
                    ApiKey = apiKey,
                    UserId = userId,
                    CreatedAt = _clock.GetUtcNow().UtcDateTime,
                    UpdatedAt = _clock.GetUtcNow().UtcDateTime
                });
            }

            await _context.SaveChangesAsync();
            _logger.LogInformation("MapTiler API key saved to database");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving MapTiler API key");
            return false;
        }
    }

    public async Task<bool> HasApiKeyAsync()
    {
        var key = await GetApiKeyAsync();
        return !string.IsNullOrWhiteSpace(key);
    }
}
