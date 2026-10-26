namespace RouteBaseWeb.Constants;

/// <summary>
/// Single source of truth for all third-party CDN library URLs and versions.
/// Referenced by Razor pages/partials so version upgrades only touch this file.
/// </summary>
public static class CdnLibraries
{
    // --- MapLibre GL JS ---
    // Keep version in sync with package.json devDependency "maplibre-gl" (types only)
    public const string MapLibreVersion = "5.19.0";
    public const string MapLibreCss     = $"https://unpkg.com/maplibre-gl@{MapLibreVersion}/dist/maplibre-gl.css";
    public const string MapLibreCssSri  = "sha384-MGCxhspF/+ufueUgol3FDkiAYQbpSNRhBT0VWHJt64U8qIy9qlnXWx8LAbj6niPH";
    public const string MapLibreJs      = $"https://unpkg.com/maplibre-gl@{MapLibreVersion}/dist/maplibre-gl.js";
    public const string MapLibreJsSri   = "sha384-pEfbADcwebVj4NNOvWFLUkm+FiGTICE5bChpV647czG7OpSqcHNgxM8QawfAkbRO";

    // --- Chart.js + plugins ---
    public const string ChartJsVersion        = "4.4.0";
    public const string ChartJs               = $"https://cdn.jsdelivr.net/npm/chart.js@{ChartJsVersion}/dist/chart.umd.min.js";
    public const string ChartJsSri            = "sha384-e6nUZLBkQ86NJ6TVVKAeSaK8jWa3NhkYWZFomE39AvDbQWeie9PlQqM3pmYW5d1g";
    public const string ChartJsZoomVersion    = "2.0.1";
    public const string ChartJsZoom           = $"https://cdn.jsdelivr.net/npm/chartjs-plugin-zoom@{ChartJsZoomVersion}/dist/chartjs-plugin-zoom.min.js";
    public const string ChartJsZoomSri        = "sha384-zPzbVRXfR492Sd5D+HydTYCxxgHAfgVO8KERbLlpeH5unsmbAEXrscGUUqLZG9BM";
    public const string ChartJsDateFnsVersion = "3.0.0";
    public const string ChartJsDateFns        = $"https://cdn.jsdelivr.net/npm/chartjs-adapter-date-fns@{ChartJsDateFnsVersion}/dist/chartjs-adapter-date-fns.bundle.min.js";
    public const string ChartJsDateFnsSri     = "sha384-cVMg8E3QFwTvGCDuK+ET4PD341jF3W8nO1auiXfuZNQkzbUUiBGLsIQUE+b1mxws";

    // --- Bootstrap Icons (loaded by _DashboardLayout via the "bootstrap-icons-css" key) ---
    // SRI covers the CSS only; the font files it @font-face-references are not script and aren't hashed.
    public const string BootstrapIconsVersion = "1.11.0";
    public const string BootstrapIconsCss     = $"https://cdn.jsdelivr.net/npm/bootstrap-icons@{BootstrapIconsVersion}/font/bootstrap-icons.css";
    public const string BootstrapIconsCssSri  = "sha384-QuGBSgV5Im3DzL2z+8Ko9/hqNy/N0O7zwvXAtfd1MvPKWa/UbeLV65cfm4BV5Wgq";

    // --- @microsoft/signalr (live recording) ---
    // Matches ASP.NET Core 9 server-side SignalR.
    // Exposes window.signalR on the page.
    public const string SignalRVersion = "8.0.7";
    public const string SignalRJs      = $"https://cdn.jsdelivr.net/npm/@microsoft/signalr@{SignalRVersion}/dist/browser/signalr.min.js";
    public const string SignalRJsSri   = "sha384-mU1xC5yC2LldSW74Rj1Ax8wPiLw/28V5eh51uKJMlBbRVsOtUYd4xyzNsgIAJARB";
}
