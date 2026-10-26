using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace RouteBaseWeb.Tests.Architecture;

/// <summary>
/// C# layering gate — the domain and service layers must not depend UP on the web layer.
///
/// Companion to the frontend's map-engine rule (eslint <c>no-restricted-imports</c> over
/// <c>src/js/core/**</c>) and the Android one (<c>LayeringTest.kt</c>). All three enforce the same
/// idea and exist for the same reason: import direction has a crisp, objective definition, so it
/// belongs in a gate — unlike file length, which is a proxy and now only fires on runaways.
///
/// The layers, bottom-up:
///   Models/      DTOs, entities, EF context — the shapes
///   Services/    business logic, persistence orchestration
///   ---- the line this test defends ----
///   Api/         controllers: HTTP binding, status codes, request/response wiring
///   Pages/       Razor PageModels: the same presentation tier as controllers
///
/// <c>Api/Pages → Services → Models</c> is the only legal direction. A service that references a
/// controller or a Razor PageModel has put HTTP/presentation concerns inside business logic: it becomes
/// untestable without the MVC stack, and the logic can no longer be reused from a queue handler or a
/// background worker (which is exactly how this codebase runs half its work — see <c>IOperationHandler</c>).
///
/// Deliberately NO exemption list. Both directions were measured clean on 2026-07-31 when this was
/// written, so the gate went in green; the first exemption added here is the point at which it stops
/// meaning anything. If you need something from a controller, the shape belongs in Models/ or the
/// behaviour belongs in Services/.
/// </summary>
public class LayeringTests
{
    /// <summary>Directories that must never reference the Api or Pages namespace, and why each matters.</summary>
    private static readonly string[] LowerLayers = ["Services", "Models"];

    /// <summary>Presentation-tier namespaces a lower layer must never reference — controllers and Razor PageModels.</summary>
    private static readonly string[] ForbiddenNamespaces = ["RouteBaseWeb.Api", "RouteBaseWeb.Pages"];

    [Fact]
    public void Services_and_Models_never_reference_the_Api_or_Pages_layer()
    {
        var projectRoot = FindProjectRoot();

        var files = LowerLayers
            .Select(dir => Path.Combine(projectRoot, dir))
            .Where(Directory.Exists)
            .SelectMany(dir => Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();

        // Vacuity guard: a wrong root would make every assertion below pass without reading anything.
        Assert.True(
            files.Count > 100,
            $"layering walk found only {files.Count} files under {string.Join(" + ", LowerLayers)} — "
                + $"the project root is wrong (resolved to '{projectRoot}'). Fix the walk, do not weaken the test.");

        var violations = new List<string>();
        foreach (var file in files)
        {
            var lineNumber = 0;
            foreach (var line in File.ReadLines(file))
            {
                lineNumber++;
                var trimmed = line.TrimStart();

                // Comments explaining the boundary are fine — only real references count.
                if (trimmed.StartsWith("//", StringComparison.Ordinal)
                    || trimmed.StartsWith("*", StringComparison.Ordinal)
                    || trimmed.StartsWith("/*", StringComparison.Ordinal))
                {
                    continue;
                }

                // A namespace DECLARATION is not a dependency — it says what this file IS, not what it
                // depends on. `Models/Requests/StatisticsRequest.cs` declares `namespace RouteBaseWeb.Api`
                // (a file-location/namespace mismatch, reported separately 2026-07-31); that is a tidiness
                // problem for a human to decide on, not an illegal edge, and this gate must not conflate
                // the two or it will be argued with and then weakened.
                if (trimmed.StartsWith("namespace ", StringComparison.Ordinal))
                {
                    continue;
                }

                var hit = ForbiddenNamespaces.FirstOrDefault(ns => trimmed.Contains(ns, StringComparison.Ordinal));
                if (hit is not null)
                {
                    var rel = Path.GetRelativePath(projectRoot, file);
                    violations.Add($"{rel}:{lineNumber}: [{hit}] {trimmed}");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "LAYERING VIOLATION — a lower layer references the Api (controller) or Pages (Razor) layer. "
                + "Api/Pages -> Services -> Models is the only legal direction; move the shape to Models/ or the "
                + "behaviour to Services/. Do NOT add an exemption:\n"
                + string.Join("\n", violations));
    }

    /// <summary>
    /// The gate lands green on today's source (no lower-layer file references Pages), so the real scan can
    /// never demonstrate the Pages arm actually fires. This pins the predicate directly against synthetic
    /// lines: it proves a <c>RouteBaseWeb.Pages</c> reference IS flagged (arm not vacuous), that a
    /// sub-namespace is caught by the substring, and that the comment / namespace-declaration skips still hold.
    /// </summary>
    [Fact]
    public void Forbidden_predicate_flags_Pages_references_but_not_comments_or_declarations()
    {
        static bool IsViolation(string line)
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("//", StringComparison.Ordinal)
                || trimmed.StartsWith("*", StringComparison.Ordinal)
                || trimmed.StartsWith("/*", StringComparison.Ordinal))
            {
                return false;
            }
            if (trimmed.StartsWith("namespace ", StringComparison.Ordinal))
            {
                return false;
            }
            return ForbiddenNamespaces.Any(ns => trimmed.Contains(ns, StringComparison.Ordinal));
        }

        Assert.True(IsViolation("    var x = RouteBaseWeb.Pages.Foo;"));
        Assert.True(IsViolation("using RouteBaseWeb.Pages.Auth;"));   // sub-namespace caught by substring
        Assert.True(IsViolation("    var y = RouteBaseWeb.Api.Bar;")); // the original arm still fires
        Assert.False(IsViolation("// see RouteBaseWeb.Pages for the presentation tier"));
        Assert.False(IsViolation("namespace RouteBaseWeb.Pages.Segments;"));
        Assert.False(IsViolation("    var z = RouteBaseWeb.Services.Thing;"));
    }

    /// <summary>Walks up from the test assembly to the RouteBaseWeb project directory.</summary>
    private static string FindProjectRoot()
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
}
