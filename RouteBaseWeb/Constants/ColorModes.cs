namespace RouteBaseWeb.Constants;

/// <summary>
/// The DefaultColorMode setting's closed value set — how a GPS track is coloured.
///
/// Was a bare <c>string[] allowedColorModes = ["none", "speed", …]</c> inside
/// <c>ProfileSettings.PersistAllAsync</c>, restating the <c>&lt;option&gt;</c> list two files away with
/// nothing relating them. Same shape as <see cref="ChartDetailLevels"/> and <see cref="MapBasemaps"/>:
/// the closed set lives once, and <see cref="SettingsCatalog"/> is what the form and the save path both
/// read it through.
///
/// The client has its own notion of the pre-init state (<c>map-dispatcher.ts</c>'s literal
/// <c>'none'</c>). Typing that against a GENERATED union is a separate change — it needs a wire contract
/// carrying the value, which no DTO does today.
/// </summary>
public static class ColorModes
{
    public const string None      = "none";
    public const string Speed     = "speed";
    public const string HeartRate = "heartrate";
    public const string Elevation = "elevation";
    public const string Cadence   = "cadence";

    public static readonly string[] All = [None, Speed, HeartRate, Elevation, Cadence];
}
