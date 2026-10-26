using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using RouteBaseWeb.Models;
using RouteBaseWeb.Services;

namespace RouteBaseWeb.Constants;

/// <summary>One selectable value of a <see cref="ChoiceSetting"/>: the stored id and its form label.</summary>
public sealed record SettingOption(string Value, string Label);

/// <summary>
/// The bounds themselves, grouped rather than spread across the parent's parameter list — four
/// consecutive <c>decimal</c> parameters is a positional-argument trap, and BackendConventionTests
/// rejects it (it fired on the first version of this file).
/// </summary>
public sealed record SettingRange(decimal Min, decimal Max, decimal Step);

/// <summary>
/// A setting whose rule is a RANGE. Owns both halves of that rule — the HTML the form renders and the
/// clamp the server applies — so they cannot say different things.
/// </summary>
public sealed record NumericSetting(string Category, string Key, decimal Default, SettingRange Range)
{
    public decimal Min => Range.Min;
    public decimal Max => Range.Max;
    public decimal Step => Range.Step;

    public decimal Clamp(decimal value) => Math.Clamp(value, Min, Max);
    public int Clamp(int value) => (int)Math.Clamp(value, Min, Max);
    public bool InRange(decimal value) => value >= Min && value <= Max;

    /// <summary>
    /// InvariantCulture, and this is load-bearing rather than tidy: Razor writes these straight into
    /// <c>min="…"</c>/<c>step="…"</c>, and on a comma-decimal server culture <c>0.1m.ToString()</c>
    /// renders <c>0,1</c> — an attribute value the browser silently discards, so the constraint would
    /// just stop existing. Same class of bug as the settings island's <c>"0,2"</c> tension.
    /// </summary>
    public string MinAttr => Text(Min);
    public string MaxAttr => Text(Max);
    public string StepAttr => Text(Step);

    /// <summary>"1–301" for a hint line, so the prose cannot drift from the constraint either.</summary>
    public string RangeText => $"{Text(Min)}–{Text(Max)}";
    public string DefaultText => Text(Default);

    private static string Text(decimal d) => d.ToString("0.####", CultureInfo.InvariantCulture);
}

/// <summary>
/// A setting whose rule is a CLOSED SET. The form renders one <c>&lt;option&gt;</c> per entry and the save
/// path coerces through the same list, which is what makes a forged POST value unable to persist.
/// </summary>
public sealed record ChoiceSetting(
    string Category, string Key, string Default, IReadOnlyList<SettingOption> Options)
{
    // [NotNullWhen] rather than a postfix `!` in Coerce: the null-safe pattern Visibilities.IsValid
    // already uses, and Constants is pinned at zero null-forgiving operators.
    public bool IsAllowed([NotNullWhen(true)] string? value) =>
        value is not null && Options.Any(o => string.Equals(o.Value, value, StringComparison.Ordinal));

    public string Coerce(string? value) => IsAllowed(value) ? value : Default;
}

/// <summary>
/// Every ACCOUNT setting's rule, in one place: its category, its key, its default BY REFERENCE to
/// <see cref="ProfileSettingsDefaults"/> (never a literal — that is the whole PS-03 point), and either a
/// numeric range or a closed set.
///
/// Why this exists: the rule used to live in three unrelated copies — the <c>min</c>/<c>max</c>/<c>step</c>
/// attributes in ProfileSettings.cshtml, the <c>Math.Clamp</c> calls in PersistAllAsync, and the prose in
/// each hint line. Nothing related them, so the HTML could permit what the server clamped and the hint
/// could describe neither. The form and the save path now read the same entry.
///
/// Validation is a POLICY, not a save-time side effect:
///   • the form renders its constraint from here, so the browser rejects out-of-range first;
///   • the POST adds a ModelState error instead of silently clamping the user's number to something
///     they did not type;
///   • the READ path clamps (ChartSettings/MapSettings.LoadAsync, AppSettingsProvider), so a row
///     hand-edited in SQLite cannot ship a broken value to a page.
///
/// BOOLEANS ARE NOT HERE. A checkbox has no rule to state and nothing to drift — adding entries whose
/// only content is "it is a bool" would make this list look complete while saying nothing. The set of
/// PERSISTED settings is <c>ProfileSettingsModel.PersistAllAsync</c>; this is the set of settings with a
/// RULE.
/// </summary>
public static class SettingsCatalog
{
    // ── Charts ────────────────────────────────────────────────────────────────────────────
    // Seconds, not GPS points: the same point count is a different physical window on every device.
    // 0 is a real value — "no smoothing" — so the range starts there rather than at 1.
    public static readonly NumericSetting SpeedSmoothingSeconds = new(
        SettingCategories.Charts, SettingKeys.SpeedSmoothingSeconds,
        ProfileSettingsDefaults.SpeedSmoothingSeconds, new(Min: 0, Max: 300, Step: 1));

    // Bins across the visible range. Below ~20 the trace stops describing the ride; above the sample
    // count it silently degrades to the samples themselves, which is the honest ceiling.
    public static readonly NumericSetting SpeedBinCount = new(
        SettingCategories.Charts, SettingKeys.SpeedBinCount,
        ProfileSettingsDefaults.SpeedBinCount, new(Min: 20, Max: 2000, Step: 10));

    // The per-chart button is a DEVICE preference (it is "how I am looking at this right now"); this is
    // the account-level answer to "what should it open on before I have touched it".
    public static readonly ChoiceSetting DefaultSpeedView = new(
        SettingCategories.Charts, SettingKeys.DefaultSpeedView,
        ProfileSettingsDefaults.DefaultSpeedView,
        [
            new(SpeedViews.Smoothed, "Smoothed"),
            new(SpeedViews.Recorded, "Recorded"),
        ]);

    public static readonly NumericSetting ElevationSmoothingWindow = new(
        SettingCategories.Charts, SettingKeys.ElevationSmoothingWindow,
        ProfileSettingsDefaults.ElevationSmoothingWindow, new(Min: 1, Max: 301, Step: 1));

    public static readonly NumericSetting ChartTension = new(
        SettingCategories.Charts, SettingKeys.ChartTension,
        ProfileSettingsDefaults.ChartTension, new(Min: 0.0m, Max: 1.0m, Step: 0.1m));

    public static readonly NumericSetting ChartBorderWidth = new(
        SettingCategories.Charts, SettingKeys.ChartBorderWidth,
        ProfileSettingsDefaults.ChartBorderWidth, new(Min: 1, Max: 5, Step: 1));

    public static readonly ChoiceSetting ChartDetail = new(
        SettingCategories.Charts, SettingKeys.ChartDetail, ProfileSettingsDefaults.ChartDetail,
        [
            new(ChartDetailLevels.Auto,   "Auto"),
            new(ChartDetailLevels.Low,    "Low"),
            new(ChartDetailLevels.Normal, "Normal"),
            new(ChartDetailLevels.High,   "High"),
            new(ChartDetailLevels.Full,   "Full"),
        ]);

    // ── Splits ────────────────────────────────────────────────────────────────────────────
    public static readonly NumericSetting SplitDistanceRunWalk = new(
        SettingCategories.Splits, SettingKeys.SplitDistanceRunWalk,
        ProfileSettingsDefaults.SplitDistanceRunWalk, new(Min: 0.1m, Max: 10.0m, Step: 0.1m));

    public static readonly NumericSetting SplitDistanceOther = new(
        SettingCategories.Splits, SettingKeys.SplitDistanceOther,
        ProfileSettingsDefaults.SplitDistanceOther, new(Min: 0.1m, Max: 20.0m, Step: 0.1m));

    // ── Map ───────────────────────────────────────────────────────────────────────────────
    public static readonly NumericSetting DefaultMapZoom = new(
        SettingCategories.Map, SettingKeys.DefaultMapZoom,
        ProfileSettingsDefaults.DefaultMapZoom, new(Min: 1, Max: 19, Step: 1));

    public static readonly NumericSetting HillshadeExaggeration = new(
        SettingCategories.Map, SettingKeys.HillshadeExaggeration,
        ProfileSettingsDefaults.HillshadeExaggeration, new(Min: 0.0m, Max: 1.0m, Step: 0.1m));

    /// <summary>
    /// Min is 0.1, not 0, and that is deliberate. 0 would flatten the mesh entirely, which reads as "3D
    /// off" but is not — the DEM source and the TerrainControl toggle stay live, so the control would be
    /// present and do nothing visible. It would also be the one map value a <c>|| default</c> fallback
    /// silently rewrites; keeping 0 unrepresentable means the client's terrain fallbacks cannot misfire.
    /// </summary>
    public static readonly NumericSetting TerrainExaggeration = new(
        SettingCategories.Map, SettingKeys.TerrainExaggeration,
        ProfileSettingsDefaults.TerrainExaggeration, new(Min: 0.1m, Max: 3.0m, Step: 0.1m));

    public static readonly NumericSetting FogDistance = new(
        SettingCategories.Map, SettingKeys.FogDistance,
        ProfileSettingsDefaults.FogDistance, new(Min: 0, Max: 20, Step: 1));

    /// <summary>
    /// Options come from <see cref="MapBasemaps.Catalog"/> — the same list the TS registry is pinned
    /// against by BasemapRegistryParityTests — so a basemap added on one side cannot appear here without
    /// the other. The key-required suffix rides the LABEL, not the id.
    /// </summary>
    public static readonly ChoiceSetting DefaultMapStyle = new(
        SettingCategories.Map, SettingKeys.DefaultMapStyle, ProfileSettingsDefaults.DefaultMapStyle,
        MapBasemaps.Catalog
            .Select(b => new SettingOption(b.Id, b.KeyRequired ? $"{b.Label} (requires MapTiler key)" : b.Label))
            .ToList());

    // ── Segments ──────────────────────────────────────────────────────────────────────────
    public static readonly NumericSetting DefaultMatchThreshold = new(
        SettingCategories.Segments, SettingKeys.DefaultMatchThreshold,
        ProfileSettingsDefaults.DefaultMatchThreshold,
        new(Min: SegmentAnalysis.MinMatchThresholdPercent,
            Max: SegmentAnalysis.MaxMatchThresholdPercent, Step: 1));

    // ── Performance ───────────────────────────────────────────────────────────────────────
    /// <summary>
    /// Rider + bike mass. Consumed SERVER-SIDE only (SegmentEffortPerformanceBuilder), so it is account
    /// data with a form but deliberately NOT a field of SettingsPayload — the payload reaches anonymous
    /// share pages, and a person's mass is not theirs to receive.
    /// </summary>
    public static readonly NumericSetting RiderWeightKg = new(
        SettingCategories.Performance, SettingKeys.RiderWeightKg,
        ProfileSettingsDefaults.RiderWeightKg, new(Min: 30m, Max: 250m, Step: 0.5m));

    // ── Privacy ───────────────────────────────────────────────────────────────────────────
    public static readonly ChoiceSetting DefaultVisibility = new(
        SettingCategories.Privacy, SettingKeys.DefaultVisibility, ProfileSettingsDefaults.DefaultVisibility,
        [
            new(Visibilities.Private, "Private — only me"),
            new(Visibilities.Friends, "Friends"),
            new(Visibilities.Public,  "Public"),
        ]);

    // ── UI ────────────────────────────────────────────────────────────────────────────────
    public static readonly ChoiceSetting DefaultColorMode = new(
        SettingCategories.UI, SettingKeys.DefaultColorMode, ProfileSettingsDefaults.DefaultColorMode,
        [
            new(ColorModes.None,      "Solid Red"),
            new(ColorModes.Speed,     "Speed Gradient"),
            new(ColorModes.HeartRate, "Heart Rate Gradient"),
            new(ColorModes.Elevation, "Elevation Gradient"),
            new(ColorModes.Cadence,   "Cadence Gradient"),
        ]);

    /// <summary>Every ranged setting — iterated by the catalog gate, not by the form.</summary>
    public static readonly IReadOnlyList<NumericSetting> Numbers =
    [
        SpeedSmoothingSeconds, SpeedBinCount, ElevationSmoothingWindow, ChartTension, ChartBorderWidth,
        SplitDistanceRunWalk, SplitDistanceOther,
        DefaultMapZoom, HillshadeExaggeration, TerrainExaggeration, FogDistance,
        DefaultMatchThreshold, RiderWeightKg,
    ];

    /// <summary>Every closed-set setting — iterated by the catalog gate, not by the form.</summary>
    public static readonly IReadOnlyList<ChoiceSetting> Choices =
    [
        ChartDetail, DefaultSpeedView, DefaultMapStyle, DefaultVisibility, DefaultColorMode,
    ];
}
