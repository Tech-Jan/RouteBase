namespace RouteBaseWeb.Tests.Services.Photos;

/// <summary>
/// Locates the three real §11 EXIF sample photos the reader/orchestrator tests exercise. The binaries are
/// NOT committed (they are personal photos — committing them is a privacy/licensing decision parked for a
/// human, see REPORT 2026-07-22). Opt them in by setting <c>ROUTEBASE_PHOTO_FIXTURES</c> to a directory
/// holding all three, or by dropping them into <c>TestData/Photos</c> beside the test assembly.
///
/// Replaces a hardcoded absolute path into the (pre-rename, off-limits) main checkout that had silently
/// gone stale — every EXIF test no-op'd green because the directory no longer existed. <see cref="Available"/>
/// gates the guarded tests; <c>PhotoExifReaderTests.FixturesDir_WhenPresent_ContainsEveryNamedSample</c>
/// fails loudly if a configured directory is missing a sample, so a stale path can never silently skip again.
/// </summary>
internal static class PhotoFixtures
{
    public const string PixelFile    = "PXL_20260623_193035174.jpg";
    public const string WhatsAppFile = "IMG-20260627-WA0003.jpeg";
    public const string SignalFile   = "signal-2026-07-06-21-10-02-518.jpg";

    public static readonly string[] AllFiles = { PixelFile, WhatsAppFile, SignalFile };

    /// <summary>Configured fixtures directory, or the in-repo default beside the test assembly.</summary>
    public static string Dir =>
        Environment.GetEnvironmentVariable("ROUTEBASE_PHOTO_FIXTURES")
        ?? Path.Combine(AppContext.BaseDirectory, "TestData", "Photos");

    /// <summary>True only when a fixtures directory is actually present (fixtures opted in).</summary>
    public static bool Available => Directory.Exists(Dir);

    public static string PathOf(string file) => Path.Combine(Dir, file);
}
