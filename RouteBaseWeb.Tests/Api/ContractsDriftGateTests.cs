using RouteBaseWeb.Constants;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace RouteBaseWeb.Tests.Api;

/// <summary>
/// PI-04: the contracts drift gate, hosted by the test run. <c>scripts/check-contracts-drift.sh</c> was
/// invoked by NOTHING — no CI exists, the csproj's npm build does not call it, the Dockerfile does not —
/// so a C# DTO edit could ship with a stale committed TS mirror and every gate stayed green.
///
/// This is the same guarantee without the script's <c>dotnet build</c> (which would recurse when hosted
/// inside a build):
/// <list type="number">
/// <item>the build that produced this test assembly just rewrote <c>openapi/RouteBaseWeb.json</c> from the
///   C# DTOs, so an uncommitted diff there IS a DTO change whose document was never staged;</item>
/// <item>the TS mirror is REGENERATED from that document in-test (<c>npm run gen:contracts</c>) and diffed
///   (MED-2). The build does not run the generator, so a field add/rename committed with a fresh JSON but a
///   stale <c>openapi-types.ts</c> used to slip through both legs — the old diff only ever compared the TS
///   against its own committed self, and the alias leg below checks only type NAMES, not fields;</item>
/// <item>every schema in that document must have its flat alias in the generated <c>contracts.ts</c> — the
///   name-level leg, and the fallback when the JS toolchain can't run (npm-less CI).</item>
/// </list>
/// Skips (does not fail) outside a git checkout — the Docker build copies sources without <c>.git</c>.
/// </summary>
public class ContractsDriftGateTests
{
    // [CallerFilePath] → this file; up 2 (Api -> RouteBaseWeb.Tests) is the repo root.
    private static string RepoRoot([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private const string DocPath = "RouteBaseWeb/openapi/RouteBaseWeb.json";
    private const string GeneratedDir = "RouteBaseWeb/src/js/generated";
    private const string ProjectDir = "RouteBaseWeb";

    [Fact]
    public void BuiltOpenApiDocument_AndRegeneratedMirror_AreCommitted()
    {
        var root = RepoRoot();
        // Path.Exists, not Directory.Exists: in a git WORKTREE `.git` is a FILE
        // (a gitdir link), not a directory — Directory.Exists silently skipped the whole diff leg there.
        // A Docker source copy has no `.git` at all, so this still skips (nothing to diff) as intended.
        if (!Path.Exists(Path.Combine(root, ".git"))) return;

        // 1) The build already rewrote the document from the C# DTOs; an uncommitted diff there is a DTO
        //    change that was never staged. Against the INDEX, like the script — so "edit DTO → build →
        //    stage" is not a red test for the whole edit session. --exit-code is load-bearing: plain
        //    `git diff` returns 0 even when it prints a diff.
        var (jsonExit, jsonOut) = Git(root, "diff", "--exit-code", "--", DocPath);
        Assert.True(jsonExit == 0,
            "openapi/RouteBaseWeb.json is STALE — a C# DTO changed but the built document was not staged.\n" +
            "Fix: `dotnet build`, then stage RouteBaseWeb/openapi/RouteBaseWeb.json.\n\n" + jsonOut);

        // 2) Regenerate the TS mirror from that SAME document and diff it (MED-2). This is what closes the
        //    field-add/rename hole: the generator rewrites openapi-types.ts + contracts.ts from the fresh
        //    JSON, so a stale committed mirror shows up as a diff here. Skips only if the JS toolchain can't
        //    be launched (same spirit as the Docker skip) — the name-level leg below is then the fallback.
        // Through a shell (like the csproj's <Exec Command="npm run build">), so npm resolves the same way
        // the build's npm invocation does rather than via a bare execvp of the nvm-symlinked launcher. The
        // `command -v npm || exit 0` guard makes a node-less .git checkout (e.g. a backend-only CI) skip the
        // regen gracefully — falling back to the name-level leg — instead of failing on a missing toolchain.
        if (!TryRun(Path.Combine(root, ProjectDir), out var genExit, out var genOut,
                "/bin/sh", "-c", "command -v npm >/dev/null 2>&1 || exit 0; npm run gen:contracts"))
            return;
        Assert.True(genExit == 0, "`npm run gen:contracts` failed to regenerate the TS mirror in-test:\n" + genOut);

        var (tsExit, tsOut) = Git(root, "diff", "--exit-code", "--", GeneratedDir);
        Assert.True(tsExit == 0,
            "The generated TS mirror is STALE — a C# DTO field changed without regenerating the TS.\n" +
            "Fix: `cd RouteBaseWeb && npm run gen:contracts`, then stage src/js/generated.\n\n" + tsOut);
    }

    [Fact]
    public void EveryDocumentSchema_HasAGeneratedContractAlias()
    {
        var root = RepoRoot();
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, DocPath)));
        var schemas = doc.RootElement.GetProperty("components").GetProperty("schemas")
            .EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

        var contracts = File.ReadAllText(Path.Combine(root, GeneratedDir, "contracts.ts"));
        var aliased = Regex.Matches(contracts, @"^export type (\w+) =", RegexOptions.Multiline, RegexDefaults.MatchTimeout)
            .Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

        var missing = schemas.Except(aliased, StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToList();
        Assert.True(missing.Count == 0,
            $"contracts.ts is missing an alias for {missing.Count} document schema(s): {string.Join(", ", missing)}.\n" +
            "The OpenAPI document moved without the TS mirror — run `npm run gen:contracts`.");
    }

    private static (int ExitCode, string Output) Git(string workingDir, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output);
    }

    /// <summary>Run <paramref name="file"/> to completion. Returns false (does NOT throw) when the executable
    /// cannot be launched — an npm-less environment then skips the regen leg rather than failing spuriously.</summary>
    private static bool TryRun(string workingDir, out int exitCode, out string output, string file, params string[] args)
    {
        exitCode = -1;
        output = "";
        var psi = new ProcessStartInfo(file)
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        Process p;
        try { p = Process.Start(psi)!; }
        catch { return false; } // toolchain absent (no npm on PATH) — caller skips
        using (p)
        {
            output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit();
            exitCode = p.ExitCode;
        }
        return true;
    }
}
