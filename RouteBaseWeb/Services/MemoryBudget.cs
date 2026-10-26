namespace RouteBaseWeb.Services;

/// <summary>
/// THE memory budget. One place, one unit (MiB), bound from the <c>"Memory"</c> section of appsettings.
///
/// It exists because the numbers were scattered — a default buried in Program.cs, a <c>const</c> inside a
/// cache class, a GC limit in the .csproj — each in a different unit, none of them mentioning the others. Move
/// the app to a bigger box and you would fix one and never find the rest.
///
/// Every cache below derives its size from here. Nothing sizes itself.
///
/// THE ONE THING THIS FILE CANNOT OWN is the GC heap limit: the runtime reads it from runtimeconfig.json
/// (emitted by the .csproj) *before* any of this configuration is loaded, so it must be declared there too.
/// That duplication is unavoidable — so <see cref="Validate"/> asserts the runtime's ACTUAL limit matches
/// <see cref="GcHeapLimitMB"/> and shouts if they have drifted apart. The drift cannot hide.
/// </summary>
public sealed record MemoryBudget
{
    public const string SectionName = "Memory";

    /// <summary>What the whole process is allowed to be. Not enforced by itself — it is the number the rest are
    /// checked against, so an over-committed budget is caught at startup rather than by the OOM killer.</summary>
    public int AppBudgetMB { get; init; } = 1000;

    /// <summary>MUST mirror <c>System.GC.HeapHardLimit</c> in the .csproj (in MiB). Declared twice by necessity
    /// — see the class remarks — and cross-checked at startup, so the two can never quietly disagree.</summary>
    public int GcHeapLimitMB { get; init; } = 750;

    /// <summary>Decoded tracks, strict-LRU (TrackReaderCache). A miss costs a re-decode (~130 ms).</summary>
    public int TrackDecodeCacheMB { get; init; } = 64;

    /// <summary>GPS coordinate payloads (GpsCoordinateCache). A miss costs a re-query.</summary>
    public int CoordinateCacheMB { get; init; } = 48;

    /// <summary>Rough bytes per cached GPS point, incl. object overhead — the coordinate cache counts POINTS,
    /// so this is what turns its MiB budget into its native unit. Kept here so the conversion is stated once.</summary>
    public const int BytesPerCoordinatePoint = 50;

    /// <summary>The coordinate cache's SizeLimit, in its own unit.</summary>
    public long CoordinateCachePoints => CoordinateCacheMB * 1024L * 1024L / BytesPerCoordinatePoint;

    /// <summary>What the size-bounded caches may hold between them.</summary>
    public int BoundedCachesMB => TrackDecodeCacheMB + CoordinateCacheMB;

    /// <summary>
    /// Problems worth saying out loud at startup — never a throw: a misjudged budget should degrade the app,
    /// not refuse to boot it. Empty = the budget is coherent.
    ///
    /// <paramref name="actualHeapLimitBytes"/> is <c>GC.GetGCMemoryInfo().TotalAvailableMemoryBytes</c>: the
    /// hard limit when one is in force, otherwise the container's or the machine's memory. That is exactly the
    /// signal that catches a HeapHardLimit which silently failed to apply (a hex value in runtimeconfig.json is
    /// ignored — it is the env-var form) or a .csproj that drifted from this file.
    /// </summary>
    public IReadOnlyList<string> Validate(long actualHeapLimitBytes)
    {
        var problems = new List<string>();
        int actualMB = (int)(actualHeapLimitBytes / 1024 / 1024);

        // 10% tolerance: the runtime rounds, and a container limit is rarely a round MiB.
        if (Math.Abs(actualMB - GcHeapLimitMB) > GcHeapLimitMB / 10)
            problems.Add(
                $"the GC heap limit is really {actualMB} MiB, but Memory:GcHeapLimitMB says {GcHeapLimitMB}. " +
                "Either System.GC.HeapHardLimit in RouteBaseWeb.csproj did not apply (runtimeconfig.json needs a " +
                "NUMBER — a hex value is silently ignored there), or the two have drifted apart.");

        if (GcHeapLimitMB >= AppBudgetMB)
            problems.Add(
                $"the GC heap alone may reach {GcHeapLimitMB} MiB of a {AppBudgetMB} MiB budget, leaving nothing " +
                "for the runtime, native allocations or SQLite. Keep the heap limit well under the budget.");

        if (BoundedCachesMB > GcHeapLimitMB / 2)
            problems.Add(
                $"the caches reserve {BoundedCachesMB} MiB of a {GcHeapLimitMB} MiB heap — over half of it — " +
                "leaving little room for request work and the editor's replay.");

        return problems;
    }

    /// <summary>The budget as one line, for the startup log. Reports what the process ACTUALLY got, not what we
    /// asked for, so a limit that failed to apply is visible rather than assumed.</summary>
    public string Describe(long actualHeapLimitBytes) =>
        $"app budget {AppBudgetMB} MiB · GC heap limit {actualHeapLimitBytes / 1024 / 1024} MiB (actual) · " +
        $"decoded-track cache {TrackDecodeCacheMB} MiB · coordinate cache {CoordinateCacheMB} MiB " +
        $"({CoordinateCachePoints:N0} pts). " +
        "The shared IMemoryCache (segments, activity slices, tours, race courses, tile indexes) is bounded by " +
        "TTL, not by size — every entry carries an expiration, but its peak is not declared here.";
}
