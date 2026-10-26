using System.Text.RegularExpressions;
using RouteBaseWeb.Constants;

namespace RouteBaseWeb.Services.Races;

/// <summary>
/// The one place a race colour is validated.
///
/// Race colours are not decoration passing through a template — they are INTERPOLATED INTO CODE at both
/// ends. A participant's colour becomes a GeoJSON feature property read by a MapLibre
/// <c>['get','color']</c> paint expression; a leg's colour is written into a CSS swatch and a canvas
/// fill. Free text there is an injection surface, so the wire only ever carries <c>#rrggbb</c>.
///
/// Null is always legal and means "no colour" — the default (real = yellow, simulated = blue) applies.
/// </summary>
public static partial class RaceColors
{
    [GeneratedRegex("^#[0-9a-fA-F]{6}$", RegexOptions.None, RegexDefaults.MatchTimeoutMillis)]
    private static partial Regex HexColor();

    /// <summary>True for null (no colour) or a well-formed <c>#rrggbb</c>. Everything else is rejected.</summary>
    public static bool IsValid(string? color) =>
        string.IsNullOrEmpty(color) || HexColor().IsMatch(color);

    /// <summary>Null, or the lower-cased <c>#rrggbb</c>. Throws on anything else — callers validate first.</summary>
#pragma warning disable CA1308 // canonical wire form of a colour is lowercase #rrggbb
    public static string? Normalize(string? color) =>
        string.IsNullOrEmpty(color)
            ? null
            : IsValid(color)
                ? color.ToLowerInvariant()
                : throw new ArgumentException($"Not a #rrggbb colour: '{color}'.", nameof(color));
#pragma warning restore CA1308

    /// <summary>
    /// The colours real riders are handed as they are added, in order.
    ///
    /// Four hues, not eight: every purple/violet candidate collides with the SIMULATED field's blue
    /// (#4dabf7) under protanopia — and a real rider indistinguishable from the 424 riders he is being
    /// compared against is the failure this palette exists to prevent. These four separate from each
    /// other and from that blue under deuteran, protan and tritan simulation (worst pair ΔE 11.9),
    /// clear 3:1 against the dark map, and are deliberately BRIGHT: they are 7px dots over aerial
    /// imagery as much as they are chart lines.
    ///
    /// Gold stays first so a race with one real rider looks exactly as it did before this existed.
    /// </summary>
    public static readonly IReadOnlyList<string> RealRiderPalette = ["#ffd43b", "#f06595", "#63e6be", "#ff922b"];

    /// <summary>What an unpainted real rider wears. Mirrored by <c>REAL_COLOR</c> in race-build.ts.</summary>
    public const string DefaultRealColor = "#ffd43b";

    /// <summary>
    /// The next palette colour for a rider joining a race whose participants already wear
    /// <paramref name="used"/> — the first one nobody has, else the first, cycling.
    ///
    /// Compared against EVERY participant's colour, not just the real ones: a painted division and a
    /// real rider in the same pink is the same collision seen from the other side. Nulls are ignored;
    /// they mean "the default", which for a simulated rider is the blue no palette entry can be.
    /// </summary>
    public static string NextRealColor(IEnumerable<string?> used)
    {
        var taken = used
            .Where(c => IsValid(c))
            .Select(Normalize)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);

        foreach (var color in RealRiderPalette)
        {
            if (!taken.Contains(color)) return color;
        }

        // Every colour is spoken for — a 5th real rider. Cycling repeats a colour, which the user can
        // repaint; refusing to add him, or inventing a hue outside the validated set, would be worse.
        return RealRiderPalette[taken.Count % RealRiderPalette.Count];
    }
}
