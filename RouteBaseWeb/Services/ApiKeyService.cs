using RouteBaseWeb.Helpers;
using System.Security.Cryptography;
using System.Text;
using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace RouteBaseWeb.Services;

public class ApiKeyService(
    RouteBaseDbContext db,
    ApiKeyCacheGeneration generation,
    TimeProvider clock) : IApiKeyService
{
    public async Task<ApiKeyStatus> GetStatusAsync(string userId)
    {
        var credential = await db.Set<AppCredentials>()
            .Where(c => c.Type == CredentialTypes.MobileApiKey && c.UserId == userId)
            .FirstOrDefaultAsync();

        // ToUtcOffset, not an implicit cast: the SQLite round-trip strips DateTimeKind, and the
        // implicit conversion would read Unspecified as server-local (AR-02).
        return new ApiKeyStatus(credential is not null, credential?.CreatedAt.ToUtcOffset());
    }

    public async Task<ApiKeyGenerateResult> GenerateAsync(string userId)
    {
        // Delete any existing key for this user.
        var existing = await db.Set<AppCredentials>()
            .Where(c => c.Type == CredentialTypes.MobileApiKey && c.UserId == userId)
            .FirstOrDefaultAsync();

        if (existing is not null)
            db.Set<AppCredentials>().Remove(existing);

        // Generate 32-byte random key, Base64Url-encode it (43 URL-safe chars).
        var keyBytes = RandomNumberGenerator.GetBytes(32);
        var plaintext = Base64UrlEncode(keyBytes);
        var hash = HashKey(plaintext);

        var now = clock.GetUtcNow().UtcDateTime;
        db.Set<AppCredentials>().Add(new AppCredentials
        {
            Type      = CredentialTypes.MobileApiKey,
            UserId    = userId,
            ApiKey    = hash,
            CreatedAt = now,
            UpdatedAt = now,
        });

        await db.SaveChangesAsync();

        // CA-06/SVC-08: bump the cache generation AFTER the row is committed. This supersedes the old
        // per-hash cache.Remove — advancing the generation invalidates every cached key at once, so even an
        // auth request that re-cached the just-deleted key mid-flight lands under the stale generation and is
        // never read. Revocation/regeneration is rare, so invalidating the whole namespace is free here.
        generation.Bump();

        return new ApiKeyGenerateResult(plaintext, now.ToUtcOffset());
    }

    public async Task<bool> RevokeAsync(string userId)
    {
        var existing = await db.Set<AppCredentials>()
            .Where(c => c.Type == CredentialTypes.MobileApiKey && c.UserId == userId)
            .FirstOrDefaultAsync();

        if (existing is null)
            return false;

        db.Set<AppCredentials>().Remove(existing);
        await db.SaveChangesAsync();

        // CA-06/SVC-08: bump after save so a concurrent lookup that re-cached the revoked key is invalidated
        // by the moved generation (see GenerateAsync for the full rationale).
        generation.Bump();

        return true;
    }

    private static string HashKey(string rawKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawKey));
#pragma warning disable CA1308 // API key stored/looked-up as lowercase hex; must match persisted rows
        return Convert.ToHexString(bytes).ToLowerInvariant();
#pragma warning restore CA1308
    }

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
