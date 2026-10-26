using RouteBaseWeb.Constants;
using RouteBaseWeb.Models;
using RouteBaseWeb.Pages;
using RouteBaseWeb.Services.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace RouteBaseWeb.Tests.Pages;

/// <summary>
/// Pins ProfileSettings page-model behavior across the PS-03..06 refactors: load-defaults equal the shared
/// consts (and equal reset-defaults), a failed persist reports an error (not success), and unknown string
/// settings fall back.
///
/// What is NO LONGER here: the clamp cases. Validation moved to <see cref="RouteBaseWeb.Constants.SettingsCatalog"/>
/// and the page now REJECTS out-of-range input with a ModelState error rather than clamping it silently,
/// so those tests were not violated — they pinned a behaviour this page deliberately gave up. The
/// arithmetic lives in SettingsCatalogTests; what stays here is that bad input is refused and writes
/// nothing.
/// </summary>
public class ProfileSettingsModelTests
{
    private static (ProfileSettingsModel model, FakeProfileSettings settings) CreateModel()
    {
        var settings = new FakeProfileSettings();
        var httpContext = new DefaultHttpContext();
        var model = new ProfileSettingsModel(settings, new FakeCurrentUser(),
            NullLogger<ProfileSettingsModel>.Instance)
        {
            PageContext = new PageContext { HttpContext = httpContext },
            TempData = new TempDataDictionary(httpContext, new FakeTempData()),
        };
        return (model, settings);
    }

    // ── PS-03: load-defaults == consts (and, since both sides use consts, == reset-defaults) ──

    [Fact]
    public async Task OnGet_EmptyStore_YieldsDefaults()
    {
        var (model, _) = CreateModel();

        await model.OnGetAsync();

        Assert.Equal(ProfileSettingsDefaults.SpeedSmoothingSeconds, model.SpeedSmoothingSeconds);
        Assert.Equal(ProfileSettingsDefaults.ElevationSmoothingWindow, model.ElevationSmoothingWindow);
        Assert.Equal(ProfileSettingsDefaults.ChartTension, model.ChartTension);
        Assert.Equal(ProfileSettingsDefaults.ChartBorderWidth, model.ChartBorderWidth);
        Assert.Equal(ProfileSettingsDefaults.DefaultMapZoom, model.DefaultMapZoom);
        Assert.Equal(ProfileSettingsDefaults.DefaultMapStyle, model.DefaultMapStyle);
        Assert.Equal(ProfileSettingsDefaults.HillshadeExaggeration, model.HillshadeExaggeration);
        Assert.Equal(ProfileSettingsDefaults.FogDistance, model.FogDistance);
        Assert.Equal(ProfileSettingsDefaults.DefaultColorMode, model.DefaultColorMode);
        Assert.Equal(ProfileSettingsDefaults.ShowDebugInfo, model.ShowDebugInfo);
        Assert.Equal(ProfileSettingsDefaults.SplitDistanceRunWalk, model.SplitDistanceRunWalk);
        Assert.Equal(ProfileSettingsDefaults.SplitDistanceOther, model.SplitDistanceOther);
        Assert.Equal(ProfileSettingsDefaults.DefaultMatchThreshold, model.DefaultMatchThreshold);
        // The two that had no UI until the catalog step — they are loaded now, so they must default too.
        Assert.Equal(ProfileSettingsDefaults.RiderWeightKg, model.RiderWeightKg);
        Assert.Equal(ProfileSettingsDefaults.DefaultVisibility, model.DefaultVisibility);
    }

    // ── Out-of-range input is REJECTED, not clamped ───────────────────────────
    // The contract changed deliberately: clamping told the user the save worked and stored a number they
    // never typed. The clamp ARITHMETIC moved to SettingsCatalogTests, which is where the rule now lives;
    // what this page owes is that bad input produces an error and writes NOTHING.

    [Fact]
    public async Task OnPostSave_OutOfRange_AddsModelErrorAndSavesNothing()
    {
        var (model, settings) = CreateModel();
        model.SpeedSmoothingSeconds = 9999;  // above max 300
        model.ChartTension = 5.0m;           // above max 1.0

        var result = await model.OnPostSaveAsync();

        Assert.IsType<PageResult>(result);   // re-render, not the PRG redirect
        Assert.False(model.ModelState.IsValid);
        Assert.Contains(nameof(model.SpeedSmoothingSeconds), model.ModelState.Keys, StringComparer.Ordinal);
        Assert.Contains(nameof(model.ChartTension), model.ModelState.Keys, StringComparer.Ordinal);
        Assert.NotNull(model.ErrorMessage);
        Assert.Null(model.SuccessMessage);

        // ATOMIC: one bad field must not persist the fifteen good ones, or a rejected save silently
        // half-applies and the banner is a lie.
        Assert.Empty(settings.Saved);
    }

    [Fact]
    public async Task OnPostSave_InRange_SavesAndRedirects()
    {
        var (model, settings) = CreateModel();
        model.SpeedSmoothingSeconds = 25;

        var result = await model.OnPostSaveAsync();

        Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal(25, settings.Saved[$"{SettingCategories.Charts}.{SettingKeys.SpeedSmoothingSeconds}"]);
    }

    // ── The two settings that had no UI ───────────────────────────────────────

    [Fact]
    public async Task OnPostSave_PersistsRiderWeightAndDefaultVisibility()
    {
        var (model, settings) = CreateModel();
        model.RiderWeightKg = 82.5m;
        model.DefaultVisibility = Visibilities.Public;

        await model.OnPostSaveAsync();

        Assert.Equal(82.5m, settings.Saved[$"{SettingCategories.Performance}.{SettingKeys.RiderWeightKg}"]);
        Assert.Equal(Visibilities.Public, settings.Saved[$"{SettingCategories.Privacy}.{SettingKeys.DefaultVisibility}"]);
    }

    [Fact]
    public async Task OnPostSave_ForgedVisibility_CoercesToDefault()
    {
        var (model, settings) = CreateModel();
        model.DefaultVisibility = "everyone";   // not a Visibilities value

        await model.OnPostSaveAsync();

        // Coerced, not rejected: the <select> renders only catalog options, so an unknown value is a
        // forged POST rather than a mistake a user could correct from an error message. Persisting it
        // would make every new segment invisible to VisibilityService — including to its owner.
        Assert.Equal(ProfileSettingsDefaults.DefaultVisibility,
            settings.Saved[$"{SettingCategories.Privacy}.{SettingKeys.DefaultVisibility}"]);
    }

    // ── PS-04: failed persist reports error, not success ──────────────────────

    [Fact]
    public async Task OnPostReset_StoreThrows_SetsErrorAndNoSuccess()
    {
        var (model, settings) = CreateModel();
        settings.ThrowOnSet = true;

        await model.OnPostResetAsync();

        Assert.NotNull(model.ErrorMessage);
        Assert.Null(model.SuccessMessage);
    }

    [Fact]
    public async Task OnPostReset_Succeeds_SetsResetMessage()
    {
        var (model, _) = CreateModel();

        await model.OnPostResetAsync();

        Assert.Equal("Settings reset to defaults!", model.SuccessMessage);
        Assert.Null(model.ErrorMessage);
    }

    // ── PS-06: unknown string setting falls back to default ───────────────────

    [Fact]
    public async Task OnPostSave_UnknownMapStyle_FallsBackToDefault()
    {
        var (model, settings) = CreateModel();
        model.DefaultMapStyle = "bogus";
        model.DefaultColorMode = "nonsense";

        await model.OnPostSaveAsync();

        Assert.Equal(ProfileSettingsDefaults.DefaultMapStyle,
            settings.Saved[$"{SettingCategories.Map}.{SettingKeys.DefaultMapStyle}"]);
        Assert.Equal(ProfileSettingsDefaults.DefaultColorMode,
            settings.Saved[$"{SettingCategories.UI}.{SettingKeys.DefaultColorMode}"]);
    }

    [Fact]
    public async Task OnPostSave_ValidMapStyle_IsPreserved()
    {
        var (model, settings) = CreateModel();
        model.DefaultMapStyle = "satellite";

        await model.OnPostSaveAsync();

        Assert.Equal("satellite", settings.Saved[$"{SettingCategories.Map}.{SettingKeys.DefaultMapStyle}"]);
    }

    // Regression: keyless basemaps (liberty/bright/...) used to be rejected by a stale 3-item
    // whitelist and silently reset to the default on save. The whitelist now derives from
    // MapBasemaps.Catalog, so every catalog id persists.
    [Fact]
    public async Task OnPostSave_KeylessBasemap_IsPreserved()
    {
        var (model, settings) = CreateModel();
        model.DefaultMapStyle = "liberty";

        await model.OnPostSaveAsync();

        Assert.Equal("liberty", settings.Saved[$"{SettingCategories.Map}.{SettingKeys.DefaultMapStyle}"]);
    }

    // ── Fakes ─────────────────────────────────────────────────────────────────

    private sealed class FakeProfileSettings : IProfileSettingsService
    {
        public readonly Dictionary<string, object> Saved = new(StringComparer.Ordinal);
        public bool ThrowOnSet { get; set; }

        public Task<T?> GetSettingAsync<T>(string category, string key, string? userId = null, T? defaultValue = default)
            => Task.FromResult(defaultValue);
        public Task<string?> GetSettingAsync(string category, string key, string? userId = null)
            => Task.FromResult<string?>(null);
        public Task<bool> SetSettingAsync(string category, string key, object value, string userId)
        {
            if (ThrowOnSet) throw new InvalidOperationException("simulated store failure");
            Saved[$"{category}.{key}"] = value;
            return Task.FromResult(true);
        }
        public Task SetSettingsAsync(IReadOnlyList<(string Category, string Key, object Value)> settings, string userId)
        {
            if (ThrowOnSet) throw new InvalidOperationException("simulated store failure");
            foreach (var s in settings) Saved[$"{s.Category}.{s.Key}"] = s.Value;
            return Task.CompletedTask;
        }
        // Reads only — no fake here stores anything a delete could remove.
        public Task<bool> DeleteSettingAsync(string category, string key, string userId) =>
            Task.FromResult(false);

        public Task<Dictionary<string, string>> GetCategorySettingsAsync(string category, string? userId = null)
            => Task.FromResult(new Dictionary<string, string>(StringComparer.Ordinal));
    }

    private sealed class FakeCurrentUser : ICurrentUserService
    {
        public string GetUserId() => "test-user";
        public string? TryGetUserId() => "test-user";
        public string? GetDisplayName() => null;
        public bool IsAdmin() => false;
        public void SetUserIdOverride(string userId) { }
        public IDisposable BeginUserScope(string userId) => NoopUserScope.Instance;
    }

    private sealed class FakeTempData : ITempDataProvider
    {
        public IDictionary<string, object?> LoadTempData(HttpContext context) => new Dictionary<string, object?>(StringComparer.Ordinal);
        public void SaveTempData(HttpContext context, IDictionary<string, object?> values) { }
    }
}
