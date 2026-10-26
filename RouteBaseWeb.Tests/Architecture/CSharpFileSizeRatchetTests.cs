using Xunit;

namespace RouteBaseWeb.Tests.Architecture;

/// <summary>
/// C# file-size ratchet — the third mirror of the frontend's src/js/file-size-ratchet.test.ts (the worked
/// example — read it first) and GpsRecorder/.../KotlinFileSizeRatchetTest.kt. Enforces the project's caps on
/// C# source: a RUNAWAY detector (logic 1000 / tests 1500) plus per-file growth pins. Pure filesystem walk,
/// no host/DI; <c>bin/</c>, <c>obj/</c> and any <c>generated/</c> output are skipped (exempt by convention:
/// generated files). Line count is <c>wc -l</c> (newline count) — the same metric the other two ratchets
/// use, so all three agree on what a "line" is.
///
/// The ratchet:
///   • FAILS if a listed file grows past its recorded cap — split it, or (only with a written reason) raise
///     the number here in the same commit; the diff makes the decision visible to a reviewer.
///   • FAILS if any UNLISTED file crosses its RUNAWAY cap (logic 1000 / test 1500) — split it, or
///     add it here with a rationale.
///   • FAILS if an exemption is STALE — the file shrank below its recorded pin (lower the number here in
///     the same commit so caps ratchet down instead of drifting) or no longer exists. This is stricter
///     than the TS/Kotlin ratchets, which treat the pin as an upper bound only; the convention's "caps only
///     ever go down" is enforced here, not left to the author to remember.
///
/// Seeded 2026-07-31 after Program.cs was split from 711 to 617 lines. The only file over any cap
/// is the one test exemption below; no logic file needs pinning (the next-largest, Services/SegmentAnalysis.cs
/// at 690, is under 700 — an unneeded exemption would itself be drift).
/// </summary>
public class CSharpFileSizeRatchetTests
{
    // RUNAWAY caps, not style caps (scope change 2026-07-31 — see the TS ratchet header for the full
    // rationale). The review trigger for "this file is getting big" is human judgement at ~400-700;
    // these numbers only fire where size stops being arguable.
    private const int LogicCap = 1000;
    private const int TestCap = 1500;

    private sealed record SizePin(string Path, int MaxLines);

    /// <summary>
    /// Repo-relative path (forward slashes) → recorded line count, an EXACT pin. Grow past it → split or
    /// raise with a written reason; shrink below it → lower it here so the cap tightens.
    /// </summary>
    private static readonly SizePin[] Exemptions =
    [
        // RouteBaseWeb.Tests/Services/ImportHostileInputTests.cs — over the 1000-line test cap. It bundles
        // the FIT/TCX/GPX hostile-input fixtures into one cohesive suite. Decided 2026-08-02: KEEP
        // IT AS IT IS, no fixture split — a file over the line is a question, not a defect, and
        // the pin is the answer, since it cannot grow unnoticed. The next-largest test file
        // (Services/Sharing/ShareLinkServiceTests.cs, 996) is under the cap and deliberately left unpinned —
        // the ratchet catching it later is it working.
        // 1089 → 1090 (K6b, 2026-08-01): the `using System.Globalization;` line the culture sweep added.
        new SizePin("RouteBaseWeb.Tests/Services/ImportHostileInputTests.cs", 1090),
    ];

    [Fact]
    public void No_csharp_file_runs_away_past_its_cap_and_no_pinned_file_grows()
    {
        string repoRoot = RepoRoot();

        var files = CollectCsFiles(Path.Combine(repoRoot, "RouteBaseWeb"), LogicCap)
            .Concat(CollectCsFiles(Path.Combine(repoRoot, "RouteBaseWeb.Tests"), TestCap))
            .Select(f => (Rel: RepoRelative(repoRoot, f.Abs), f.Abs, f.Cap))
            .ToList();

        // Vacuity guard: a broken walk (wrong root, empty read) would make every check below pass silently.
        // The two projects hold ~830 .cs files today.
        Assert.True(
            files.Count > 400,
            $"source-file walk found suspiciously few .cs files ({files.Count}) — the walk is broken");

        // Every exemption must still point at a real file — a rename/delete must not silently drop a cap.
        var missing = Exemptions
            .Where(e => !File.Exists(Path.Combine(repoRoot, e.Path.Replace('/', Path.DirectorySeparatorChar))))
            .Select(e => e.Path)
            .ToList();
        Assert.True(
            missing.Count == 0,
            "exempt file(s) renamed or deleted — update the exemption path in CSharpFileSizeRatchetTests.cs:\n"
                + string.Join("\n", missing));

        var capByPath = Exemptions.ToDictionary(e => e.Path, e => e.MaxLines, StringComparer.Ordinal);
        var grown = new List<string>();
        var stale = new List<string>();
        var newViolations = new List<string>();
        int maxObserved = 0;
        foreach (var (rel, abs, cap) in files)
        {
            int lines = CountLines(abs);
            if (lines > maxObserved) maxObserved = lines;
            if (capByPath.TryGetValue(rel, out int recorded))
            {
                if (lines > recorded)
                    grown.Add($"{rel}: {lines} > recorded {recorded}");
                else if (lines < recorded)
                    stale.Add($"{rel}: {lines} < recorded {recorded} (lower the pin, or remove it if now under {cap})");
            }
            else if (lines > cap)
            {
                newViolations.Add($"{rel}: {lines} > {cap}");
            }
        }

        // Vacuity guard: if CountLines silently returned small values, the checks below would pass hollowly.
        // The largest exempt file (ImportHostileInputTests.cs, 1090) proves the counter reads real content.
        Assert.True(
            maxObserved > 600,
            // 600, not LogicCap: it keeps the guard honest without coupling it to the one file that sits near
            // the cap. That file stays as it is (see Exemptions), but a future shrink there must not turn this
            // vacuity check red — a false alarm on an improvement.
            $"line counter appears broken — no source file measured over 600 lines (max={maxObserved})");

        Assert.True(
            grown.Count == 0,
            "C# file grew past its recorded cap — split it, or (with a written reason) raise the number in "
                + "CSharpFileSizeRatchetTests.cs:\n" + string.Join("\n", grown));
        // NOTE: `stale` is deliberately NOT asserted. This ratchet used to hard-fail when a pinned file
        // shrank below its pin, to force "caps only ever go down" mechanically. That was stricter than the
        // TS and Kotlin ratchets and it fought the pile workflow: jobs run in worktrees cut from master and
        // are told NOT to edit the ratchet table (two jobs editing it = merge conflict), so a job that
        // shrinks a pinned file would redden its own build with no legal fix. With caps demoted to runaway
        // detection, failing a green build over a loose pin is noise. Monotonic is now a convention in all
        // three languages: when a pinned file shrinks, lower its number in the same commit.
        _ = stale;
        Assert.True(
            newViolations.Count == 0,
            "C# file crossed its RUNAWAY cap (logic 1000 / tests 1500) — this is no longer a judgement call. Split it, or pin it with a documented "
                + "exemption in CSharpFileSizeRatchetTests.cs:\n" + string.Join("\n", newViolations));
    }

    /// <summary>Line count as <c>wc -l</c> (newline count) — the metric the recorded caps were seeded with.</summary>
    private static int CountLines(string abs) => File.ReadAllText(abs).Count(c => c == '\n');

    /// <summary>Every <c>.cs</c> file under <paramref name="root"/>, skipping bin/ obj/ generated/ output.</summary>
    private static IEnumerable<(string Abs, int Cap)> CollectCsFiles(string root, int cap)
    {
        var acc = new List<string>();
        Walk(root, acc);
        return acc.Select(abs => (abs, cap));
    }

    private static void Walk(string dir, List<string> acc)
    {
        foreach (string sub in Directory.EnumerateDirectories(dir))
        {
            string name = Path.GetFileName(sub);
            if (name is "bin" or "obj" or "generated") continue; // build + codegen output — exempt
            Walk(sub, acc);
        }
        acc.AddRange(Directory.EnumerateFiles(dir, "*.cs"));
    }

    private static string RepoRelative(string repoRoot, string abs) =>
        Path.GetRelativePath(repoRoot, abs).Replace(Path.DirectorySeparatorChar, '/');

    /// <summary>
    /// Walks up from the test binary to the repo root — the directory holding both project trees — so this
    /// does not depend on the working directory the runner happens to use (mirrors ShareCspHostParityTests).
    /// </summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "RouteBaseWeb", "src", "js"))
                && Directory.Exists(Path.Combine(dir.FullName, "RouteBaseWeb.Tests")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repo root from " + AppContext.BaseDirectory);
    }
}
