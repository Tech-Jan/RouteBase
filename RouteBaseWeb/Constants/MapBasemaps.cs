namespace RouteBaseWeb.Constants;

/// <summary>
/// Server-side catalog of selectable basemaps for the Default basemap profile setting.
/// Single source for both the &lt;option&gt; list (ProfileSettings.cshtml) and the save-time
/// whitelist (ProfileSettings.cshtml.cs) — previously two hand-maintained lists that drifted
/// (the whitelist still had only streets/satellite/terrain, so picking any other basemap
/// silently reset to the default on save).
///
/// The ids MUST stay in sync with the TS registry BASEMAPS in
/// src/js/core/basemap-registry.ts (that file owns the style URLs; this one is the id contract
/// the server validates against).
/// </summary>
public static class MapBasemaps
{
    public readonly record struct Basemap(string Id, string Label, bool KeyRequired);

    /// <summary>Selectable basemaps, in display order. Mirrors BASEMAPS in basemap-registry.ts.</summary>
    public static readonly IReadOnlyList<Basemap> Catalog = new[]
    {
        new Basemap("liberty",        "Liberty",      KeyRequired: false),
        new Basemap("bright",         "Bright",       KeyRequired: false),
        new Basemap("positron",       "Muted",        KeyRequired: false),
        new Basemap("osm-raster",     "OSM",          KeyRequired: false),
        new Basemap("satellite-esri", "Satellite",    KeyRequired: false),
        new Basemap("streets",        "Streets (MT api)",    KeyRequired: true),
        new Basemap("satellite",      "Satellite (MT api)",  KeyRequired: true),
        new Basemap("terrain",        "3D Terrain (MT api)", KeyRequired: true),
    };

    /// <summary>Valid basemap ids — used to whitelist the posted Default basemap value.</summary>
    public static readonly IReadOnlySet<string> ValidIds =
        Catalog.Select(b => b.Id).ToHashSet(StringComparer.Ordinal);
}
