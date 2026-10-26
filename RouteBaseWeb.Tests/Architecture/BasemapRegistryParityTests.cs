using System.Text.RegularExpressions;
using RouteBaseWeb.Constants;
using Xunit;

namespace RouteBaseWeb.Tests.Architecture;

/// <summary>
/// Gate (a). <see cref="MapBasemaps.Catalog"/> is a C# copy of the frontend's <c>BASEMAPS</c> registry.
/// MapBasemaps.cs says the two "MUST stay in sync" — this is the thing that makes that true.
///
/// A one-sided edit is silent and lossy in BOTH directions:
///   • delete an id from C# only → <see cref="MapBasemaps.ValidIds"/> rejects the posted value and the
///     save silently resets a user's DefaultMapStyle to the default. That is exactly the bug the C# side
///     was introduced to fix, one layer up.
///   • flip <c>keyRequired</c> on one side only → the server offers an <c>&lt;option&gt;</c> the client
///     cannot resolve (or hides one it could), so the basemap picker and the renderer disagree about what
///     a keyless page can show.
/// Directly relevant to the intended MapTiler removal, which is a multi-row delete across both files.
///
/// Modelled on <see cref="RouteBaseWeb.Tests.Services.Sharing.ShareCspHostParityTests"/> — same shape,
/// same reason (a browser-only or save-time break that no compiler and no other test can see), including
/// its vacuity guard: a parse that stopped matching would let the comparison pass while checking nothing.
///
/// Scope is ids + key-gating ONLY. Labels, style URLs and <c>kind</c> live in the TS registry by design
/// (it owns the URLs); C# owns the id contract it validates against. Asserting labels here would gate a
/// display string, which is not a contract.
/// </summary>
public class BasemapRegistryParityTests
{
    private const string RegistryRelativePath = "src/js/core/basemap-registry.ts";

    private sealed record TsBasemap(string Id, bool KeyRequired);

    /// <summary>Walks up from the test binary to the web project, so the working directory does not matter.</summary>
    private static string WebRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "RouteBaseWeb");
            if (Directory.Exists(Path.Combine(candidate, "src", "js"))) return candidate;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the RouteBaseWeb project from " + AppContext.BaseDirectory);
    }

    /// <summary>
    /// The <c>BASEMAPS</c> array literal, in source order. Sliced to the array first so an unrelated object
    /// literal elsewhere in the file cannot contribute a phantom entry.
    ///
    /// Entries are delimited by their own <c>id:</c>, NOT by braces: the MapTiler style URLs embed a
    /// <c>{key}</c> placeholder, so a brace-matching scan splits precisely the key-gated rows in half and
    /// reports them as unnamed keyless entries — the two rows this gate exists to watch. Anchoring on
    /// <c>id:</c> is also indifferent to whether an entry is written on one line or several.
    /// </summary>
    private static string ArrayText()
    {
        var path = Path.Combine(WebRoot(), RegistryRelativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"{RegistryRelativePath} has moved — update this test.");

        var source = File.ReadAllText(path);

        var start = source.IndexOf("export const BASEMAPS", StringComparison.Ordinal);
        Assert.True(start >= 0, $"`export const BASEMAPS` not found in {RegistryRelativePath} — this gate cannot parse it.");
        var end = source.IndexOf("];", start, StringComparison.Ordinal);
        Assert.True(end > start, $"Could not find the end of the BASEMAPS array in {RegistryRelativePath}.");

        return source[start..end];
    }

    private static IReadOnlyList<TsBasemap> ParseTsRegistry()
    {
        var array = ArrayText();
        var ids = Regex.Matches(array, @"\bid\s*:\s*'(?<id>[^']+)'", RegexOptions.None, RegexDefaults.MatchTimeout);

        return ids.Select((m, i) =>
        {
            var entryEnd = i + 1 < ids.Count ? ids[i + 1].Index : array.Length;
            var entry = array[m.Index..entryEnd];
            return new TsBasemap(m.Groups["id"].Value, Regex.IsMatch(entry, @"\bkeyRequired\s*:\s*true\b", RegexOptions.None, RegexDefaults.MatchTimeout));
        }).ToList();
    }

    [Fact]
    public void TsBasemapRegistry_MatchesTheCSharpCatalog_IdsInOrderAndKeyGating()
    {
        var ts = ParseTsRegistry().Select(b => $"{b.Id} keyRequired={b.KeyRequired}").ToArray();
        var cs = MapBasemaps.Catalog.Select(b => $"{b.Id} keyRequired={b.KeyRequired}").ToArray();

        Assert.True(
            ts.SequenceEqual(cs, StringComparer.Ordinal),
            "MapBasemaps.Catalog and BASEMAPS in " + RegistryRelativePath + " have drifted. They are one "
            + "contract in two files: C# validates the posted DefaultMapStyle against it and renders the "
            + "<option> list; TS resolves the style URL and hides key-gated entries. A one-sided delete "
            + "silently resets a saved DefaultMapStyle on the next save; a one-sided keyRequired flip makes "
            + "the picker and the renderer disagree.\n"
            + $"  TS ({RegistryRelativePath}):\n    " + string.Join("\n    ", ts) + "\n"
            + "  C# (Constants/MapBasemaps.cs):\n    " + string.Join("\n    ", cs));
    }

    /// <summary>
    /// Guards the guard: if the slice or the entry regex stopped matching, the test above would compare two
    /// empty (or truncated) lists and pass while allowing anything.
    /// </summary>
    [Fact]
    public void TheRegistryParse_ActuallyFindsEntries()
    {
        var ts = ParseTsRegistry();

        Assert.True(ts.Count >= 6, $"Parsed only {ts.Count} BASEMAPS entries — the parse is broken, so the parity check is vacuous.");
        Assert.All(ts, b => Assert.False(string.IsNullOrEmpty(b.Id), "Parsed a BASEMAPS entry with no id — the id regex is broken."));

        // One entry per styleUrl, cross-checked against a field the id anchor does not use. Catches
        // UNDER-parsing without naming any id, so removing MapTiler cannot break this guard.
        var styleUrls = Regex.Matches(ArrayText(), @"\bstyleUrl\s*:", RegexOptions.None, RegexDefaults.MatchTimeout).Count;
        Assert.True(ts.Count == styleUrls, $"Parsed {ts.Count} entries but the registry declares {styleUrls} styleUrls — the parse is dropping entries.");

        // Both halves of the flag must be observed, or a parse that never sees `keyRequired` would make
        // every entry look keyless and the comparison above would only be testing ids.
        Assert.Contains(ts, b => b.KeyRequired);
        Assert.Contains(ts, b => !b.KeyRequired);
    }
}
