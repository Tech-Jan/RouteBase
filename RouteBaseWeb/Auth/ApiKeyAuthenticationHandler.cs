using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace RouteBaseWeb.Auth;

public class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    RouteBaseDbContext db,
    IMemoryCache cache,
    ApiKeyCacheGeneration generation)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Api-Key", out var rawKeyValues))
            return AuthenticateResult.NoResult();

        var rawKey = rawKeyValues.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(rawKey))
            return AuthenticateResult.Fail("Empty API key");

        var hash = HashKey(rawKey);

        // Cache lookup to avoid hitting SQLite on every rapid GPS POST. The generation is read once and
        // baked into the key: if a revoke/regenerate bumps it between this read and the Set below, the entry
        // lands under a now-stale generation and is never read again (SVC-08 — closes the re-cache race).
        var cacheKey = CacheKeys.ApiKeyUser(generation.Current, hash);
        if (!cache.TryGetValue(cacheKey, out string? userId))
        {
            var credential = await db.Set<AppCredentials>()
                .Where(c => c.Type == CredentialTypes.MobileApiKey && c.ApiKey == hash)
                .FirstOrDefaultAsync();

            userId = credential?.UserId;

            // CA-07: cache only hits. Caching null lets any distinct invalid key spray a 30 s
            // entry into the SizeLimit-less shared cache. Cost of the trade: an invalid/revoked
            // key now costs one indexed single-row SELECT per request (failure path is not hot).
            if (userId is not null)
                cache.Set(cacheKey, userId, TimeSpan.FromSeconds(30));
        }

        if (string.IsNullOrEmpty(userId))
            return AuthenticateResult.Fail("Invalid API key");

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId),
            new Claim("sub", userId),
        };
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);

        return AuthenticateResult.Success(ticket);
    }

    private static string HashKey(string rawKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawKey));
        // CA1308: lowercase hex IS the stored/compared key format — genuine wire/DB output, not a
        // normalization-for-compare. ToUpperInvariant would invalidate every existing stored API-key hash.
#pragma warning disable CA1308
        return Convert.ToHexString(bytes).ToLowerInvariant();
#pragma warning restore CA1308
    }
}
