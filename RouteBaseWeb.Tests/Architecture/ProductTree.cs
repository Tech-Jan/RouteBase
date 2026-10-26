using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;

namespace RouteBaseWeb.Tests.Architecture;

/// <summary>
/// The shared filesystem walk under every architecture gate. Extracted (rule of three) when the
/// type-quality pass wanted a third copy of <c>ProductRoot()</c>; the pre-existing gates
/// (<see cref="BackendConventionTests"/>, <see cref="LayeringTests"/> siblings) staying green on the
/// same walk is the proof the extraction changed nothing.
/// </summary>
internal static class ProductTree
{
    /// <summary>Lines that are pure comments/doc — a rule NAMED in a comment is not a violation of it.</summary>
    internal static bool IsComment(string trimmed) =>
        trimmed.StartsWith("//", StringComparison.Ordinal)
        || trimmed.StartsWith("*", StringComparison.Ordinal)
        || trimmed.StartsWith("/*", StringComparison.Ordinal);

    internal static bool NotBuildOutput(string f) =>
        !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    /// <summary>Walks up from the test assembly to the RouteBaseWeb product project directory.</summary>
    internal static string ProductRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "RouteBaseWeb");
            if (Directory.Exists(Path.Combine(candidate, "Services")))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"could not locate the RouteBaseWeb project from {AppContext.BaseDirectory}");
    }

    /// <summary>Repo root via this source file's own location (Architecture → Tests → root) — the
    /// convention <c>ContractsDriftGateTests</c> established; works in worktrees where `.git` is a file.</summary>
    internal static string RepoRoot([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    internal static List<string> ProductCsFiles(out string productRoot)
    {
        productRoot = ProductRoot();
        return Directory.EnumerateFiles(productRoot, "*.cs", SearchOption.AllDirectories)
            .Where(NotBuildOutput)
            .ToList();
    }

    internal static List<string> ProductCshtmlFiles(out string productRoot)
    {
        productRoot = ProductRoot();
        return Directory.EnumerateFiles(productRoot, "*.cshtml", SearchOption.AllDirectories)
            .Where(NotBuildOutput)
            .ToList();
    }
}
