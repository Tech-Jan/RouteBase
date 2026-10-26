namespace RouteBaseWeb.Services.Interfaces;

public interface IApiKeyService
{
    Task<ApiKeyStatus> GetStatusAsync(string userId);
    Task<ApiKeyGenerateResult> GenerateAsync(string userId);

    /// <summary>Revokes the user's API key if one exists.</summary>
    /// <returns>True if a key was found and revoked; false if none existed.</returns>
    Task<bool> RevokeAsync(string userId);
}

public record ApiKeyStatus(bool HasKey, DateTimeOffset? CreatedAt);

public record ApiKeyGenerateResult(string Plaintext, DateTimeOffset CreatedAt);
