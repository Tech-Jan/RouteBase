using System.Text.Json;
using Xunit;

namespace RouteBaseWeb.Tests.Architecture;

/// <summary>
/// The cheap half of the mutation ratchet.
///
/// WHY A SPLIT GATE. Mutation testing is the only instrument that can tell a real test from one that
/// executes a line and asserts nothing — it breaks the code on purpose and checks whether anything
/// complains. But a full Stryker run over Services/Tracks + Services/Segments takes ~16 minutes, so it
/// cannot live in <c>dotnet test</c>. It runs opt-in via <c>scripts/gates.sh --mutation</c>, exactly like
/// the analyzer dragnet.
///
/// That leaves one failure mode the slow gate cannot catch on its own: the PIN FILE going stale. A pin
/// naming a file that has been renamed or deleted looks green forever while protecting nothing — the same
/// shape as the "exempt file renamed" case CSharpFileSizeRatchetTests guards. These tests are that guard,
/// and they cost milliseconds because they never run Stryker: they only check that every pin still points
/// at real code.
///
/// The scores themselves are pinned in <c>mutation-pins.json</c> and compared by the script.
/// Pins only ever RISE. Lowering one needs a written reason in the same commit.
/// </summary>
public class MutationRatchetPinTests
{
    private const string PinsFileName = "mutation-pins.json";

    /// <summary>Subtrees the pins are allowed to cover — must match MUTATE_GLOBS in the script.</summary>
    private static readonly string[] CoveredSubtrees =
    [
        "RouteBaseWeb/Services/Tracks/",
        "RouteBaseWeb/Services/Segments/",
    ];

    /// <summary>Walks up from the test binary to the repo root, so cwd cannot break this.</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "RouteBaseWeb", "src", "js"))) return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repo root from " + AppContext.BaseDirectory);
    }

    private static Dictionary<string, double> Pins()
    {
        var path = Path.Combine(RepoRoot(), "RouteBaseWeb.Tests", "Architecture", PinsFileName);
        Assert.True(File.Exists(path), $"{PinsFileName} is missing — the mutation ratchet has no pins to enforce.");

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        Assert.True(
            doc.RootElement.TryGetProperty("pins", out var pins),
            $"{PinsFileName} has no 'pins' object — the script would read zero pins and pass vacuously.");

        return pins.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetDouble(), StringComparer.Ordinal);
    }

    /// <summary>
    /// Guards the guard. A pin file that parsed but held nothing would make the slow gate green forever;
    /// the baseline measured 26 files with mutable logic, so anything near zero means the file was
    /// truncated or the update step wrote an empty set.
    /// </summary>
    [Fact]
    public void Pin_file_actually_holds_pins()
    {
        var pins = Pins();
        Assert.True(pins.Count >= 20, $"only {pins.Count} pins — the baseline had 26 files with valid mutants.");
    }

    /// <summary>
    /// The real point: a pin naming a file that no longer exists protects nothing and never fails.
    /// Renaming a source file must break this, so the pin is moved rather than silently abandoned.
    /// </summary>
    [Fact]
    public void Every_pinned_file_still_exists()
    {
        var root = RepoRoot();
        var missing = Pins().Keys
            .Where(rel => !File.Exists(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar))))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            missing.Count == 0,
            "pinned file(s) no longer exist — move the pin to the new path, or delete it and say why:\n  "
                + string.Join("\n  ", missing));
    }

    /// <summary>
    /// Pins must stay inside the subtrees the script actually mutates. A pin outside them can never be
    /// measured, so it would sit in the file looking like protection while the script reports it MISSING
    /// on every run — or worse, gets deleted to quieten the noise.
    /// </summary>
    [Fact]
    public void Pins_stay_within_the_mutated_subtrees()
    {
        var strays = Pins().Keys
            .Where(rel => !CoveredSubtrees.Any(t => rel.StartsWith(t, StringComparison.Ordinal)))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            strays.Count == 0,
            "pinned file(s) outside the mutated subtrees — extend MUTATE_GLOBS in scripts/mutation-ratchet.py "
                + "and CoveredSubtrees here, together:\n  " + string.Join("\n  ", strays));
    }

    /// <summary>A score outside 0-100 means the pin file was hand-edited into nonsense.</summary>
    [Fact]
    public void Pinned_scores_are_percentages()
    {
        var bad = Pins().Where(p => p.Value is < 0 or > 100).Select(p => $"{p.Key} = {p.Value}").ToList();
        Assert.True(bad.Count == 0, "pin(s) are not percentages:\n  " + string.Join("\n  ", bad));
    }
}
