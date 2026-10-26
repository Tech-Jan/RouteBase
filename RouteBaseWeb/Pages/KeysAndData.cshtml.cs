using RouteBaseWeb.Constants;
using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace RouteBaseWeb.Pages;

public class KeysAndDataModel : PageModel
{
    private readonly IStravaAuthService _auth;
    private readonly IDatabaseResetService _dbReset;
    private readonly IMapTilerService _mapTilerService;
    private readonly ILogger<KeysAndDataModel> _logger;

    public KeysAndDataModel(
        IStravaAuthService auth,
        IDatabaseResetService dbReset,
        IMapTilerService mapTilerService,
        ILogger<KeysAndDataModel> logger)
    {
        _auth            = auth;
        _dbReset         = dbReset;
        _mapTilerService = mapTilerService;
        _logger          = logger;
    }

    public bool IsAuthenticated { get; set; }
    public long? AthleteId { get; set; }
    public string? ErrorMessage { get; set; }
    public string? SuccessMessage { get; set; }
    public string? ClientId { get; set; }
    public bool HasMapTilerKey { get; set; }

    public async Task<IActionResult> OnGetAsync(string? code = null, string? error = null, string? state = null)
    {
        if (!string.IsNullOrEmpty(code))
        {
            // Verify the CSRF nonce set when the authorize URL was built (PS-02): single-use,
            // so remove it regardless. A missing/mismatched state means the code wasn't minted
            // by this session — refuse the exchange to block cross-account token injection.
            var expectedState = HttpContext.Session.GetString(SessionKeys.StravaOAuthState);
            HttpContext.Session.Remove(SessionKeys.StravaOAuthState);
            if (string.IsNullOrEmpty(expectedState) || state != expectedState)
            {
                TempData["ErrorMessage"] = "Strava authorization could not be verified. Please try connecting again.";
                _logger.LogWarning("Strava OAuth callback rejected: state mismatch or missing session nonce");
                return RedirectToPage();
            }

            var tokenResponse = await _auth.ExchangeCodeForTokenAsync(code);
            if (tokenResponse != null)
            {
                TempData["SuccessMessage"] = "Successfully connected to Strava!";
                _logger.LogInformation("Successfully authenticated with Strava for athlete {AthleteId}", tokenResponse.Athlete?.Id);
            }
            else
            {
                TempData["ErrorMessage"] = "Failed to connect to Strava. Please try again.";
                _logger.LogError("Failed to exchange code for token");
            }

            return RedirectToPage();
        }

        SuccessMessage = TempData["SuccessMessage"] as string;
        ErrorMessage   = TempData["ErrorMessage"]   as string;

        if (!string.IsNullOrEmpty(error))
        {
            _logger.LogWarning("Strava authentication error: {Error}", error);
            // PI-01: reflect the value only when it has the shape of an OAuth error code (short snake_case
            // token) — never arbitrary query text, which a crafted link could use to plant a (Razor-encoded,
            // so non-scripting) phishing message in the authenticated user's own banner. Raw value logged above.
            ErrorMessage = LooksLikeOAuthErrorCode(error)
                ? $"Authentication error: {error}"
                : "Strava authentication failed. Please try again.";
        }

        await LoadPageDataAsync();

        return Page();
    }

    // OAuth 2.0 error codes are short snake_case tokens (access_denied, invalid_scope, …); anything else is
    // not a code this app minted an authorize link for, so it is not reflected back to the page.
    private static bool LooksLikeOAuthErrorCode(string value) =>
        value.Length <= 40 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    private async Task LoadPageDataAsync()
    {
        ClientId = await _auth.GetSavedClientIdAsync();

        try
        {
            HasMapTilerKey = await _mapTilerService.HasApiKeyAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "KeysAndData page data load failed ({Part})", "maptiler");
            HasMapTilerKey = false;
        }

        try
        {
            IsAuthenticated = await _auth.IsAuthenticatedAsync();

            if (IsAuthenticated)
            {
                var athlete = await _auth.GetAthleteAsync();
                AthleteId = athlete?.Id;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "KeysAndData page data load failed ({Part})", "strava");
            IsAuthenticated = false;
        }
    }

    // Connect flow lives entirely in keys-and-data.ts → POST /api/strava/auth-url (state-verified).
    // The old OnPostConnect page handler was a dead JS-off duplicate — removed (PS-08).

    public async Task<IActionResult> OnPostDisconnectAsync()
    {
        await _auth.DisconnectAsync();
        TempData["SuccessMessage"] = "Successfully disconnected from Strava.";
        _logger.LogInformation("Disconnected from Strava");

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostResetDatabaseAsync(
        bool delActivities     = false,
        bool delGps            = false,
        bool delPhotos         = false,
        bool delMultiDay       = false,
        bool delSegmentMatches = false,
        bool delSegments       = false,
        bool delStravaToken    = false,
        bool delStravaApiKey   = false,
        bool delMapTiler       = false,
        bool delProfileSettings = false,
        string? confirmationText = null)
    {
        // Server-side confirmation, not just the client-JS button gate: this is an irreversible
        // multi-table hard delete — a stray resubmission or a JS bug must not reach it.
        if (!string.Equals(confirmationText?.Trim(), "DELETE", StringComparison.Ordinal))
        {
            TempData["ErrorMessage"] = "Reset not confirmed — type DELETE to confirm.";
            return RedirectToPage();
        }

        var opts = new DeleteOptions
        {
            Activities      = delActivities,
            Gps             = delGps,
            Photos          = delPhotos,
            MultiDayTours   = delMultiDay,
            SegmentMatches  = delSegmentMatches,
            Segments        = delSegments,
            StravaToken     = delStravaToken,
            StravaApiKey    = delStravaApiKey,
            MapTilerApiKey  = delMapTiler,
            ProfileSettings = delProfileSettings
        };

        if (!opts.Activities && !opts.Gps && !opts.Photos && !opts.MultiDayTours &&
            !opts.SegmentMatches && !opts.Segments && !opts.StravaToken &&
            !opts.StravaApiKey && !opts.MapTilerApiKey && !opts.ProfileSettings)
        {
            ErrorMessage = "Nothing selected — please check at least one item to delete.";
            await LoadPageDataAsync();
            return Page();
        }

        try
        {
            _logger.LogWarning("Granular database reset initiated: {@Options}", opts);
            await _dbReset.ResetDatabaseAsync(opts);
            TempData["SuccessMessage"] = "Selected data deleted successfully.";
        }
        catch (Exception ex)
        {
            // Generic banner — ex.Message can carry SQL fragments/paths; the log has the details.
            _logger.LogError(ex, "Error during granular database reset");
            TempData["ErrorMessage"] = "Failed to delete data — see the error log for details.";
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostSaveMapTilerKeyAsync(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            ErrorMessage = "MapTiler API key is required.";
            await LoadPageDataAsync();
            return Page();
        }

        try
        {
            var success = await _mapTilerService.SaveApiKeyAsync(apiKey);
            if (success)
            {
                // PRG: redirect on success (like the sibling handlers) so a refresh doesn't re-post the key.
                TempData["SuccessMessage"] = "MapTiler API key saved successfully!";
                _logger.LogInformation("MapTiler API key saved successfully");
                return RedirectToPage();
            }

            ErrorMessage = "Failed to save MapTiler API key.";
        }
        catch (Exception ex)
        {
            // Generic banner — ex.Message can carry internals; the log has the details.
            _logger.LogError(ex, "Error saving MapTiler API key");
            ErrorMessage = "Failed to save MapTiler API key — see the error log for details.";
        }

        await LoadPageDataAsync();
        return Page();
    }
}
