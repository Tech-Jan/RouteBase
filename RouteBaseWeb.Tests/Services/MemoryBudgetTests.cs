using RouteBaseWeb.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace RouteBaseWeb.Tests.Services;

/// <summary>
/// The budget's whole reason to exist is that memory numbers used to be scattered — a default in Program.cs, a
/// const in a cache, a GC limit in the .csproj — so moving to a bigger box meant finding all of them. These
/// tests pin the one duplication that CANNOT be removed (the GC heap limit, which the runtime reads before any
/// configuration exists) and prove it cannot drift quietly.
/// </summary>
public class MemoryBudgetTests
{
    private static long MiB(int mb) => mb * 1024L * 1024L;

    private static MemoryBudget Budget() => new()
    {
        AppBudgetMB = 1000, GcHeapLimitMB = 750, TrackDecodeCacheMB = 64, CoordinateCacheMB = 48,
    };

    [Fact]
    public void ACoherentBudget_HasNothingToSay()
    {
        Assert.Empty(Budget().Validate(MiB(750)));
    }

    /// <summary>The scenario the whole design is for: appsettings raised for a bigger box, the .csproj twin
    /// forgotten. The process still runs on the old limit — and must SAY so.</summary>
    [Fact]
    public void AHeapLimitThatDriftedFromTheCsproj_IsReported()
    {
        var budget = Budget() with { GcHeapLimitMB = 1500 };   // appsettings says 1500…
        var problems = budget.Validate(MiB(750));              // …but the runtime really got 750

        Assert.Contains(problems, p => p.Contains("really 750", StringComparison.Ordinal) && p.Contains("says 1500", StringComparison.Ordinal));
    }

    /// <summary>The silent-failure case: a hex value in runtimeconfig.json is ignored, so NO limit applies and
    /// the GC sizes itself against the whole machine. That shows up as an actual limit near machine memory.</summary>
    [Fact]
    public void AHeapLimitThatNeverApplied_IsReported()
    {
        var problems = Budget().Validate(MiB(2000));   // no limit in force → the 2 GB box
        Assert.Contains(problems, p => p.Contains("really 2000", StringComparison.Ordinal));
    }

    [Fact]
    public void AHeapLimitThatEatsTheWholeBudget_IsReported()
    {
        var budget = Budget() with { AppBudgetMB = 700, GcHeapLimitMB = 750 };
        Assert.Contains(budget.Validate(MiB(750)), p => p.Contains("leaving nothing", StringComparison.Ordinal));
    }

    [Fact]
    public void CachesTakingOverHalfTheHeap_AreReported()
    {
        var budget = Budget() with { TrackDecodeCacheMB = 300, CoordinateCacheMB = 200 };
        Assert.Contains(budget.Validate(MiB(750)), p => p.Contains("over half of it", StringComparison.Ordinal));
    }

    /// <summary>The coordinate cache counts POINTS, not bytes — the budget is the only place that conversion is
    /// written, so nobody has to remember it at a call site.</summary>
    [Fact]
    public void TheCoordinateBudget_ConvertsToTheCachesOwnUnit()
    {
        var budget = Budget();                                  // 48 MiB at ~50 B/point
        Assert.Equal(48 * 1024L * 1024L / 50, budget.CoordinateCachePoints);
        Assert.Equal(112, budget.BoundedCachesMB);              // 64 + 48
    }

    /// <summary>The real appsettings.json must bind to a coherent budget — a typo'd key would otherwise fall
    /// back to defaults in silence, which is the failure mode this whole file exists to prevent.</summary>
    [Fact]
    public void TheShippedAppSettings_BindsAndIsCoherent()
    {
        var cfg = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(RepoRoot(), "RouteBaseWeb", "appsettings.json"), optional: false)
            .Build();

        var budget = cfg.GetSection(MemoryBudget.SectionName).Get<MemoryBudget>();

        Assert.NotNull(budget);
        Assert.Equal(1000, budget!.AppBudgetMB);
        Assert.Equal(750, budget.GcHeapLimitMB);
        Assert.Empty(budget.Validate(MiB(budget.GcHeapLimitMB)));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "RouteBaseWeb", "appsettings.json")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
