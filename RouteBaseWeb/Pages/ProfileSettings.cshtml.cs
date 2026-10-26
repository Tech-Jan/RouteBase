using RouteBaseWeb.Constants;
using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace RouteBaseWeb.Pages;

public class ProfileSettingsModel : PageModel
{
    private readonly IProfileSettingsService _settingsService;
    private readonly ICurrentUserService _currentUser;
    private readonly ILogger<ProfileSettingsModel> _logger;

    public ProfileSettingsModel(
        IProfileSettingsService settingsService,
        ICurrentUserService currentUser,
        ILogger<ProfileSettingsModel> logger)
    {
        _settingsService = settingsService;
        _currentUser = currentUser;
        _logger = logger;
    }

    // Chart Settings
    [BindProperty]
    public int SpeedSmoothingSeconds { get; set; } = ProfileSettingsDefaults.SpeedSmoothingSeconds;

    [BindProperty]
    public int SpeedBinCount { get; set; } = ProfileSettingsDefaults.SpeedBinCount;

    [BindProperty]
    public string DefaultSpeedView { get; set; } = ProfileSettingsDefaults.DefaultSpeedView;

    [BindProperty]
    public int ElevationSmoothingWindow { get; set; } = ProfileSettingsDefaults.ElevationSmoothingWindow;

    [BindProperty]
    public decimal ChartTension { get; set; } = ProfileSettingsDefaults.ChartTension;

    [BindProperty]
    public int ChartBorderWidth { get; set; } = ProfileSettingsDefaults.ChartBorderWidth;

    [BindProperty]
    public string ChartDetail { get; set; } = ProfileSettingsDefaults.ChartDetail;

    // Map Settings
    [BindProperty]
    public int DefaultMapZoom { get; set; } = ProfileSettingsDefaults.DefaultMapZoom;

    [BindProperty]
    public string DefaultMapStyle { get; set; } = ProfileSettingsDefaults.DefaultMapStyle;

    [BindProperty]
    public decimal HillshadeExaggeration { get; set; } = ProfileSettingsDefaults.HillshadeExaggeration;

    [BindProperty]
    public decimal TerrainExaggeration { get; set; } = ProfileSettingsDefaults.TerrainExaggeration;

    [BindProperty]
    public int FogDistance { get; set; } = ProfileSettingsDefaults.FogDistance;

    // UI Settings
    [BindProperty]
    public string DefaultColorMode { get; set; } = ProfileSettingsDefaults.DefaultColorMode;

    [BindProperty]
    public bool ShowDebugInfo { get; set; } = ProfileSettingsDefaults.ShowDebugInfo;

    // Splits Settings
    [BindProperty]
    public decimal SplitDistanceRunWalk { get; set; } = ProfileSettingsDefaults.SplitDistanceRunWalk;

    [BindProperty]
    public decimal SplitDistanceOther { get; set; } = ProfileSettingsDefaults.SplitDistanceOther;

    // Segment Settings
    [BindProperty]
    public int DefaultMatchThreshold { get; set; } = ProfileSettingsDefaults.DefaultMatchThreshold;

    // Recording Settings
    [BindProperty]
    public bool AutoApproveRecordings { get; set; } = ProfileSettingsDefaults.AutoApproveRecordings;

    // Performance Settings
    // Consumed server-side only (SegmentEffortPerformanceBuilder) — account data with a form, but never
    // a field of SettingsPayload: that payload reaches anonymous share pages.
    [BindProperty]
    public decimal RiderWeightKg { get; set; } = ProfileSettingsDefaults.RiderWeightKg;

    // Privacy Settings
    // "New segments", not "new activities and segments": SegmentCrud is the only reader, and activities
    // take their visibility from the recorder request or an explicit edit (decided 2026-08-02).
    [BindProperty]
    public string DefaultVisibility { get; set; } = ProfileSettingsDefaults.DefaultVisibility;

    [TempData]
    public string? SuccessMessage { get; set; }

    [TempData]
    public string? ErrorMessage { get; set; }

    // Same-request render only (set during OnGet's load) — deliberately NOT [TempData]: a TempData
    // write during a GET survives into the NEXT request, so one transient load failure showed a stale
    // "Error loading settings" banner on the following, perfectly successful visit.
    public string? LoadErrorMessage { get; set; }

    public async Task OnGetAsync()
    {
        await LoadSettingsAsync();
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        // Rejected, not clamped. Silently rewriting someone's 9999 to 301 tells them the save worked and
        // leaves a number they never chose; the browser already refuses it via the catalog-rendered
        // min/max, so reaching here means the constraint was bypassed and saying so is the honest answer.
        Validate();
        if (!ModelState.IsValid)
        {
            ErrorMessage = "Some settings are out of range — nothing was saved.";
            return Page();
        }

        var userId = _currentUser.GetUserId();
        try
        {
            await PersistAllAsync(userId);
            SuccessMessage = "Settings saved successfully!";
            _logger.LogInformation("Profile settings saved");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving profile settings");
            ErrorMessage = "Error saving settings. Please try again.";
        }

        return RedirectToPage();
    }

    /// <summary>
    /// One ModelState error per out-of-range field, from the same catalog entry the form rendered its
    /// min/max/step from — so the HTML constraint and the server rule cannot disagree.
    ///
    /// The closed-set settings are NOT validated here: they are &lt;select&gt;s whose options come from the
    /// same catalog entry, so an unknown value means a forged POST rather than a mistake. Those coerce to
    /// the default in PersistAllAsync instead of blocking the save with an error nobody can act on.
    /// </summary>
    private void Validate()
    {
        Check(nameof(SpeedSmoothingSeconds), SettingsCatalog.SpeedSmoothingSeconds, SpeedSmoothingSeconds);
        Check(nameof(SpeedBinCount), SettingsCatalog.SpeedBinCount, SpeedBinCount);
        Check(nameof(ElevationSmoothingWindow), SettingsCatalog.ElevationSmoothingWindow, ElevationSmoothingWindow);
        Check(nameof(ChartTension), SettingsCatalog.ChartTension, ChartTension);
        Check(nameof(ChartBorderWidth), SettingsCatalog.ChartBorderWidth, ChartBorderWidth);
        Check(nameof(SplitDistanceRunWalk), SettingsCatalog.SplitDistanceRunWalk, SplitDistanceRunWalk);
        Check(nameof(SplitDistanceOther), SettingsCatalog.SplitDistanceOther, SplitDistanceOther);
        Check(nameof(DefaultMapZoom), SettingsCatalog.DefaultMapZoom, DefaultMapZoom);
        Check(nameof(HillshadeExaggeration), SettingsCatalog.HillshadeExaggeration, HillshadeExaggeration);
        Check(nameof(TerrainExaggeration), SettingsCatalog.TerrainExaggeration, TerrainExaggeration);
        Check(nameof(FogDistance), SettingsCatalog.FogDistance, FogDistance);
        Check(nameof(DefaultMatchThreshold), SettingsCatalog.DefaultMatchThreshold, DefaultMatchThreshold);
        Check(nameof(RiderWeightKg), SettingsCatalog.RiderWeightKg, RiderWeightKg);
    }

    private void Check(string property, NumericSetting spec, decimal value)
    {
        if (spec.InRange(value)) return;
        ModelState.AddModelError(property, $"Must be between {spec.MinAttr} and {spec.MaxAttr}.");
    }

    public async Task<IActionResult> OnPostResetAsync()
    {
        var userId = _currentUser.GetUserId();
        ApplyDefaults();
        try
        {
            await PersistAllAsync(userId);
            SuccessMessage = "Settings reset to defaults!";
            _logger.LogInformation("Profile settings reset to defaults");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resetting profile settings");
            ErrorMessage = "Error resetting settings. Please try again.";
        }

        return RedirectToPage();
    }

    // Persists every setting. Throws on a store failure (no internal catch) so a failed save/reset never
    // reports success — the handlers translate the exception into an error banner. Extracted from the old
    // handler-to-handler call (PS-04).
    //
    // Ranges are NOT clamped here any more: OnPostSave rejects out-of-range input with a ModelState error
    // before this runs, and OnPostReset only ever passes the defaults. Reset is the one caller that skips
    // Validate(), which is correct — a default that failed its own rule is a catalog bug, and
    // SettingsCatalogTests pins that it cannot happen.
    private async Task PersistAllAsync(string userId)
    {
        // The <select>s render their options from these same catalog entries, so an unknown value here is
        // a forged POST, not a mistake — coerce rather than persist a broken map style or an ignored
        // color mode (PS-06). Before the catalog this was three hand-kept whitelists, one of which
        // (basemaps) had already drifted and silently reset every keyless choice on save.
        DefaultMapStyle = SettingsCatalog.DefaultMapStyle.Coerce(DefaultMapStyle);
        DefaultColorMode = SettingsCatalog.DefaultColorMode.Coerce(DefaultColorMode);
        ChartDetail = SettingsCatalog.ChartDetail.Coerce(ChartDetail);
        DefaultVisibility = SettingsCatalog.DefaultVisibility.Coerce(DefaultVisibility);

        // One batch → one SaveChanges → one SQLite write transaction (PS-05): a Save click is atomic
        // rather than 17 separate commits, so a mid-sequence failure can't leave settings half-saved.
        var updates = new (string Category, string Key, object Value)[]
        {
            (SettingCategories.Charts, SettingKeys.SpeedSmoothingSeconds, SpeedSmoothingSeconds),
            (SettingCategories.Charts, SettingKeys.SpeedBinCount, SpeedBinCount),
            (SettingCategories.Charts, SettingKeys.DefaultSpeedView, SettingsCatalog.DefaultSpeedView.Coerce(DefaultSpeedView)),
            (SettingCategories.Charts, SettingKeys.ElevationSmoothingWindow, ElevationSmoothingWindow),
            (SettingCategories.Charts, SettingKeys.ChartTension, ChartTension),
            (SettingCategories.Charts, SettingKeys.ChartBorderWidth, ChartBorderWidth),
            (SettingCategories.Charts, SettingKeys.ChartDetail, ChartDetail),
            (SettingCategories.Map, SettingKeys.DefaultMapZoom, DefaultMapZoom),
            (SettingCategories.Map, SettingKeys.DefaultMapStyle, DefaultMapStyle),
            (SettingCategories.Map, SettingKeys.HillshadeExaggeration, HillshadeExaggeration),
            (SettingCategories.Map, SettingKeys.TerrainExaggeration, TerrainExaggeration),
            (SettingCategories.Map, SettingKeys.FogDistance, FogDistance),
            (SettingCategories.UI, SettingKeys.DefaultColorMode, DefaultColorMode),
            (SettingCategories.UI, SettingKeys.ShowDebugInfo, ShowDebugInfo),
            (SettingCategories.Splits, SettingKeys.SplitDistanceRunWalk, SplitDistanceRunWalk),
            (SettingCategories.Splits, SettingKeys.SplitDistanceOther, SplitDistanceOther),
            (SettingCategories.Segments, SettingKeys.DefaultMatchThreshold, DefaultMatchThreshold),
            (SettingCategories.Recording, SettingKeys.AutoApproveRecordings, AutoApproveRecordings),
            (SettingCategories.Performance, SettingKeys.RiderWeightKg, RiderWeightKg),
            (SettingCategories.Privacy, SettingKeys.DefaultVisibility, DefaultVisibility),
        };
        await _settingsService.SetSettingsAsync(updates, userId);
    }

    // Resets every bound property to its shared default constant (PS-03/PS-04).
    private void ApplyDefaults()
    {
        SpeedSmoothingSeconds = ProfileSettingsDefaults.SpeedSmoothingSeconds;
        SpeedBinCount = ProfileSettingsDefaults.SpeedBinCount;
        DefaultSpeedView = ProfileSettingsDefaults.DefaultSpeedView;
        ElevationSmoothingWindow = ProfileSettingsDefaults.ElevationSmoothingWindow;
        ChartTension = ProfileSettingsDefaults.ChartTension;
        ChartBorderWidth = ProfileSettingsDefaults.ChartBorderWidth;
        ChartDetail = ProfileSettingsDefaults.ChartDetail;
        DefaultMapZoom = ProfileSettingsDefaults.DefaultMapZoom;
        DefaultMapStyle = ProfileSettingsDefaults.DefaultMapStyle;
        HillshadeExaggeration = ProfileSettingsDefaults.HillshadeExaggeration;
        TerrainExaggeration = ProfileSettingsDefaults.TerrainExaggeration;
        FogDistance = ProfileSettingsDefaults.FogDistance;
        DefaultColorMode = ProfileSettingsDefaults.DefaultColorMode;
        ShowDebugInfo = ProfileSettingsDefaults.ShowDebugInfo;
        SplitDistanceRunWalk = ProfileSettingsDefaults.SplitDistanceRunWalk;
        SplitDistanceOther = ProfileSettingsDefaults.SplitDistanceOther;
        DefaultMatchThreshold = ProfileSettingsDefaults.DefaultMatchThreshold;
        AutoApproveRecordings = ProfileSettingsDefaults.AutoApproveRecordings;
        RiderWeightKg = ProfileSettingsDefaults.RiderWeightKg;
        DefaultVisibility = ProfileSettingsDefaults.DefaultVisibility;
    }

    private async Task LoadSettingsAsync()
    {
        try
        {
            var userId = _currentUser.GetUserId();

            // Load chart settings
            SpeedSmoothingSeconds = await _settingsService.GetSettingAsync<int>(SettingCategories.Charts, SettingKeys.SpeedSmoothingSeconds, userId, defaultValue: ProfileSettingsDefaults.SpeedSmoothingSeconds);
            SpeedBinCount = await _settingsService.GetSettingAsync<int>(SettingCategories.Charts, SettingKeys.SpeedBinCount, userId, defaultValue: ProfileSettingsDefaults.SpeedBinCount);
            DefaultSpeedView = await _settingsService.GetSettingAsync<string>(SettingCategories.Charts, SettingKeys.DefaultSpeedView, userId, defaultValue: ProfileSettingsDefaults.DefaultSpeedView);
            ElevationSmoothingWindow = await _settingsService.GetSettingAsync<int>(SettingCategories.Charts, SettingKeys.ElevationSmoothingWindow, userId, defaultValue: ProfileSettingsDefaults.ElevationSmoothingWindow);
            ChartTension = await _settingsService.GetSettingAsync<decimal>(SettingCategories.Charts, SettingKeys.ChartTension, userId, defaultValue: ProfileSettingsDefaults.ChartTension);
            ChartBorderWidth = await _settingsService.GetSettingAsync<int>(SettingCategories.Charts, SettingKeys.ChartBorderWidth, userId, defaultValue: ProfileSettingsDefaults.ChartBorderWidth);
            ChartDetail = await _settingsService.GetSettingAsync<string>(SettingCategories.Charts, SettingKeys.ChartDetail, userId, defaultValue: ProfileSettingsDefaults.ChartDetail) ?? ProfileSettingsDefaults.ChartDetail;

            // Load map settings
            DefaultMapZoom = await _settingsService.GetSettingAsync<int>(SettingCategories.Map, SettingKeys.DefaultMapZoom, userId, defaultValue: ProfileSettingsDefaults.DefaultMapZoom);
            DefaultMapStyle = await _settingsService.GetSettingAsync<string>(SettingCategories.Map, SettingKeys.DefaultMapStyle, userId, defaultValue: ProfileSettingsDefaults.DefaultMapStyle) ?? ProfileSettingsDefaults.DefaultMapStyle;
            HillshadeExaggeration = await _settingsService.GetSettingAsync<decimal>(SettingCategories.Map, SettingKeys.HillshadeExaggeration, userId, defaultValue: ProfileSettingsDefaults.HillshadeExaggeration);
            TerrainExaggeration = await _settingsService.GetSettingAsync<decimal>(SettingCategories.Map, SettingKeys.TerrainExaggeration, userId, defaultValue: ProfileSettingsDefaults.TerrainExaggeration);
            FogDistance = await _settingsService.GetSettingAsync<int>(SettingCategories.Map, SettingKeys.FogDistance, userId, defaultValue: ProfileSettingsDefaults.FogDistance);

            // Load UI settings
            DefaultColorMode = await _settingsService.GetSettingAsync<string>(SettingCategories.UI, SettingKeys.DefaultColorMode, userId, defaultValue: ProfileSettingsDefaults.DefaultColorMode) ?? ProfileSettingsDefaults.DefaultColorMode;
            ShowDebugInfo = await _settingsService.GetSettingAsync<bool>(SettingCategories.UI, SettingKeys.ShowDebugInfo, userId, defaultValue: ProfileSettingsDefaults.ShowDebugInfo);

            // Load splits settings
            SplitDistanceRunWalk = await _settingsService.GetSettingAsync<decimal>(SettingCategories.Splits, SettingKeys.SplitDistanceRunWalk, userId, defaultValue: ProfileSettingsDefaults.SplitDistanceRunWalk);
            SplitDistanceOther = await _settingsService.GetSettingAsync<decimal>(SettingCategories.Splits, SettingKeys.SplitDistanceOther, userId, defaultValue: ProfileSettingsDefaults.SplitDistanceOther);

            // Load segment settings
            DefaultMatchThreshold = await _settingsService.GetSettingAsync<int>(SettingCategories.Segments, SettingKeys.DefaultMatchThreshold, userId, defaultValue: ProfileSettingsDefaults.DefaultMatchThreshold);

            // Load recording settings
            AutoApproveRecordings = await _settingsService.GetSettingAsync<bool>(SettingCategories.Recording, SettingKeys.AutoApproveRecordings, userId, defaultValue: ProfileSettingsDefaults.AutoApproveRecordings);

            // Load performance + privacy settings — the two that had no UI until now (SegmentEffortPerformance
            // Builder and SegmentCrud read them, so a user could be affected by a value they could not see).
            RiderWeightKg = await _settingsService.GetSettingAsync<decimal>(SettingCategories.Performance, SettingKeys.RiderWeightKg, userId, defaultValue: ProfileSettingsDefaults.RiderWeightKg);
            DefaultVisibility = await _settingsService.GetSettingAsync<string>(SettingCategories.Privacy, SettingKeys.DefaultVisibility, userId, defaultValue: ProfileSettingsDefaults.DefaultVisibility) ?? ProfileSettingsDefaults.DefaultVisibility;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading profile settings");
            LoadErrorMessage = "Error loading settings. Using defaults.";
        }
    }
}
