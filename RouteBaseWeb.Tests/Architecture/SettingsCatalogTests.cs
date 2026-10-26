using System.Globalization;
using System.Reflection;
using RouteBaseWeb.Constants;
using RouteBaseWeb.Models;
using Xunit;

namespace RouteBaseWeb.Tests.Architecture;

/// <summary>
/// <see cref="SettingsCatalog"/> is the one place a setting's RULE lives — the range the form renders and
/// the clamp the server applies. These are the facts that make it worth having; if any of them stops
/// holding, the catalog is back to being a fourth copy of the rule rather than the only one.
///
/// The clamp cases here were MOVED from ProfileSettingsModelTests, deliberately and not because they
/// failed: clamping is no longer something the page does on save (it now reports a ModelState error and
/// re-renders), so testing it through the page model would pin behaviour that page no longer has. The
/// page keeps one test that bad input is REJECTED; the arithmetic lives here.
/// </summary>
public class SettingsCatalogTests
{
    // ── The rule must admit its own default ───────────────────────────────────────────────

    [Fact]
    public void EveryNumericDefault_SatisfiesItsOwnRange()
    {
        Assert.NotEmpty(SettingsCatalog.Numbers);
        foreach (var s in SettingsCatalog.Numbers)
        {
            Assert.True(
                s.InRange(s.Default),
                $"{s.Category}.{s.Key}: default {s.Default} is outside its own range "
                + $"[{s.Min}, {s.Max}] — the form would open on a value it refuses to save.");
        }
    }

    /// <summary>
    /// A default outside its own option list is exactly the 'streets' defect: the shipped value was not
    /// something the page could resolve, so every read silently degraded to something else.
    /// </summary>
    [Fact]
    public void EveryChoiceDefault_IsOneOfItsOwnOptions()
    {
        Assert.NotEmpty(SettingsCatalog.Choices);
        foreach (var s in SettingsCatalog.Choices)
        {
            Assert.True(
                s.IsAllowed(s.Default),
                $"{s.Category}.{s.Key}: default '{s.Default}' is not in its option list "
                + $"({string.Join(", ", s.Options.Select(o => o.Value))}).");
        }
    }

    // ── The closed sets must stay the closed sets ─────────────────────────────────────────

    [Fact]
    public void ChartDetailOptions_AreExactlyChartDetailLevels()
    {
        Assert.Equal(ChartDetailLevels.All, SettingsCatalog.ChartDetail.Options.Select(o => o.Value).ToArray());
    }

    [Fact]
    public void ColorModeOptions_AreExactlyColorModes()
    {
        Assert.Equal(ColorModes.All, SettingsCatalog.DefaultColorMode.Options.Select(o => o.Value).ToArray());
    }

    [Fact]
    public void BasemapOptions_AreExactlyMapBasemapsCatalog()
    {
        Assert.Equal(
            MapBasemaps.Catalog.Select(b => b.Id).ToArray(),
            SettingsCatalog.DefaultMapStyle.Options.Select(o => o.Value).ToArray());
    }

    [Fact]
    public void VisibilityOptions_AreAllValidVisibilities()
    {
        foreach (var option in SettingsCatalog.DefaultVisibility.Options)
            Assert.True(Visibilities.IsValid(option.Value), $"'{option.Value}' is not a known visibility");
        Assert.Equal(3, SettingsCatalog.DefaultVisibility.Options.Count);
    }

    // ── Keys are real, and no two entries claim the same row ──────────────────────────────

    [Fact]
    public void EveryKey_IsASettingKeysConstant()
    {
        var known = typeof(SettingKeys)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral)
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var (category, key) in AllEntries())
            Assert.True(known.Contains(key), $"{category}.{key} is not a SettingKeys constant — a typo here "
                + "writes a row nothing ever reads back.");
    }

    [Fact]
    public void NoTwoEntries_ClaimTheSameCategoryAndKey()
    {
        var all = AllEntries().ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
    }

    // ── Clamp / coerce arithmetic (moved here from ProfileSettingsModelTests) ─────────────

    [Fact]
    public void Clamp_PullsOutOfRangeValuesToTheNearestBound()
    {
        Assert.Equal(300, SettingsCatalog.SpeedSmoothingSeconds.Clamp(9999));
        // 0 is IN range for a seconds window — it is the honest spelling of "no smoothing".
        Assert.Equal(0, SettingsCatalog.SpeedSmoothingSeconds.Clamp(-5));
        Assert.Equal(1.0m, SettingsCatalog.ChartTension.Clamp(5.0m));
        Assert.Equal(1, SettingsCatalog.DefaultMapZoom.Clamp(0));
        Assert.Equal(19, SettingsCatalog.DefaultMapZoom.Clamp(99));
    }

    /// <summary>
    /// 0 is a legal FogDistance ("disabled") and a legal HillshadeExaggeration ("no shading"). Both are
    /// the low bound, so a clamp that treated 0 as missing would erase a deliberate choice — the same
    /// `|| default` mistake the client normaliser is written longhand to avoid.
    /// </summary>
    [Fact]
    public void Clamp_LeavesALegitimateZeroAlone()
    {
        Assert.Equal(0, SettingsCatalog.FogDistance.Clamp(0));
        Assert.Equal(0.0m, SettingsCatalog.HillshadeExaggeration.Clamp(0.0m));
    }

    [Fact]
    public void Coerce_RejectsAValueOutsideTheClosedSet()
    {
        Assert.Equal(ProfileSettingsDefaults.DefaultMapStyle, SettingsCatalog.DefaultMapStyle.Coerce("bogus"));
        Assert.Equal(ProfileSettingsDefaults.DefaultColorMode, SettingsCatalog.DefaultColorMode.Coerce("nonsense"));
        Assert.Equal(ProfileSettingsDefaults.ChartDetail, SettingsCatalog.ChartDetail.Coerce(null));
    }

    [Fact]
    public void Coerce_KeepsEveryValueTheFormCanOffer()
    {
        foreach (var s in SettingsCatalog.Choices)
            foreach (var option in s.Options)
                Assert.Equal(option.Value, s.Coerce(option.Value));
    }

    // ── The HTML attributes are culture-proof ─────────────────────────────────────────────

    /// <summary>
    /// Razor writes these into <c>min="…"</c>/<c>step="…"</c>. Under a comma-decimal culture a plain
    /// ToString() renders "0,1", which a browser drops on the floor — the constraint would silently stop
    /// existing on exactly the servers where the settings island once emitted "0,2" and broke the page.
    /// </summary>
    [Fact]
    public void RangeAttributes_AreInvariantUnderACommaDecimalCulture()
    {
        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        try
        {
            Assert.Equal("0.1", SettingsCatalog.ChartTension.StepAttr);
            Assert.Equal("1", SettingsCatalog.ChartTension.MaxAttr);
            Assert.Equal("0.1", SettingsCatalog.SplitDistanceRunWalk.MinAttr);
            Assert.Equal("0.5", SettingsCatalog.RiderWeightKg.StepAttr);
            Assert.DoesNotContain(",", SettingsCatalog.HillshadeExaggeration.StepAttr, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    private static IEnumerable<(string Category, string Key)> AllEntries() =>
        SettingsCatalog.Numbers.Select(s => (s.Category, s.Key))
            .Concat(SettingsCatalog.Choices.Select(s => (s.Category, s.Key)));
}
