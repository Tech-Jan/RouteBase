namespace RouteBaseWeb.Services.Interfaces;

/// <summary>
/// Service for managing MapTiler API credentials.
/// </summary>
public interface IMapTilerService
{
    Task<string?> GetApiKeyAsync();
    Task<bool> SaveApiKeyAsync(string apiKey);
    Task<bool> HasApiKeyAsync();
}
