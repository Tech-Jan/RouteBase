using System.Text.Json;
using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Credentials;
using RouteBaseWeb.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using RouteBaseWeb.Models.Strava;

namespace RouteBaseWeb.Services;

public class StravaAuthService : IStravaAuthService
{
    // SV-03: a short-lived context per DB op (via the factory) instead of the shared scoped context.
    // The worker runs auth (this service), fetchers, and the persister in ONE scope; on mid-run token
    // expiry a fetcher thread's refresh/save would otherwise run on the same RouteBaseDbContext the
    // persister is inserting on → EF "second operation on this context". SQLite handles the extra
    // short-lived connections (WAL / db-level locking); the token ops are tiny reads/writes.
    private readonly IDbContextFactory<RouteBaseDbContext> _dbFactory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<StravaAuthService> _logger;
    private readonly ICurrentUserService _currentUser;
    private readonly ICredentialProtector _protector;
    private readonly TimeProvider _clock;

    private const string StravaOAuthUrl = "https://www.strava.com/oauth";

    // SV-01: per-scope token cache — hot for the lifetime of an import run (OperationQueueWorker
    // creates one scope per run; thousands of hits) and for paged fetches within one request. The
    // semaphore serializes concurrent cache-miss DB reads from the parallel fetchers. Scoped lifetime
    // is deliberate — an unkeyed singleton (_currentUser is per-scope) would leak tokens across users.
    // Token + expiry travel as ONE immutable pair: the fast path reads outside the semaphore, and two
    // separate fields could tear (fresh token with stale expiry, or an expired token with a new expiry).
    private sealed record CachedToken(string Token, DateTime Expiry);
    private volatile CachedToken? _cached;
    private readonly SemaphoreSlim _tokenSem = new(1, 1);

    public StravaAuthService(
        IDbContextFactory<RouteBaseDbContext> dbFactory,
        IHttpClientFactory httpClientFactory,
        ILogger<StravaAuthService> logger,
        ICurrentUserService currentUser,
        ICredentialProtector protector,
        TimeProvider clock)
    {
        _dbFactory = dbFactory;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _currentUser = currentUser;
        _protector = protector;
        _clock = clock;
    }

    public async Task<bool> IsAuthenticatedAsync()
    {
        var userId = _currentUser.GetUserId();
        await using var db = await _dbFactory.CreateDbContextAsync();
        var token = await db.AppCredentials
            .FirstOrDefaultAsync(c => c.Type == CredentialTypes.StravaToken && c.UserId == userId);
        return token != null;
    }

    public async Task<StravaAthlete?> GetAthleteAsync()
    {
        var userId = _currentUser.GetUserId();
        await using var db = await _dbFactory.CreateDbContextAsync();
        var token = await db.AppCredentials
            .FirstOrDefaultAsync(c => c.Type == CredentialTypes.StravaToken && c.UserId == userId);
        return token != null ? new StravaAthlete { Id = token.AthleteId ?? 0 } : null;
    }

    public string GetAuthorizationUrl(string redirectUri, string? clientId, string? state = null)
    {
        if (string.IsNullOrEmpty(clientId))
            throw new InvalidOperationException("Strava Client ID is required. Configure it on the API Keys & Database page.");

        var scope = "read,activity:read_all";
        var url = $"{StravaOAuthUrl}/authorize?client_id={clientId}&response_type=code&redirect_uri={Uri.EscapeDataString(redirectUri)}&approval_prompt=auto&scope={scope}";
        if (!string.IsNullOrEmpty(state))
            url += $"&state={Uri.EscapeDataString(state)}";
        return url;
    }

    public async Task<StravaTokenResponse?> ExchangeCodeForTokenAsync(string code)
    {
        try
        {
            var (clientId, clientSecret) = await GetCredentialsAsync();

            var client = _httpClientFactory.CreateClient();
            var requestData = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "client_id",     clientId     ?? "" },
                { "client_secret", clientSecret ?? "" },
                { "code",          code },
                { "grant_type",    "authorization_code" }
            };

            var response = await client.PostAsync($"{StravaOAuthUrl}/token", new FormUrlEncodedContent(requestData));

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Failed to exchange code for token: {StatusCode}", response.StatusCode);
                return null;
            }

            var content = await response.Content.ReadAsStringAsync();
            var tokenResponse = JsonSerializer.Deserialize<StravaTokenResponse>(content);

            if (tokenResponse != null)
                await SaveTokenAsync(tokenResponse);

            return tokenResponse;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error exchanging code for token");
            return null;
        }
    }

    public async Task<string?> GetValidAccessTokenAsync()
    {
        if (_cached is { } fast && _clock.GetUtcNow().UtcDateTime < fast.Expiry)
            return fast.Token;

        await _tokenSem.WaitAsync();
        try
        {
            if (_cached is { } hit && _clock.GetUtcNow().UtcDateTime < hit.Expiry)
                return hit.Token;

            var userId = _currentUser.GetUserId();
            await using var db = await _dbFactory.CreateDbContextAsync();
            // AsNoTracking: RefreshTokenAsync saves on a separate context, so a tracking
            // re-read below would hit EF's identity map and return the stale (expired) token
            // instead of the freshly-refreshed DB values → a 401 that only clears on reconnect.
            var token = await db.AppCredentials.AsNoTracking()
                .FirstOrDefaultAsync(c => c.Type == CredentialTypes.StravaToken && c.UserId == userId);

            if (token == null)
            {
                _logger.LogWarning("No Strava token found");
                return null;
            }

            // Null expiry = UNKNOWN validity (legacy/partial row) → refresh to learn it. The lifted null
            // comparison silently skipped the refresh forever, and the MinValue cache expiry below
            // defeated the cache on every call — ending in 401s recoverable only by reconnecting.
            if (token.ExpiresAt is null || token.ExpiresAt <= _clock.GetUtcNow().UtcDateTime.AddMinutes(5))
            {
                _logger.LogInformation("Token expired, refreshing...");
                var refreshed = await RefreshTokenAsync(token);
                if (!refreshed)
                    return null;
                token = await db.AppCredentials.AsNoTracking()
                    .FirstOrDefaultAsync(c => c.Type == CredentialTypes.StravaToken && c.UserId == userId);
            }

            // Rows are encrypted at rest (decision ⑥); cache and return the plaintext.
            if (token?.AccessToken is { } storedAccess && _protector.Unprotect(storedAccess) is { } access)
            {
                _cached = new CachedToken(access, token.ExpiresAt?.AddMinutes(-5) ?? DateTime.MinValue);
                return access;
            }
            _cached = null;
            return null;
        }
        finally
        {
            _tokenSem.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        var userId = _currentUser.GetUserId();
        await using var db = await _dbFactory.CreateDbContextAsync();
        var stravaRows = await db.AppCredentials
            .Where(c => c.UserId == userId &&
                       (c.Type == CredentialTypes.StravaToken ||
                        c.Type == CredentialTypes.StravaCredentials))
            .ToListAsync();

        if (stravaRows.Count > 0)
        {
            db.AppCredentials.RemoveRange(stravaRows);
            await db.SaveChangesAsync();
            _logger.LogInformation("Disconnected from Strava — removed {Count} credential row(s) ({Types})",
                stravaRows.Count,
                string.Join(", ", stravaRows.Select(r => r.Type)));
        }
        else
        {
            _logger.LogWarning("DisconnectAsync called but no Strava rows found in AppCredentials");
        }
    }

    public async Task<string?> GetSavedClientIdAsync()
    {
        try
        {
            var userId = _currentUser.GetUserId();
            await using var db = await _dbFactory.CreateDbContextAsync();
            var credentials = await db.AppCredentials
                .FirstOrDefaultAsync(c => c.Type == CredentialTypes.StravaCredentials && c.UserId == userId);
            return credentials?.ClientId;
        }
        catch
        {
            return null;
        }
    }

    public async Task SaveCredentialsAsync(string clientId, string clientSecret)
    {
        var userId = _currentUser.GetUserId();
        await using var db = await _dbFactory.CreateDbContextAsync();
        var existing = await db.AppCredentials
            .FirstOrDefaultAsync(c => c.Type == CredentialTypes.StravaCredentials && c.UserId == userId);

        // ClientId is public by design (it rides in the OAuth redirect URL) — only the secret is
        // encrypted at rest (decision ⑥ extension, 2026-07-18).
        if (existing != null)
        {
            existing.ClientId     = clientId;
            existing.ClientSecret = _protector.Protect(clientSecret);
            existing.UpdatedAt    = _clock.GetUtcNow().UtcDateTime;
        }
        else
        {
            db.AppCredentials.Add(new AppCredentials
            {
                Type         = CredentialTypes.StravaCredentials,
                ClientId     = clientId,
                ClientSecret = _protector.Protect(clientSecret),
                UserId       = userId,
                CreatedAt    = _clock.GetUtcNow().UtcDateTime,
                UpdatedAt    = _clock.GetUtcNow().UtcDateTime
            });
        }

        await db.SaveChangesAsync();
        _logger.LogInformation("Strava credentials saved successfully");
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private async Task<(string? clientId, string? clientSecret)> GetCredentialsAsync()
    {
        var userId = _currentUser.GetUserId();
        await using var db = await _dbFactory.CreateDbContextAsync();
        var credentials = await db.AppCredentials
            .FirstOrDefaultAsync(c => c.Type == CredentialTypes.StravaCredentials && c.UserId == userId);
        return credentials != null
            ? (credentials.ClientId,
               credentials.ClientSecret is { } storedSecret ? _protector.Unprotect(storedSecret) : null)
            : (null, null);
    }

    private async Task SaveTokenAsync(StravaTokenResponse tokenResponse)
    {
        if (tokenResponse.AccessToken == null || tokenResponse.RefreshToken == null)
            return;

        var userId = _currentUser.GetUserId();
        await using var db = await _dbFactory.CreateDbContextAsync();
        var existing = await db.AppCredentials
            .FirstOrDefaultAsync(c => c.Type == CredentialTypes.StravaToken && c.UserId == userId);
        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(tokenResponse.ExpiresAt).DateTime;

        // Refresh responses omit the athlete object — only the initial code exchange carries it.
        // Preserve the existing AthleteId on refresh instead of clobbering it to 0.
        if (existing != null)
        {
            existing.AccessToken  = _protector.Protect(tokenResponse.AccessToken);
            existing.RefreshToken = _protector.Protect(tokenResponse.RefreshToken);
            existing.ExpiresAt    = expiresAt;
            existing.AthleteId    = tokenResponse.Athlete?.Id ?? existing.AthleteId;
            existing.UpdatedAt    = _clock.GetUtcNow().UtcDateTime;
        }
        else
        {
            db.AppCredentials.Add(new AppCredentials
            {
                Type         = CredentialTypes.StravaToken,
                AccessToken  = _protector.Protect(tokenResponse.AccessToken),
                RefreshToken = _protector.Protect(tokenResponse.RefreshToken),
                ExpiresAt    = expiresAt,
                AthleteId    = tokenResponse.Athlete?.Id,
                UserId       = userId,
                CreatedAt    = _clock.GetUtcNow().UtcDateTime,
                UpdatedAt    = _clock.GetUtcNow().UtcDateTime
            });
        }

        await db.SaveChangesAsync();
    }

    private async Task<bool> RefreshTokenAsync(AppCredentials token)
    {
        try
        {
            var (clientId, clientSecret) = await GetCredentialsAsync();

            var client = _httpClientFactory.CreateClient();
            var requestData = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "client_id",     clientId     ?? "" },
                { "client_secret", clientSecret ?? "" },
                { "refresh_token", token.RefreshToken is { } storedRefresh ? _protector.Unprotect(storedRefresh) ?? "" : "" },
                { "grant_type",    "refresh_token" }
            };

            var response = await client.PostAsync($"{StravaOAuthUrl}/token", new FormUrlEncodedContent(requestData));

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Failed to refresh token: {StatusCode}", response.StatusCode);
                return false;
            }

            var content = await response.Content.ReadAsStringAsync();
            var tokenResponse = JsonSerializer.Deserialize<StravaTokenResponse>(content);

            if (tokenResponse?.AccessToken != null && tokenResponse.RefreshToken != null)
            {
                await SaveTokenAsync(tokenResponse);
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error refreshing token");
            return false;
        }
    }
}
