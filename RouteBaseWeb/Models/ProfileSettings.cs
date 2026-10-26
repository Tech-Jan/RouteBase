using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace RouteBaseWeb.Models;

/// <summary>
/// One stored preference for one user: (Category, SettingKey, UserId) → SettingValue.
///
/// Every row is user-scoped. There are no global rows: the seed that wrote them was deleted at step 1a
/// because it was a second copy of <see cref="Constants.ProfileSettingsDefaults"/> whose only job was to
/// stay identical to the first — which is exactly how the stored HillshadeExaggeration drifted from the
/// const. Defaults are applied on READ (MapSettings/ChartSettings.LoadAsync), never stored.
///
/// The three former self-description columns (DataType, Description, DefaultValue) are gone: all three
/// were write-only. <c>ConvertValue&lt;T&gt;</c> keys off <c>typeof(T)</c> at the call site and never read
/// the DataType tag, and nothing ever read Description or DefaultValue back out.
/// </summary>
[Table("ProfileSettings")]
public class ProfileSettings
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// Setting category (e.g., "Charts", "Map", "Performance")
    /// </summary>
    [Required]
    [MaxLength(50)]
    public string Category { get; set; } = string.Empty;

    /// <summary>
    /// Setting key (e.g., "SpeedSmoothingWindow", "ElevationSmoothingWindow")
    /// </summary>
    [Required]
    [MaxLength(100)]
    public string SettingKey { get; set; } = string.Empty;

    /// <summary>
    /// Setting value stored as string (convert as needed).
    ///
    /// 2000, raised from 500 for saved race-import mappings: a mapping with ~10 checkpoints serialises to
    /// 400–700 characters of JSON, and a Ski to Sea one is longer still. SQLite needs no migration (TEXT
    /// is TEXT regardless of the declared length) and DbSchemaManagerTests asserts column NAMES only, so
    /// this is a validation bound rather than a storage change.
    /// </summary>
    [Required]
    [MaxLength(2000)]
    public string SettingValue { get; set; } = string.Empty;

    /// <summary>
    /// When the setting was created
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// When the setting was last updated
    /// </summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// Owner of the setting. NOT NULL — SQLite treats NULLs as DISTINCT in a unique index, so while this
    /// was nullable the (Category, SettingKey, UserId) index did not actually constrain global rows and
    /// duplicates had to be deduped in code on every boot. Non-null makes the index do its job.
    /// </summary>
    [Required]
    [MaxLength(100)]
    public string UserId { get; set; } = string.Empty;
}

/// <summary>
/// Predefined setting keys for type-safe access
/// </summary>
public static class SettingKeys
{
    // Chart Smoothing Settings
    // Speed is smoothed over a window of SECONDS, not of GPS points: a point count is a different
    // physical window on every recording device. The old "SpeedSmoothingWindow" row is not migrated —
    // reinterpreting a device-dependent sample count as seconds would be a guess.
    public const string SpeedSmoothingSeconds = "SpeedSmoothingSeconds";
    // Bins across the VISIBLE range of the speed chart, so the detail stays constant as you zoom.
    public const string SpeedBinCount = "SpeedBinCount";
    // Which view a speed chart opens on before the viewer has switched it on this device.
    public const string DefaultSpeedView = "DefaultSpeedView";
    public const string ElevationSmoothingWindow = "ElevationSmoothingWindow";
    
    // Chart Display Settings
    public const string ChartTension = "ChartTension";
    public const string ChartBorderWidth = "ChartBorderWidth";
    // Segment chart point detail (low/normal/high/full) — budget multiplier for server-side downsampling.
    public const string ChartDetail = "ChartDetail";
    
    // Map Settings
    public const string DefaultMapZoom = "DefaultMapZoom";
    public const string DefaultMapStyle = "DefaultMapStyle";
    public const string HillshadeExaggeration = "HillshadeExaggeration";
    // 3D mesh relief, distinct from HillshadeExaggeration's shading strength.
    public const string TerrainExaggeration = "TerrainExaggeration";
    public const string FogDistance = "FogDistance";
    
    // Performance Settings
    // Rider + bike mass (kg) used to estimate effort cost when no power meter recorded the effort.
    public const string RiderWeightKg = "RiderWeightKg";
    
    // UI Preferences
    public const string DefaultColorMode = "DefaultColorMode";
    public const string ShowDebugInfo = "ShowDebugInfo";
    
    // Splits Settings
    public const string SplitDistanceRunWalk = "SplitDistanceRunWalk";
    public const string SplitDistanceOther = "SplitDistanceOther";

    // Segment Settings
    // Default match threshold (%) used when analyzing a single segment and by "Analyze All".
    public const string DefaultMatchThreshold = "DefaultMatchThreshold";

    // Recording Settings
    // Release auto-detected rides at finalize instead of parking them in the review queue.
    public const string AutoApproveRecordings = "AutoApproveRecordings";

    // Privacy Settings
    public const string DefaultVisibility = "DefaultVisibility";
}

/// <summary>
/// Setting categories for organization
/// </summary>
public static class SettingCategories
{
    public const string Charts = "Charts";
    public const string Map = "Map";
    public const string Performance = "Performance";
    public const string UI = "UI";
    public const string Splits = "Splits";
    public const string Privacy = "Privacy";
    public const string Segments = "Segments";
    public const string Recording = "Recording";

    /// <summary>
    /// Saved results-CSV column mappings — see <c>Services/Races/RaceImportProfileStore.cs</c>.
    ///
    /// The one category whose keys are DATA rather than catalog entries: the user names them, and there
    /// are as many as they save. ProfileSettings tolerates that by design (it is a generic per-user KV
    /// store), which is what made a new table unnecessary.
    /// </summary>
    public const string RaceImport = "RaceImport";
}
