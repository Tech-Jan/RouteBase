using RouteBaseWeb.Models;
using RouteBaseWeb.Services;

namespace RouteBaseWeb.Constants;

/// <summary>
/// Single source of truth for ProfileSettings default values. Previously each default lived in
/// three drifting copies (property initializers, load defaults, reset literals) in
/// ProfileSettings.cshtml.cs — HillshadeExaggeration had already diverged. Consolidated here (PS-03).
/// </summary>
public static class ProfileSettingsDefaults
{
    // Chart Settings
    // Seconds of riding averaged into each drawn speed point, and the width of the min/max band the
    // chart's "Full" mode draws. 30 s reads as a speed rather than as terrain noise while still
    // showing a sprint; the ceiling goes to 5 minutes for anyone who wants the shape of the ride
    // rather than its detail. Zero means no smoothing.
    public const int SpeedSmoothingSeconds = 30;
    // How many bins the speed chart quantises the smoothed line into, across whatever range is on
    // screen. Higher = finer detail and a closer trace; lower = broader, more readable levels.
    public const int SpeedBinCount = 250;
    // The view a speed chart opens on. Values are the client SeriesMode ids so no mapping is needed.
    public const string DefaultSpeedView = SpeedViews.Smoothed;
    public const int ElevationSmoothingWindow = 7;
    public const decimal ChartTension = 0.2m;
    public const int ChartBorderWidth = 2;
    // Segment chart point detail (auto scales by effort count, never full; full = every point).
    public const string ChartDetail = ChartDetailLevels.Auto;

    // Map Settings
    public const int DefaultMapZoom = 13;
    // Keyless on purpose (decided 2026-08-02): the free Esri satellite raster. "streets" was
    // keyRequired, so on a share page or any page without a MapTiler key it never resolved and
    // defaultBasemapId() degraded to liberty anyway — the shipped default named a value it could not
    // render. A keyless id makes the setting honest and survives the MapTiler removal without a second
    // edit. MUST stay an id in MapBasemaps.ValidIds ('liberty' remains the registry's hard fallback).
    public const string DefaultMapStyle = "satellite-esri";
    // 0.2 per user decision M3b (BasemapsTerrain BT-08); was 0.1 (US-04, Backend-CoreServices).
    public const decimal HillshadeExaggeration = 0.2m;
    // 3D vertical relief. NOT the same knob as HillshadeExaggeration: this scales the terrain MESH, that
    // one scales the shading drawn on it. 1.0 = true-to-life. Was two hardcoded literals — Activity Details
    // rendered at 1.5 while every other map used 1.0, so the same hill looked different page to page
    // (decided 2026-08-02: unify on 1.0 and let the user choose).
    public const decimal TerrainExaggeration = 1.0m;
    public const int FogDistance = 10;

    // Performance Settings
    // Rider + bike mass (kg) for estimated efforts (no power meter). Was a private const inside
    // SegmentEffortPerformanceBuilder plus a seed literal, with nothing relating the two.
    public const decimal RiderWeightKg = 75m;

    // Privacy Settings
    // What a NEW SEGMENT is created with (SegmentCrud). Deliberately not "new activities and segments"
    // as the key's old comment claimed: activities take visibility from the recorder request or an
    // explicit edit, so the UI label says "new segments" — what the code actually does.
    public const string DefaultVisibility = Visibilities.Default;

    // UI Settings
    public const string DefaultColorMode = ColorModes.None;
    public const bool ShowDebugInfo = false;

    // Splits Settings
    public const decimal SplitDistanceRunWalk = 1.0m;
    public const decimal SplitDistanceOther = 4.0m;

    // Segment Settings
    public const int DefaultMatchThreshold = SegmentAnalysis.DefaultMatchThresholdPercent;

    // Recording Settings
    // Auto-detected rides skip the review queue and go straight into circulation. On by default: the
    // adjudicator's floors already discard the false triggers this queue existed to catch, so what was
    // left to approve was almost always a real ride. Turning it off restores the Record page review queue.
    public const bool AutoApproveRecordings = true;

    // Photo Settings — preview rendition max edge (px). Single source of truth (§1a); the import UI
    // offers a clamped choice from {640, 1024, 1600}. Changing this affects only NEW imports (the
    // original is discarded, so stored sizes are fixed at import time).
    public const int PhotoPreviewMaxEdge = 1024;
    public static readonly int[] PhotoPreviewMaxEdgeAllowed = { 640, 1024, 1600 };

    // Per-file upload cap. Import reads each original fully into RAM (plus a transient SkiaSharp copy) to
    // decode, so one oversized file is the only unbounded memory spike — reject it at the upload seam.
    public const long PhotoMaxFileBytes = 50L * 1024 * 1024;
}

/// <summary>
/// The speed chart's two views. These strings ARE the client's SeriesMode ids (core/chart-series-modes),
/// so the setting round-trips to the browser without a mapping table to keep in step.
/// </summary>
public static class SpeedViews
{
    public const string Recorded = "recorded";
    public const string Smoothed = "smoothBin";
}

/// <summary>The ChartDetail setting's closed value set (D2) — validated fail-back to Auto.</summary>
public static class ChartDetailLevels
{
    public const string Auto   = "auto";
    public const string Low    = "low";
    public const string Normal = "normal";
    public const string High   = "high";
    public const string Full   = "full";

    public static readonly string[] All = [Auto, Low, Normal, High, Full];
}
